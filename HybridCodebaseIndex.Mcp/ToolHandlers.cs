using System.Collections.Frozen;
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

    internal static string Handle(string name, IReadOnlyDictionary<string, JsonElement> args)
    {
        args ??= FrozenDictionary<string, JsonElement>.Empty;
        return name switch
        {
            "codebase_index_search" => HandleSearch(args),
            "codebase_index_status" => HandleStatus(args),
            "codebase_index_reindex" => HandleReindex(args),
            _ => throw new ArgumentException($"Unknown tool: {name}", nameof(name)),
        };
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
            Hits: response.Hits.Select(static h => new HitDto(h.Path, h.HitKind, h.RankScore, h.Snippet, h.LineStart, h.LineEnd)).ToList());

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
            IndexedAtIso: st.IndexedAtIso,
            WorkspaceRoot: st.WorkspaceRootNormalized);

        return JsonSerializer.Serialize(dto, JsonOut);
    }

    private static string HandleReindex(IReadOnlyDictionary<string, JsonElement> args)
    {
        var ws = RequireString(args, "workspace_path");
        var summary = Service.FullReindexAsync(ws).GetAwaiter().GetResult();
        var dto = new ReindexResultDto(
            IndexFormatVersion: summary.IndexFormatVersion,
            DatabasePath: summary.DatabasePath,
            FilesIndexed: summary.FilesIndexed,
            FilesSkippedTooLarge: summary.FilesSkippedTooLarge,
            FilesSkippedBinary: summary.FilesSkippedBinary,
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

    private sealed record SearchResultDto(
        string? Err,
        int IndexFormatVersion,
        string Query,
        string DatabasePath,
        List<HitDto> Hits);

    private sealed record HitDto(
        string Path,
        string HitKind,
        double RankScore,
        string? Snippet,
        int LineStart,
        int LineEnd);

    private sealed record StatusResultDto(
        int IndexFormatVersion,
        string DatabasePath,
        bool DatabaseExists,
        int DocumentCount,
        string? IndexedAtIso,
        string? WorkspaceRoot);

    private sealed record ReindexResultDto(
        int IndexFormatVersion,
        string DatabasePath,
        int FilesIndexed,
        int FilesSkippedTooLarge,
        int FilesSkippedBinary,
        long DurationMs);
}
