namespace HybridCodebaseIndex.Core;

/// <summary>Сервис гибридного индекса (ADR 0105 слой B, v0: только FTS5 по тексту файлов).</summary>
public sealed class CodebaseIndexService
{
    private readonly string _indexDirectoryRelative;

    public CodebaseIndexService(string indexDirectoryRelative = ".hybrid-codebase-index")
    {
        _indexDirectoryRelative = string.IsNullOrWhiteSpace(indexDirectoryRelative)
            ? ".hybrid-codebase-index"
            : indexDirectoryRelative;
    }

    public string GetDatabasePath(string workspaceRoot)
        => SqliteFtsIndex.ResolveDatabasePathForRead(Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar)), _indexDirectoryRelative);

    public Task<ReindexSummary> FullReindexAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var db = SqliteFtsIndex.ResolveDatabasePathForWrite(root, _indexDirectoryRelative);
        return SqliteFtsIndex.FullRebuildAsync(root, db, cancellationToken);
    }

    public Task<ExplainHitResponse> ExplainHitAsync(
        string workspaceRoot,
        long hitId,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var db = SqliteFtsIndex.ResolveDatabasePathForRead(root, _indexDirectoryRelative);
        return SqliteFtsIndex.ExplainHitAsync(root, db, hitId, cancellationToken);
    }

    public Task<(SearchResponse response, string? error)> SearchAsync(
        string workspaceRoot,
        string query,
        int topN = 15,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var db = SqliteFtsIndex.ResolveDatabasePathForRead(root, _indexDirectoryRelative);
        return SqliteFtsIndex.SearchAsync(root, db, query, topN, cancellationToken);
    }

    public Task<IndexStatus> GetStatusAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var db = SqliteFtsIndex.ResolveDatabasePathForRead(root, _indexDirectoryRelative);
        return SqliteFtsIndex.GetStatusAsync(root, db, cancellationToken);
    }
}
