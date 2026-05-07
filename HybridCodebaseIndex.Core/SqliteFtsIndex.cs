using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace HybridCodebaseIndex.Core;

internal static class SqliteFtsIndex
{
    internal const int FormatVersion = 1;

    internal static string ResolveDatabasePath(string workspaceRoot, string indexDirectoryRelative)
    {
        var dir = Path.Combine(workspaceRoot, indexDirectoryRelative.TrimStart(Path.DirectorySeparatorChar, '/'));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"codebase-index-v{FormatVersion}.sqlite");
    }

    internal static Task<ReindexSummary> FullRebuildAsync(
        string workspaceRoot,
        string dbPath,
        CancellationToken cancellationToken)
        => Task.Run(() => FullRebuild(workspaceRoot, dbPath, cancellationToken), cancellationToken);

    private static ReindexSummary FullRebuild(string workspaceRoot, string dbPath, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));

        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var filesIndexed = 0;
        var skippedLarge = 0;
        var skippedBinary = 0;

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        conn.Open();

        InitEmptyIndex(conn, workspaceRoot);

        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO chunks(path, extension, body)
            VALUES ($path, $ext, $body);
            """;

        foreach (var absolute in WorkspaceScanner.EnumerateIndexableFiles(workspaceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(absolute);
                if (info.Length > WorkspaceScanner.MaxIndexedFileBytes)
                {
                    skippedLarge++;
                    continue;
                }
            }
            catch
            {
                continue;
            }

            using var fs = info.OpenRead();
            var probeSize = (int)Math.Min(8192, info.Length);
            var probe = new byte[probeSize];
            var read = fs.ReadAtLeast(probe.AsSpan(0, probeSize), probeSize, throwOnEndOfStream: false);
            if (WorkspaceScanner.LooksBinary(probe.AsSpan(0, read)))
            {
                skippedBinary++;
                continue;
            }

            fs.Position = 0;
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();

            var relative = WorkspaceScanner.RelativePath(workspaceRoot, absolute);
            var ext = Path.GetExtension(absolute);

            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$path", relative.Replace("\\", "/", StringComparison.Ordinal));
            insert.Parameters.AddWithValue("$ext", ext);
            insert.Parameters.AddWithValue("$body", text);
            insert.ExecuteNonQuery();
            filesIndexed++;
        }

        tx.Commit();
        sw.Stop();
        return new ReindexSummary(
            FormatVersion,
            dbPath,
            filesIndexed,
            skippedLarge,
            skippedBinary,
            sw.Elapsed);
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
              extension,
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

    internal static Task<(SearchResponse response, string? error)> SearchAsync(
        string workspaceRoot,
        string dbPath,
        string query,
        int topN,
        CancellationToken cancellationToken)
        => Task.Run(() => Search(workspaceRoot, dbPath, query, topN), cancellationToken);

    private static (SearchResponse response, string? error) Search(
        string workspaceRoot,
        string dbPath,
        string userQuery,
        int topN)
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
        cmd.CommandText = """
             SELECT path, bm25(chunks), snippet(chunks, 2, '[', ']', ' … ', 24)
             FROM chunks
             WHERE chunks MATCH $q
             ORDER BY bm25(chunks) DESC
             LIMIT $lim;
             """;
        cmd.Parameters.AddWithValue("$q", fts);
        cmd.Parameters.AddWithValue("$lim", topN);

        var hits = new List<IndexHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            var bm = reader.GetDouble(1);
            var snip = reader.IsDBNull(2) ? null : reader.GetString(2);
            hits.Add(new IndexHit(path, HitKinds.TextFts, bm, snip, LineStart: 0, LineEnd: 0));
        }

        return (new SearchResponse(FormatVersion, userQuery, dbPath, hits), null);
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

    internal static Task<IndexStatus> GetStatusAsync(string workspaceRoot, string dbPath, CancellationToken cancellationToken)
        => Task.Run(() => GetStatus(workspaceRoot, dbPath), cancellationToken);

    private static IndexStatus GetStatus(string workspaceRoot, string dbPath)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var exists = File.Exists(dbPath);
        if (!exists)
            return new IndexStatus(FormatVersion, dbPath, false, 0, null, workspaceRoot);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        var indexedAt = ReadMeta(conn, "indexed_at");
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT count(*) FROM chunks;";
        var docCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
        return new IndexStatus(FormatVersion, dbPath, true, docCount, indexedAt, workspaceRoot);
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
