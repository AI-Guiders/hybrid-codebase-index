using System.Buffers.Binary;
using HybridCodebaseIndex.Core.Embeddings;
using Microsoft.Data.Sqlite;

namespace HybridCodebaseIndex.Core;

internal static partial class SqliteFtsIndex
{
    private static void EnsureVectorsTable(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS vectors(
              chunk_rowid INTEGER PRIMARY KEY,
              dim INTEGER NOT NULL,
              norm REAL NOT NULL,
              vec BLOB NOT NULL,
              updated_at_utc_ticks INTEGER NOT NULL
            );
            """);
    }

    internal static Task<(int vectorsUpserted, string? err)> ReindexVectorsAsync(
        string workspaceRoot,
        string dbPath,
        CancellationToken cancellationToken)
        => Task.Run(() => ReindexVectors(workspaceRoot, dbPath, cancellationToken), cancellationToken);

    private static (int vectorsUpserted, string? err) ReindexVectors(
        string workspaceRoot,
        string dbPath,
        CancellationToken cancellationToken)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        if (!File.Exists(dbPath))
            return (0, "Index database not found; run codebase_index_reindex.");

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        conn.Open();

        EnsureMetaTable(conn);
        EnsureChunksTable(conn);
        EnsureFileStateTable(conn);
        EnsureVectorsTable(conn);

        var settings = IndexSettings.TryLoadFromIndexDirectory(Path.GetDirectoryName(dbPath)!);
        if (!settings.SemanticEnabled)
            return (0, "semantic_enabled=false in settings; enable it to build vectors.");

        var provider = EmbeddingProviderFactory.Create(settings, Path.GetDirectoryName(dbPath));
        var dim = provider.Dimension;

        using var select = conn.CreateCommand();
        select.CommandText = "SELECT rowid, substr(body, 1, 4000) FROM chunks;";

        using var upsert = conn.CreateCommand();
        upsert.CommandText = """
            INSERT INTO vectors(chunk_rowid, dim, norm, vec, updated_at_utc_ticks)
            VALUES($id, $dim, $norm, $vec, $t)
            ON CONFLICT(chunk_rowid) DO UPDATE SET
              dim=excluded.dim,
              norm=excluded.norm,
              vec=excluded.vec,
              updated_at_utc_ticks=excluded.updated_at_utc_ticks;
            """;

        var nowTicks = DateTime.UtcNow.Ticks;
        var count = 0;

        using var r = select.ExecuteReader();
        while (r.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = r.GetInt64(0);
            var text = r.IsDBNull(1) ? "" : r.GetString(1);

            var emb = provider.EmbedAsync(text, cancellationToken).GetAwaiter().GetResult();
            if (emb.Length != dim)
                continue;

            var (blob, norm) = SerializeVector(emb);

            upsert.Parameters.Clear();
            upsert.Parameters.AddWithValue("$id", id);
            upsert.Parameters.AddWithValue("$dim", dim);
            upsert.Parameters.AddWithValue("$norm", norm);
            upsert.Parameters.AddWithValue("$vec", blob);
            upsert.Parameters.AddWithValue("$t", nowTicks);
            upsert.ExecuteNonQuery();
            count++;
        }

        UpsertMeta(conn, "vec_dim", dim.ToString(System.Globalization.CultureInfo.InvariantCulture));
        UpsertMeta(conn, "vec_indexed_at", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return (count, null);
    }

    private static (byte[] blob, double norm) SerializeVector(float[] v)
    {
        double sum = 0;
        foreach (var x in v)
            sum += x * x;
        var norm = Math.Sqrt(sum);

        var blob = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * 4, 4), v[i]);

        return (blob, norm);
    }
}

