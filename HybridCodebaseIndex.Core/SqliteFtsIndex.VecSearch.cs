using System.Buffers.Binary;
using HybridCodebaseIndex.Core.Embeddings;
using Microsoft.Data.Sqlite;

namespace HybridCodebaseIndex.Core;

internal static partial class SqliteFtsIndex
{
    internal static Task<(SearchResponse response, string? error)> SearchHybridAsync(
        string workspaceRoot,
        string dbPath,
        string query,
        int topN,
        string? pathPrefix,
        IReadOnlyList<string>? excludePathPrefixes,
        IReadOnlyList<string>? extensions,
        bool semantic,
        double alpha,
        double beta,
        int vecTopK,
        CancellationToken cancellationToken)
        => Task.Run(() => SearchHybrid(workspaceRoot, dbPath, query, topN, pathPrefix, excludePathPrefixes, extensions, semantic, alpha, beta, vecTopK, cancellationToken), cancellationToken);

    private static (SearchResponse response, string? error) SearchHybrid(
        string workspaceRoot,
        string dbPath,
        string userQuery,
        int topN,
        string? pathPrefix,
        IReadOnlyList<string>? excludePathPrefixes,
        IReadOnlyList<string>? extensions,
        bool semantic,
        double alpha,
        double beta,
        int vecTopK,
        CancellationToken cancellationToken)
    {
        // FTS always available
        var (ftsResp, ftsErr) = Search(workspaceRoot, dbPath, userQuery, topN, pathPrefix, excludePathPrefixes, extensions);
        if (!semantic)
            return (ftsResp, ftsErr);
        if (!string.IsNullOrEmpty(ftsErr))
            return (ftsResp, ftsErr);

        if (!File.Exists(dbPath))
            return (ftsResp, ftsErr);

        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        // If vectors table missing, just return FTS.
        if (!TableExists(conn, "vectors"))
            return (ftsResp, null);

        var settings = IndexSettings.TryLoadFromIndexDirectory(Path.GetDirectoryName(dbPath)!);
        if (!settings.SemanticEnabled)
            return (ftsResp, null);

        var provider = EmbeddingProviderFactory.Create(settings, Path.GetDirectoryName(dbPath));
        var qv = provider.EmbedAsync(userQuery, cancellationToken).GetAwaiter().GetResult();
        var qn = Norm(qv);
        if (qn <= 1e-12)
            return (ftsResp, null);

        vecTopK = Math.Clamp(vecTopK, 5, 200);

        var vecHits = VecTopK(conn, qv, qn, vecTopK);

        // Merge by hitId (chunk rowid)
        var merged = new Dictionary<long, IndexHit>();
        foreach (var h in ftsResp.Hits)
            merged[h.HitId] = h;

        foreach (var (chunkId, sim) in vecHits)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (merged.TryGetValue(chunkId, out var existing))
            {
                var fused = new IndexHit(
                    existing.HitId,
                    existing.Path,
                    existing.Extension,
                    existing.HitKind,
                    RankScore: alpha * (existing.FtsScore ?? existing.RankScore) + beta * sim,
                    FtsScore: existing.FtsScore ?? existing.RankScore,
                    VecScore: sim,
                    existing.Snippet,
                    existing.LineStart,
                    existing.LineEnd,
                    existing.ChunkCharCount,
                    existing.LastWriteUtcIso);
                merged[chunkId] = fused;
                continue;
            }

            // Fetch minimal chunk metadata for vec-only hits.
            if (!TryGetChunk(conn, chunkId, out var hit))
                continue;

            merged[chunkId] = new IndexHit(
                hit.HitId,
                hit.Path,
                hit.Extension,
                HitKinds.TextVector,
                RankScore: sim,
                FtsScore: null,
                VecScore: sim,
                hit.Snippet,
                hit.LineStart,
                hit.LineEnd,
                hit.ChunkCharCount,
                hit.LastWriteUtcIso);
        }

        var ordered = merged.Values
            .OrderByDescending(static h => h.RankScore)
            .Take(topN)
            .ToList();

        return (new SearchResponse(FormatVersion, userQuery, dbPath, ordered), null);
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static List<(long chunkRowId, double sim)> VecTopK(SqliteConnection conn, float[] qv, double qn, int k)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT chunk_rowid, dim, norm, vec FROM vectors;";

        var top = new List<(long id, double sim)>(capacity: k);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            var dim = r.GetInt32(1);
            var norm = r.GetDouble(2);
            if (dim != qv.Length || norm <= 1e-12)
                continue;
            var blob = (byte[])r.GetValue(3);
            var sim = DotBlob(blob, qv) / (qn * norm);
            InsertTopK(top, (id, sim), k);
        }

        return top.Select(static x => (x.id, x.sim)).ToList();
    }

    private static void InsertTopK(List<(long id, double sim)> top, (long id, double sim) item, int k)
    {
        if (top.Count < k)
        {
            top.Add(item);
            top.Sort(static (a, b) => b.sim.CompareTo(a.sim));
            return;
        }

        if (item.sim <= top[^1].sim)
            return;

        top[^1] = item;
        top.Sort(static (a, b) => b.sim.CompareTo(a.sim));
    }

    private static double DotBlob(byte[] blob, float[] qv)
    {
        double sum = 0;
        for (var i = 0; i < qv.Length; i++)
        {
            var f = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * 4, 4));
            sum += f * qv[i];
        }
        return sum;
    }

    private static double Norm(float[] v)
    {
        double sum = 0;
        foreach (var x in v)
            sum += x * x;
        return Math.Sqrt(sum);
    }

    private static bool TryGetChunk(SqliteConnection conn, long id, out IndexHit hit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.rowid, c.path, c.extension, c.line_start, c.line_end, length(c.body), snippet(chunks, 4, '[', ']', ' … ', 24), fs.last_write_utc_ticks
            FROM chunks c
            LEFT JOIN file_state fs ON fs.path = c.path
            WHERE c.rowid = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);

        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            hit = null!;
            return false;
        }

        var path = r.GetString(1);
        var ext = r.IsDBNull(2) ? "" : r.GetString(2);
        var ls = r.IsDBNull(3) ? 0 : r.GetInt32(3);
        var le = r.IsDBNull(4) ? 0 : r.GetInt32(4);
        var chars = r.IsDBNull(5) ? 0 : r.GetInt32(5);
        var snip = r.IsDBNull(6) ? null : r.GetString(6);
        var lastWriteIso = r.IsDBNull(7) ? null : new DateTime(r.GetInt64(7), DateTimeKind.Utc).ToString("O");

        hit = new IndexHit(id, path, ext, HitKinds.TextVector, 0, FtsScore: null, VecScore: null, snip, ls, le, chars, lastWriteIso);
        return true;
    }
}

