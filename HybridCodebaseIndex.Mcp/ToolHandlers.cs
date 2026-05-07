using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HybridCodebaseIndex.Core;

namespace HybridCodebaseIndex.Mcp;

internal static class ToolHandlers
{
    private static readonly CodebaseIndexService Service = new();

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions JsonOutWithNulls = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static string Handle(string name, IReadOnlyDictionary<string, JsonElement> args)
    {
        args ??= FrozenDictionary<string, JsonElement>.Empty;
        return name switch
        {
            "codebase_index_version" => HandleVersion(),
            "codebase_index_search" => HandleSearch(args),
            "codebase_index_explain" => HandleExplain(args),
            "codebase_index_status" => HandleStatus(args),
            "codebase_index_reindex" => HandleReindex(args),
            _ => throw new ArgumentException($"Unknown tool: {name}", nameof(name)),
        };
    }

    private static string HandleVersion()
    {
        var asm = typeof(ToolHandlers).Assembly;
        var name = asm.GetName();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var dto = new VersionResultDto(
            AssemblyName: name.Name ?? "unknown",
            AssemblyVersion: name.Version?.ToString(),
            InformationalVersion: info,
            FrameworkDescription: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            RuntimeIdentifier: System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            OsDescription: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ProcessArchitecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());

        return JsonSerializer.Serialize(dto, JsonOut);
    }

    private static string HandleSearch(IReadOnlyDictionary<string, JsonElement> args)
    {
        var ws = RequireString(args, "workspace_path");
        var q = RequireString(args, "query");
        var topN = OptionalInt(args, "top_n") ?? 15;
        if (topN < 1)
            topN = 1;
        if (topN > 128)
            topN = 128;

        var (response, err) = Service.SearchAsync(ws, q, topN).GetAwaiter().GetResult();
        var dto = new SearchResultDto(
            Err: err,
            IndexFormatVersion: response.IndexFormatVersion,
            Query: response.Query,
            DatabasePath: response.DatabasePath,
            Hits: response.Hits.Select(static h => new HitDto(h.HitId, h.Path, h.HitKind, h.RankScore, h.Snippet, h.LineStart, h.LineEnd)).ToList());

        return JsonSerializer.Serialize(dto, JsonOut);
    }

    private static string HandleExplain(IReadOnlyDictionary<string, JsonElement> args)
    {
        var ws = RequireString(args, "workspace_path");
        var hitId = RequireLong(args, "hit_id");

        var resp = Service.ExplainHitAsync(ws, hitId).GetAwaiter().GetResult();
        var dto = new ExplainResultDto(
            Err: resp.Err,
            IndexFormatVersion: resp.IndexFormatVersion,
            DatabasePath: resp.DatabasePath,
            Hit: resp.Hit is null
                ? null
                : new HitDto(resp.Hit.HitId, resp.Hit.Path, resp.Hit.HitKind, resp.Hit.RankScore, resp.Hit.Snippet, resp.Hit.LineStart, resp.Hit.LineEnd));

        return JsonSerializer.Serialize(dto, JsonOut);
    }

    private static string HandleStatus(IReadOnlyDictionary<string, JsonElement> args)
    {
        var ws = RequireString(args, "workspace_path");
        var st = Service.GetStatusAsync(ws).GetAwaiter().GetResult();
        var dto = new StatusResultDto(
            IndexFormatVersion: st.IndexFormatVersion,
            DatabasePath: st.DatabasePath,
            DatabaseExists: st.DatabaseExists,
            DocumentCount: st.DocumentCount,
            DocumentCountMayBeStale: st.DocumentCountMayBeStale,
            IndexedAtIso: st.IndexedAtIso,
            WorkspaceRoot: st.WorkspaceRootNormalized,
            LastReindexError: st.LastReindexError,
            LastReindexErrorAtIso: st.LastReindexErrorAtIso,
            SettingsSource: st.SettingsSource,
            SettingsParseError: st.SettingsParseError,
            ReindexState: st.ReindexState,
            ReindexStartedAtIso: st.ReindexStartedAtIso);

        // Keep the contract stable: include nullable fields explicitly.
        return JsonSerializer.Serialize(dto, JsonOutWithNulls);
    }

    private static string HandleReindex(IReadOnlyDictionary<string, JsonElement> args)
    {
        var ws = RequireString(args, "workspace_path");
        var full = OptionalBool(args, "full_rebuild") ?? false;
        var summary = full
            ? Service.FullRebuildAsync(ws).GetAwaiter().GetResult()
            : Service.FullReindexAsync(ws).GetAwaiter().GetResult();
        var dto = new ReindexResultDto(
            IndexFormatVersion: summary.IndexFormatVersion,
            DatabasePath: summary.DatabasePath,
            FilesIndexed: summary.FilesIndexed,
            FilesSkippedTooLarge: summary.FilesSkippedTooLarge,
            FilesSkippedBinary: summary.FilesSkippedBinary,
            FilesSkippedExcluded: summary.FilesSkippedExcluded,
            SkippedSample: summary.SkippedSample.Select(static s => new SkippedDto(s.Path, s.Reason)).ToList(),
            DurationMs: (long)summary.Duration.TotalMilliseconds);

        return JsonSerializer.Serialize(dto, JsonOut);
    }

    private static string RequireString(IReadOnlyDictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Missing or invalid '{key}' (string required).");

        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException($"Missing or invalid '{key}' (string required).");

        return s.Trim();
    }

    private static int? OptionalInt(IReadOnlyDictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el))
            return null;
        return el.TryGetInt32(out var i) ? i : null;
    }

    private static bool? OptionalBool(IReadOnlyDictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el))
            return null;
        return el.ValueKind == JsonValueKind.True ? true
            : el.ValueKind == JsonValueKind.False ? false
            : null;
    }

    private static long RequireLong(IReadOnlyDictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el))
            throw new ArgumentException($"Missing or invalid '{key}' (integer required).");

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v))
            return v;

        throw new ArgumentException($"Missing or invalid '{key}' (integer required).");
    }

    private sealed record SearchResultDto(
        string? Err,
        int IndexFormatVersion,
        string Query,
        string DatabasePath,
        List<HitDto> Hits);

    private sealed record HitDto(
        long HitId,
        string Path,
        string HitKind,
        double RankScore,
        string? Snippet,
        int LineStart,
        int LineEnd);

    private sealed record ExplainResultDto(
        string? Err,
        int IndexFormatVersion,
        string DatabasePath,
        HitDto? Hit);

    private sealed record StatusResultDto(
        int IndexFormatVersion,
        string DatabasePath,
        bool DatabaseExists,
        int DocumentCount,
        bool DocumentCountMayBeStale,
        string? IndexedAtIso,
        string? WorkspaceRoot,
        string? LastReindexError,
        string? LastReindexErrorAtIso,
        string SettingsSource,
        string? SettingsParseError,
        string? ReindexState,
        string? ReindexStartedAtIso);

    private sealed record ReindexResultDto(
        int IndexFormatVersion,
        string DatabasePath,
        int FilesIndexed,
        int FilesSkippedTooLarge,
        int FilesSkippedBinary,
        int FilesSkippedExcluded,
        List<SkippedDto> SkippedSample,
        long DurationMs);

    private sealed record SkippedDto(
        string Path,
        string Reason);

    private sealed record VersionResultDto(
        string AssemblyName,
        string? AssemblyVersion,
        string? InformationalVersion,
        string FrameworkDescription,
        string RuntimeIdentifier,
        string OsDescription,
        string ProcessArchitecture);
}
