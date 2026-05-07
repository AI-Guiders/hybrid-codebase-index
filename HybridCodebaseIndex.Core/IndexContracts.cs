namespace HybridCodebaseIndex.Core;

/// <summary>Стабильные значения типа попадания (ADR 0105 слой B). Векторный канал появится при включении semantic.</summary>
public static class HitKinds
{
    public const string TextFts = "text_fts";
    public const string TextVector = "text_vector";
}

public sealed record IndexHit(
    long HitId,
    string Path,
    string HitKind,
    double RankScore,
    string? Snippet,
    int LineStart,
    int LineEnd);

public sealed record SearchResponse(
    int IndexFormatVersion,
    string Query,
    string DatabasePath,
    IReadOnlyList<IndexHit> Hits);

public sealed record ExplainHitResponse(
    int IndexFormatVersion,
    string DatabasePath,
    IndexHit? Hit,
    string? Err);

public sealed record IndexStatus(
    int IndexFormatVersion,
    string DatabasePath,
    bool DatabaseExists,
    int DocumentCount,
    string? IndexedAtIso,
    string? WorkspaceRootNormalized,
    string? LastReindexError,
    string? LastReindexErrorAtIso,
    string SettingsSource,
    string? SettingsParseError);

public sealed record ReindexSummary(
    int IndexFormatVersion,
    string DatabasePath,
    int FilesIndexed,
    int FilesSkippedTooLarge,
    int FilesSkippedBinary,
    int FilesSkippedExcluded,
    IReadOnlyList<SkippedPath> SkippedSample,
    TimeSpan Duration);

public sealed record SkippedPath(
    string Path,
    string Reason);
