using System.Text;
using Microsoft.Data.Sqlite;

namespace HybridCodebaseIndex.Core;

internal static partial class SqliteFtsIndex
{
    internal static Task<(SearchResponse response, string? error)> SearchAsync(
        string workspaceRoot,
        string dbPath,
        string query,
        int topN,
        string? pathPrefix,
        IReadOnlyList<string>? excludePathPrefixes,
        IReadOnlyList<string>? extensions,
        CancellationToken cancellationToken)
        => Task.Run(() => Search(workspaceRoot, dbPath, query, topN, pathPrefix, excludePathPrefixes, extensions), cancellationToken);

    private static (SearchResponse response, string? error) Search(
        string workspaceRoot,
        string dbPath,
        string userQuery,
        int topN,
        string? pathPrefix,
        IReadOnlyList<string>? excludePathPrefixes,
        IReadOnlyList<string>? extensions)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        if (!File.Exists(dbPath))
            return (new SearchResponse(FormatVersion, userQuery, dbPath, []), "Index database not found; run codebase_index_reindex.");

        var fts = BuildMatchQuery(userQuery);
        if (fts is null)
            return (new SearchResponse(FormatVersion, userQuery, dbPath, []), null);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder();
        sql.AppendLine("SELECT rowid, path, extension, line_start, line_end, length(body), bm25(chunks), snippet(chunks, 4, '[', ']', ' … ', 24)");
        sql.AppendLine("FROM chunks");
        sql.AppendLine("WHERE chunks MATCH $q");

        if (!string.IsNullOrWhiteSpace(pathPrefix))
        {
            var pfx = NormalizePathPrefix(pathPrefix);
            sql.AppendLine("  AND path LIKE $pfx ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$pfx", EscapeLike(pfx) + "%");
        }

        if (excludePathPrefixes is { Count: > 0 })
        {
            var i = 0;
            foreach (var raw in excludePathPrefixes)
            {
                var p = NormalizePathPrefix(raw);
                if (p.Length == 0)
                    continue;
                var key = "$xp" + i++;
                sql.AppendLine($"  AND path NOT LIKE {key} ESCAPE '\\'");
                cmd.Parameters.AddWithValue(key, EscapeLike(p) + "%");
            }
        }

        if (extensions is { Count: > 0 })
        {
            var norm = NormalizeExtensionsForFilter(extensions);
            if (norm.Count > 0)
            {
                var keys = new List<string>(norm.Count);
                for (var i = 0; i < norm.Count; i++)
                {
                    var k = "$e" + i;
                    keys.Add(k);
                    cmd.Parameters.AddWithValue(k, norm[i]);
                }
                sql.AppendLine($"  AND extension IN ({string.Join(", ", keys)})");
            }
        }

        sql.AppendLine("ORDER BY bm25(chunks) DESC");
        sql.AppendLine("LIMIT $lim;");

        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$q", fts);
        cmd.Parameters.AddWithValue("$lim", topN);

        var hits = new List<IndexHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var hitId = reader.GetInt64(0);
            var path = reader.GetString(1);
            var ext = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var lineStart = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var lineEnd = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            var chunkChars = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            var bm = reader.GetDouble(6);
            var snip = reader.IsDBNull(7) ? null : reader.GetString(7);
            hits.Add(new IndexHit(hitId, path, ext, HitKinds.TextFts, bm, snip, lineStart, lineEnd, chunkChars));
        }

        return (new SearchResponse(FormatVersion, userQuery, dbPath, hits), null);
    }

    private static string NormalizePathPrefix(string raw)
        => raw.Trim().Replace("\\", "/", StringComparison.Ordinal).TrimStart('/');

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static List<string> NormalizeExtensionsForFilter(IReadOnlyList<string> raw)
    {
        var list = new List<string>(raw.Count);
        foreach (var s0 in raw)
        {
            var s = s0.Trim();
            if (s.Length == 0)
                continue;
            if (!s.StartsWith(".", StringComparison.Ordinal))
                s = "." + s;
            s = s.ToLowerInvariant();
            list.Add(s);
        }
        return list;
    }

    internal static Task<ExplainHitResponse> ExplainHitAsync(
        string workspaceRoot,
        string dbPath,
        long hitId,
        CancellationToken cancellationToken)
        => Task.Run(() => ExplainHit(workspaceRoot, dbPath, hitId), cancellationToken);

    private static ExplainHitResponse ExplainHit(string workspaceRoot, string dbPath, long hitId)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        if (!File.Exists(dbPath))
            return new ExplainHitResponse(FormatVersion, dbPath, null, "Index database not found; run codebase_index_reindex.");

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT rowid, path, extension, line_start, line_end, length(body), substr(body, 1, 1200)
            FROM chunks
            WHERE rowid = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", hitId);

        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return new ExplainHitResponse(FormatVersion, dbPath, null, $"Hit not found: {hitId}");

        var id = r.GetInt64(0);
        var path = r.GetString(1);
        var ext = r.IsDBNull(2) ? "" : r.GetString(2);
        var lineStart = r.IsDBNull(3) ? 0 : r.GetInt32(3);
        var lineEnd = r.IsDBNull(4) ? 0 : r.GetInt32(4);
        var chunkChars = r.IsDBNull(5) ? 0 : r.GetInt32(5);
        var body = r.IsDBNull(6) ? null : r.GetString(6);

        var hit = new IndexHit(id, path, ext, HitKinds.TextFts, 0, body, lineStart, lineEnd, chunkChars);
        return new ExplainHitResponse(FormatVersion, dbPath, hit, null);
    }

    /// <summary>Безопасное FTS5 MATCH: токены через AND, суффикс * (префиксное совпадение). Пустой запрос → null.</summary>
    internal static string? BuildMatchQuery(string userQuery)
    {
        var tokens = userQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static t => t.Trim().Trim('"'))
            .Where(static t => t.Length > 0)
            .Take(24)
            .ToArray();
        if (tokens.Length == 0)
            return null;

        var parts = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            var safe = t.Replace("\"", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal);
            if (safe.Length == 0)
                continue;
            parts.Add('"' + safe + "\"*");
        }

        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }
}

