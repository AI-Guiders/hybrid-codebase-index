using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace HybridCodebaseIndex.Core;

internal static partial class SqliteFtsIndex
{
    // Bump when on-disk schema changes in a non-backward-compatible way.
    internal const int FormatVersion = 2;

    internal static string ResolveDatabasePath(string workspaceRoot, string indexDirectoryRelative)
    {
        // Back-compat note:
        // - Read operations can fall back to legacy location.
        // - Write operations (reindex) always go to the requested location.
        // This helper keeps old callsites working by using the "read" resolution.
        return ResolveDatabasePathForRead(workspaceRoot, indexDirectoryRelative);
    }

    internal static string ResolveDatabasePathForRead(string workspaceRoot, string indexDirectoryRelative)
    {
        var root = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var requestedDir = Path.Combine(root, indexDirectoryRelative.TrimStart(Path.DirectorySeparatorChar, '/'));
        var requestedDb = Path.Combine(requestedDir, $"codebase-index-v{FormatVersion}.sqlite");
        if (File.Exists(requestedDb))
            return requestedDb;

        var legacyDb = GetLegacyDbPath(root);
        if (File.Exists(legacyDb))
            return legacyDb;

        return requestedDb;
    }

    internal static string ResolveDatabasePathForWrite(string workspaceRoot, string indexDirectoryRelative)
    {
        var root = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var requestedDir = Path.Combine(root, indexDirectoryRelative.TrimStart(Path.DirectorySeparatorChar, '/'));
        Directory.CreateDirectory(requestedDir);

        // Best-effort migrate settings.toml if legacy exists and new doesn't.
        TryMigrateSettingsToml(root, requestedDir);

        return Path.Combine(requestedDir, $"codebase-index-v{FormatVersion}.sqlite");
    }

    private static string GetLegacyDbPath(string workspaceRootNormalized)
    {
        var legacyDir = Path.Combine(workspaceRootNormalized, ".cascade-ide", "hybrid-codebase-index");
        return Path.Combine(legacyDir, $"codebase-index-v{FormatVersion}.sqlite");
    }

    private static void TryMigrateSettingsToml(string workspaceRootNormalized, string requestedDir)
    {
        try
        {
            var newSettings = Path.Combine(requestedDir, "settings.toml");
            if (File.Exists(newSettings))
                return;

            var legacySettings = Path.Combine(workspaceRootNormalized, ".cascade-ide", "hybrid-codebase-index", "settings.toml");
            if (!File.Exists(legacySettings))
                return;

            File.Copy(legacySettings, newSettings);
        }
        catch
        {
            // best-effort
        }
    }

    internal static Task<ReindexSummary> FullRebuildAsync(
        string workspaceRoot,
        string dbPath,
        CancellationToken cancellationToken)
        => Task.Run(() => FullRebuild(workspaceRoot, dbPath, cancellationToken), cancellationToken);

    internal static Task<ReindexSummary> ReindexIncrementalAsync(
        string workspaceRoot,
        string dbPath,
        CancellationToken cancellationToken)
        => Task.Run(() => ReindexIncremental(workspaceRoot, dbPath, cancellationToken), cancellationToken);

    private static ReindexSummary FullRebuild(string workspaceRoot, string dbPath, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));

        var filesIndexed = 0;
        var skippedLarge = 0;
        var skippedBinary = 0;
        var skippedExcluded = 0;
        var skippedSample = new List<SkippedPath>(capacity: 64);
        var skippedReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skippedTopPathPrefixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        conn.Open();

        // Root cause fix for Windows file-locking: do NOT replace/rename the DB file.
        // Rebuild in-place by swapping tables inside the same SQLite file (WAL allows concurrent readers).
        EnsureMetaTable(conn);

        try
        {
            UpsertMeta(conn, "reindex_state", "running");
            UpsertMeta(conn, "reindex_started_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            var settings = IndexSettings.TryLoadFromIndexDirectory(Path.GetDirectoryName(dbPath)!);
            var extensions = settings.GetEffectiveExtensions();
            var maxBytes = settings.GetEffectiveMaxIndexedFileBytes();
            var chunkLines = settings.GetEffectiveChunkLines();
            var overlapLines = settings.GetEffectiveChunkOverlapLines();
            var probeBytes = settings.GetEffectiveBinaryProbeBytes();

            // Prepare a fresh FTS table for the new build.
            Exec(conn, "DROP TABLE IF EXISTS chunks_new;");
            Exec(conn, """
                CREATE VIRTUAL TABLE chunks_new USING fts5(
                  path,
                  extension UNINDEXED,
                  line_start UNINDEXED,
                  line_end UNINDEXED,
                  body,
                  tokenize='unicode61 remove_diacritics 1'
                );
                """);

            using var tx = conn.BeginTransaction();
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO chunks_new(path, extension, line_start, line_end, body)
                VALUES ($path, $ext, $ls, $le, $body);
                """;

            var roots = new List<string>(capacity: 1 + settings.ExtraIncludeRoots.Count) { workspaceRoot };
            foreach (var extra in settings.ExtraIncludeRoots)
            {
                var p = Path.GetFullPath(Path.Combine(workspaceRoot, extra));
                if (Directory.Exists(p))
                    roots.Add(p);
            }

            var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

            var excludeRootFullPaths = new List<string>(settings.ExcludeRoots.Count);
            foreach (var rel in settings.ExcludeRoots)
            {
                var p = rel?.Trim();
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                if (Path.IsPathRooted(p) || p.Contains("..", StringComparison.Ordinal))
                    continue;
                var abs = Path.GetFullPath(Path.Combine(workspaceRoot, p));
                if (Directory.Exists(abs))
                    excludeRootFullPaths.Add(abs.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            }

            var candidates = new List<string>(capacity: 8192);
            foreach (var root in roots)
                candidates.AddRange(WorkspaceScanner.EnumerateIndexableFiles(root, extSet, settings.ExcludePathSegments, excludeRootFullPaths));

            var gitIgnore = GitIgnoreRules.TryLoad(workspaceRoot, settings.IgnoreFiles);

            foreach (var absolute in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (WorkspaceScanner.ShouldExcludePath(absolute, settings.ExcludePathSegments))
                {
                    skippedExcluded++;
                    AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, WorkspaceScanner.RelativePath(workspaceRoot, absolute), "denylist");
                    continue;
                }
            }

            foreach (var absolute in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (WorkspaceScanner.ShouldExcludePath(absolute, settings.ExcludePathSegments))
                    continue;

                var rel = WorkspaceScanner.RelativePath(workspaceRoot, absolute).Replace("\\", "/", StringComparison.Ordinal);
                if (GitIgnoreRules.IsIgnored(gitIgnore, rel))
                {
                    skippedExcluded++;
                    AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, rel, "gitignore");
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(absolute);
                    if (info.Length > maxBytes)
                    {
                        skippedLarge++;
                        AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, rel, "too_large");
                        continue;
                    }
                }
                catch
                {
                    skippedExcluded++;
                    AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, rel, "io_error");
                    continue;
                }

                using var fs = info.OpenRead();
                var probeSize = (int)Math.Min(probeBytes, info.Length);
                var probe = new byte[probeSize];
                var read = fs.ReadAtLeast(probe.AsSpan(0, probeSize), probeSize, throwOnEndOfStream: false);
                if (WorkspaceScanner.LooksBinary(probe.AsSpan(0, read)))
                {
                    skippedBinary++;
                    AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, rel, "binary");
                    continue;
                }

                fs.Position = 0;
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();

                var ext = Path.GetExtension(absolute);

                var chunks = WorkspaceScanner.ChunkByLines(text, chunkLines, overlapLines);
                var anyChunk = false;
                foreach (var (lineStart, lineEnd, body) in chunks)
                {
                    insert.Parameters.Clear();
                    insert.Parameters.AddWithValue("$path", rel);
                    insert.Parameters.AddWithValue("$ext", ext);
                    insert.Parameters.AddWithValue("$ls", lineStart);
                    insert.Parameters.AddWithValue("$le", lineEnd);
                    insert.Parameters.AddWithValue("$body", body);
                    insert.ExecuteNonQuery();
                    anyChunk = true;
                }

                if (anyChunk)
                    filesIndexed++;
            }

            tx.Commit();

            // Swap tables atomically.
            using (var swapTx = conn.BeginTransaction())
            {
                // Keep it simple: run DDL under a single transaction object, avoid manual BEGIN/COMMIT SQL.
                Exec(conn, "DROP TABLE IF EXISTS chunks_old;", swapTx);
                try
                {
                    Exec(conn, "ALTER TABLE chunks RENAME TO chunks_old;", swapTx);
                }
                catch (SqliteException)
                {
                    // First build: chunks doesn't exist yet.
                }

                Exec(conn, "ALTER TABLE chunks_new RENAME TO chunks;", swapTx);
                Exec(conn, "DROP TABLE IF EXISTS chunks_old;", swapTx);
                swapTx.Commit();
            }

            UpsertMeta(conn, "format_version", FormatVersion.ToString(CultureInfo.InvariantCulture));
            UpsertMeta(conn, "indexed_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            UpsertMeta(conn, "workspace_root", workspaceRoot);

            // Clear last error on success.
            UpsertMeta(conn, "reindex_error", "");
            UpsertMeta(conn, "reindex_error_at", "");

            UpsertMeta(conn, "reindex_state", "idle");
            UpsertMeta(conn, "reindex_started_at", "");
        }
        catch (Exception ex)
        {
            // Best-effort: store the last failure so status can surface it.
            try
            {
                UpsertMeta(conn, "reindex_state", "error");
                UpsertMeta(conn, "reindex_error", ex.GetType().Name + ": " + ex.Message);
                UpsertMeta(conn, "reindex_error_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                // keep started_at as-is
            }
            catch
            {
                // ignore
            }

            throw;
        }

        sw.Stop();

        return new ReindexSummary(
            FormatVersion,
            dbPath,
            filesIndexed,
            skippedLarge,
            skippedBinary,
            skippedExcluded,
            skippedReasonCounts,
            TopPrefixes(skippedTopPathPrefixes),
            skippedSample,
            sw.Elapsed);
    }

    private static ReindexSummary ReindexIncremental(string workspaceRoot, string dbPath, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));

        var filesIndexed = 0;
        var skippedLarge = 0;
        var skippedBinary = 0;
        var skippedExcluded = 0;
        var skippedSample = new List<SkippedPath>(capacity: 64);
        var skippedReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skippedTopPathPrefixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        conn.Open();

        EnsureMetaTable(conn);
        EnsureChunksTable(conn);
        EnsureFileStateTable(conn);

        try
        {
            UpsertMeta(conn, "reindex_state", "running");
            UpsertMeta(conn, "reindex_started_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            var settings = IndexSettings.TryLoadFromIndexDirectory(Path.GetDirectoryName(dbPath)!);
            var extensions = settings.GetEffectiveExtensions();
            var maxBytes = settings.GetEffectiveMaxIndexedFileBytes();
            var chunkLines = settings.GetEffectiveChunkLines();
            var overlapLines = settings.GetEffectiveChunkOverlapLines();
            var probeBytes = settings.GetEffectiveBinaryProbeBytes();

            var roots = new List<string>(capacity: 1 + settings.ExtraIncludeRoots.Count) { workspaceRoot };
            foreach (var extra in settings.ExtraIncludeRoots)
            {
                var p = Path.GetFullPath(Path.Combine(workspaceRoot, extra));
                if (Directory.Exists(p))
                    roots.Add(p);
            }

            var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

            var excludeRootFullPaths = new List<string>(settings.ExcludeRoots.Count);
            foreach (var rel in settings.ExcludeRoots)
            {
                var p = rel?.Trim();
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                if (Path.IsPathRooted(p) || p.Contains("..", StringComparison.Ordinal))
                    continue;
                var abs = Path.GetFullPath(Path.Combine(workspaceRoot, p));
                if (Directory.Exists(abs))
                    excludeRootFullPaths.Add(abs.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            }

            var candidates = new List<string>(capacity: 8192);
            foreach (var root in roots)
                candidates.AddRange(WorkspaceScanner.EnumerateIndexableFiles(root, extSet, settings.ExcludePathSegments, excludeRootFullPaths));

            var gitIgnore = GitIgnoreRules.TryLoad(workspaceRoot, settings.IgnoreFiles);

            var seen = new HashSet<string>(StringComparer.Ordinal);

            using var tx = conn.BeginTransaction();

            foreach (var absolute in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (WorkspaceScanner.ShouldExcludePath(absolute, settings.ExcludePathSegments))
                {
                    skippedExcluded++;
                    AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, WorkspaceScanner.RelativePath(workspaceRoot, absolute), "denylist");
                    continue;
                }

                var rel = WorkspaceScanner.RelativePath(workspaceRoot, absolute).Replace("\\", "/", StringComparison.Ordinal);
                seen.Add(rel);

                if (GitIgnoreRules.IsIgnored(gitIgnore, rel))
                {
                    skippedExcluded++;
                    AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, rel, "gitignore");
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(absolute);
                    if (info.Length > maxBytes)
                    {
                        skippedLarge++;
                        AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, rel, "too_large");
                        continue;
                    }
                }
                catch
                {
                    skippedExcluded++;
                    AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, rel, "io_error");
                    continue;
                }

                var lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
                if (!IsFileChanged(conn, tx, rel, info.Length, lastWriteUtcTicks))
                    continue;

                using var fs = info.OpenRead();
                var probeSize = (int)Math.Min(probeBytes, info.Length);
                var probe = new byte[probeSize];
                var read = fs.ReadAtLeast(probe.AsSpan(0, probeSize), probeSize, throwOnEndOfStream: false);
                if (WorkspaceScanner.LooksBinary(probe.AsSpan(0, read)))
                {
                    skippedBinary++;
                    AddSkip(skippedSample, skippedReasonCounts, skippedTopPathPrefixes, rel, "binary");
                    continue;
                }

                fs.Position = 0;
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();

                // Replace chunks for this path.
                DeleteChunksForPath(conn, tx, rel);

                var ext = Path.GetExtension(absolute);
                var chunks = WorkspaceScanner.ChunkByLines(text, chunkLines, overlapLines);
                var anyChunk = false;
                foreach (var (lineStart, lineEnd, body) in chunks)
                {
                    InsertChunk(conn, tx, rel, ext, lineStart, lineEnd, body);
                    anyChunk = true;
                }

                if (anyChunk)
                    filesIndexed++;

                UpsertFileState(conn, tx, rel, info.Length, lastWriteUtcTicks);
            }

            // Remove deleted files.
            foreach (var stale in EnumerateStalePaths(conn, tx, seen))
            {
                DeleteChunksForPath(conn, tx, stale);
                DeleteFileState(conn, tx, stale);
            }

            tx.Commit();

            UpsertMeta(conn, "format_version", FormatVersion.ToString(CultureInfo.InvariantCulture));
            UpsertMeta(conn, "indexed_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            UpsertMeta(conn, "workspace_root", workspaceRoot);
            UpsertMeta(conn, "reindex_error", "");
            UpsertMeta(conn, "reindex_error_at", "");
            UpsertMeta(conn, "reindex_state", "idle");
            UpsertMeta(conn, "reindex_started_at", "");
        }
        catch (Exception ex)
        {
            try
            {
                UpsertMeta(conn, "reindex_state", "error");
                UpsertMeta(conn, "reindex_error", ex.GetType().Name + ": " + ex.Message);
                UpsertMeta(conn, "reindex_error_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
            catch
            {
                // ignore
            }

            throw;
        }

        sw.Stop();
        return new ReindexSummary(
            FormatVersion,
            dbPath,
            filesIndexed,
            skippedLarge,
            skippedBinary,
            skippedExcluded,
            skippedReasonCounts,
            TopPrefixes(skippedTopPathPrefixes),
            skippedSample,
            sw.Elapsed);
    }

    private static void EnsureChunksTable(SqliteConnection conn)
    {
        try
        {
            Exec(conn, "SELECT 1 FROM chunks LIMIT 1;");
        }
        catch (SqliteException)
        {
            Exec(conn, """
                CREATE VIRTUAL TABLE IF NOT EXISTS chunks USING fts5(
                  path,
                  extension UNINDEXED,
                  line_start UNINDEXED,
                  line_end UNINDEXED,
                  body,
                  tokenize='unicode61 remove_diacritics 1'
                );
                """);
        }
    }

    private static void EnsureFileStateTable(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS file_state(
              path TEXT PRIMARY KEY,
              size_bytes INTEGER NOT NULL,
              last_write_utc_ticks INTEGER NOT NULL
            );
            """);
    }

    private static bool IsFileChanged(SqliteConnection conn, SqliteTransaction tx, string path, long sizeBytes, long lastWriteUtcTicks)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT size_bytes, last_write_utc_ticks FROM file_state WHERE path=$p LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return true;
        var prevSize = r.GetInt64(0);
        var prevTicks = r.GetInt64(1);
        return prevSize != sizeBytes || prevTicks != lastWriteUtcTicks;
    }

    private static void UpsertFileState(SqliteConnection conn, SqliteTransaction tx, string path, long sizeBytes, long lastWriteUtcTicks)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO file_state(path, size_bytes, last_write_utc_ticks)
            VALUES($p, $s, $t)
            ON CONFLICT(path) DO UPDATE SET size_bytes=excluded.size_bytes, last_write_utc_ticks=excluded.last_write_utc_ticks;
            """;
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$s", sizeBytes);
        cmd.Parameters.AddWithValue("$t", lastWriteUtcTicks);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteFileState(SqliteConnection conn, SqliteTransaction tx, string path)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM file_state WHERE path=$p;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    private static IEnumerable<string> EnumerateStalePaths(SqliteConnection conn, SqliteTransaction tx, HashSet<string> seen)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT path FROM file_state;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var p = r.GetString(0);
            if (!seen.Contains(p))
                yield return p;
        }
    }

    private static void DeleteChunksForPath(SqliteConnection conn, SqliteTransaction tx, string path)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM chunks WHERE path=$p;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    private static void InsertChunk(
        SqliteConnection conn,
        SqliteTransaction tx,
        string path,
        string ext,
        int lineStart,
        int lineEnd,
        string body)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO chunks(path, extension, line_start, line_end, body)
            VALUES ($path, $ext, $ls, $le, $body);
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$ext", ext);
        cmd.Parameters.AddWithValue("$ls", lineStart);
        cmd.Parameters.AddWithValue("$le", lineEnd);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.ExecuteNonQuery();
    }

    private static void AddSkip(
        List<SkippedPath> sample,
        Dictionary<string, int> reasonCounts,
        Dictionary<string, int> prefixCounts,
        string relPath,
        string reason)
    {
        reasonCounts[reason] = reasonCounts.TryGetValue(reason, out var c) ? c + 1 : 1;
        var pfx = GetPathPrefix(relPath);
        prefixCounts[pfx] = prefixCounts.TryGetValue(pfx, out var pc) ? pc + 1 : 1;

        if (sample.Count >= 50)
            return;
        sample.Add(new SkippedPath(relPath, reason));
    }

    private static string GetPathPrefix(string relPath)
    {
        var p = relPath.Replace("\\", "/", StringComparison.Ordinal);
        var idx = p.IndexOf('/', StringComparison.Ordinal);
        return idx <= 0 ? p : p[..idx];
    }

    private static IReadOnlyList<(string PathPrefix, int Count)> TopPrefixes(Dictionary<string, int> prefixCounts)
        => prefixCounts
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(static kv => (kv.Key, kv.Value))
            .ToArray();

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static void EnsureMetaTable(SqliteConnection conn)
    {
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS meta(
              key TEXT PRIMARY KEY,
              value TEXT
            );
            """);
    }

    private static void UpsertMeta(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta(key,value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static void InitEmptyIndex(SqliteConnection conn, string workspaceRoot)
    {
        void Exec(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE meta(
              key TEXT PRIMARY KEY,
              value TEXT
            );
            """);
        Exec("""
            CREATE VIRTUAL TABLE chunks USING fts5(
              path,
              extension UNINDEXED,
              line_start UNINDEXED,
              line_end UNINDEXED,
              body,
              tokenize='unicode61 remove_diacritics 1'
            );
            """);

        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO meta(key,value) VALUES('format_version', $v);
                """;
            ins.Parameters.AddWithValue("$v", FormatVersion.ToString(CultureInfo.InvariantCulture));
            ins.ExecuteNonQuery();
        }

        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO meta(key,value) VALUES('indexed_at', $ts);
                """;
            ins.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            ins.ExecuteNonQuery();
        }

        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO meta(key,value) VALUES('workspace_root', $wr);
                """;
            ins.Parameters.AddWithValue("$wr", workspaceRoot);
            ins.ExecuteNonQuery();
        }
    }

    // (moved) SearchAsync/Search/ExplainHitAsync/ExplainHit/BuildMatchQuery live in SqliteFtsIndex.Query.cs

    internal static Task<IndexStatus> GetStatusAsync(string workspaceRoot, string dbPath, CancellationToken cancellationToken)
        => Task.Run(() => GetStatus(workspaceRoot, dbPath), cancellationToken);

    private static IndexStatus GetStatus(string workspaceRoot, string dbPath)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var exists = File.Exists(dbPath);
        if (!exists)
        {
            IndexSettings.TryLoadFromIndexDirectoryWithDiagnostics(
                Path.GetDirectoryName(dbPath),
                out _,
                out var src,
                out var err);
            return new IndexStatus(FormatVersion, dbPath, false, 0, false, null, workspaceRoot, null, null, src, err, null, null, null);
        }

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        var indexedAt = ReadMeta(conn, "indexed_at");
        var reindexState = ReadMeta(conn, "reindex_state");
        if (string.IsNullOrWhiteSpace(reindexState))
            reindexState = null;
        var reindexStartedAt = ReadMeta(conn, "reindex_started_at");
        if (string.IsNullOrWhiteSpace(reindexStartedAt))
            reindexStartedAt = null;
        var lastErr = ReadMeta(conn, "reindex_error");
        if (string.IsNullOrWhiteSpace(lastErr))
            lastErr = null;
        var lastErrAt = ReadMeta(conn, "reindex_error_at");
        if (string.IsNullOrWhiteSpace(lastErrAt))
            lastErrAt = null;

        IndexSettings.TryLoadFromIndexDirectoryWithDiagnostics(
            Path.GetDirectoryName(dbPath),
            out var settings,
            out var settingsSource,
            out var settingsParseError);
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT count(*) FROM chunks;";
        var docCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
        var mayBeStale = string.Equals(reindexState, "running", StringComparison.OrdinalIgnoreCase);

        var eff = new EffectiveSettings(
            settings.IncludeCsInFts,
            settings.ExtraIncludeRoots,
            settings.ExcludeRoots,
            settings.GetEffectiveExtensions(),
            settings.ExcludePathSegments,
            settings.IgnoreFiles,
            settings.GetEffectiveMaxIndexedFileBytes(),
            settings.GetEffectiveChunkLines(),
            settings.GetEffectiveChunkOverlapLines(),
            settings.GetEffectiveBinaryProbeBytes());
        return new IndexStatus(
            FormatVersion,
            dbPath,
            true,
            docCount,
            mayBeStale,
            indexedAt,
            workspaceRoot,
            lastErr,
            lastErrAt,
            settingsSource,
            settingsParseError,
            eff,
            reindexState,
            reindexStartedAt);
    }

    private static string? ReadMeta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? null : Convert.ToString(o);
    }
}
