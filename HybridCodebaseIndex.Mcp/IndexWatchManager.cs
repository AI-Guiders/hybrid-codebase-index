using System.Collections.Concurrent;
using System.Threading.Channels;
using HybridCodebaseIndex.Core;

namespace HybridCodebaseIndex.Mcp;

internal sealed class IndexWatchManager : IDisposable
{
    private sealed record WatchKey(string WorkspaceRoot, string? SolutionPath)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(SolutionPath) ? WorkspaceRoot : $"{WorkspaceRoot} | {SolutionPath}";
    }

    private sealed class WatcherState : IDisposable
    {
        private readonly CodebaseIndexService _service;
        private readonly string _workspaceRoot;
        private readonly string? _solutionPath;
        private readonly int _debounceMs;
        private readonly Channel<int> _poke;
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;

        private readonly FileSystemWatcher _fsw;

        public WatcherState(CodebaseIndexService service, string workspaceRoot, string? solutionPath, int debounceMs)
        {
            _service = service;
            _workspaceRoot = workspaceRoot;
            _solutionPath = solutionPath;
            _debounceMs = debounceMs;

            _cts = new CancellationTokenSource();
            _poke = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

            _fsw = new FileSystemWatcher(_workspaceRoot)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.CreationTime,
            };

            _fsw.Changed += OnAny;
            _fsw.Created += OnAny;
            _fsw.Deleted += OnAny;
            _fsw.Renamed += OnAny;
            _fsw.Error += OnError;

            _loop = Task.Run(LoopAsync, _cts.Token);
        }

        private void OnAny(object sender, FileSystemEventArgs e)
        {
            // Best-effort: ignore events from our own index directory.
            // Avoid heavy path matching; a false-positive poke is ok because reindex is incremental.
            _poke.Writer.TryWrite(0);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            // FileSystemWatcher can drop events when buffer overflows; trigger a catch-up reindex.
            _poke.Writer.TryWrite(0);
        }

        private async Task LoopAsync()
        {
            var ct = _cts.Token;
            var nextDelay = Task.Delay(Timeout.Infinite, ct);
            var hasPending = false;

            while (!ct.IsCancellationRequested)
            {
                var read = _poke.Reader.ReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(read, nextDelay).ConfigureAwait(false);

                if (completed == read)
                {
                    // Drain bursts quickly; debounce handles the actual batching.
                    hasPending = true;
                    _ = await read.ConfigureAwait(false);
                    nextDelay = Task.Delay(_debounceMs, ct);
                    continue;
                }

                // Debounce window elapsed
                if (!hasPending)
                {
                    nextDelay = Task.Delay(Timeout.Infinite, ct);
                    continue;
                }

                hasPending = false;
                nextDelay = Task.Delay(Timeout.Infinite, ct);

                try
                {
                    // Single-flight by key: serialize inside this watcher loop.
                    await _service.FullReindexAsync(_workspaceRoot, _solutionPath, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Best-effort background task; status tool will surface last error.
                }
            }
        }

        public void Dispose()
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Changed -= OnAny;
            _fsw.Created -= OnAny;
            _fsw.Deleted -= OnAny;
            _fsw.Renamed -= OnAny;
            _fsw.Error -= OnError;
            _fsw.Dispose();

            _cts.Cancel();
            _poke.Writer.TryComplete();
            try { _loop.GetAwaiter().GetResult(); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    private readonly CodebaseIndexService _service;
    private readonly ConcurrentDictionary<WatchKey, WatcherState> _watchers = new();

    public IndexWatchManager(CodebaseIndexService service)
    {
        _service = service;
    }

    public void SetEnabled(string workspaceRoot, string? solutionPath, bool enabled, int debounceMs)
    {
        var root = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var key = new WatchKey(root, string.IsNullOrWhiteSpace(solutionPath) ? null : solutionPath.Trim());

        if (!enabled)
        {
            if (_watchers.TryRemove(key, out var st))
                st.Dispose();
            return;
        }

        _watchers.AddOrUpdate(
            key,
            static (k, arg) => new WatcherState(arg.service, arg.root, arg.solutionPath, arg.debounceMs),
            static (k, existing, arg) =>
            {
                // Recreate to apply debounce changes cleanly.
                existing.Dispose();
                return new WatcherState(arg.service, arg.root, arg.solutionPath, arg.debounceMs);
            },
            (service: _service, root, solutionPath: key.SolutionPath, debounceMs));
    }

    public void Dispose()
    {
        foreach (var kv in _watchers)
        {
            if (_watchers.TryRemove(kv.Key, out var st))
                st.Dispose();
        }
    }
}

