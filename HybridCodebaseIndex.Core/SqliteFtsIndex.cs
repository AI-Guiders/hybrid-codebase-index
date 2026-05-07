using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace HybridCodebaseIndex.Core;

internal static class SqliteFtsIndex
{
    // Bump when on-disk schema changes in a non-backward-compatible way.
    internal const int FormatVersion = 2;

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

        // Build into a fresh file if an existing DB is present; do not touch it (it can be in use by readers).
        // If there is no DB yet, build directly into the final path to avoid extra file moves (more robust on Windows).
        var hasExisting = File.Exists(dbPath);
        var tmpPath = hasExisting ? dbPath + ".tmp-" + Guid.NewGuid().ToString("n") : dbPath;

        var filesIndexed = 0;
        var skippedLarge = 0;
        var skippedBinary = 0;
        var skippedExcluded = 0;
        var skippedSample = new List<SkippedPath>(capacity: 64);

        {
            using var conn = new SqliteConnection($"Data Source={tmpPath};Mode=ReadWriteCreate");
            conn.Open();

            InitEmptyIndex(conn, workspaceRoot);

            using var tx = conn.BeginTransaction();
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO chunks(path, extension, line_start, line_end, body)
                VALUES ($path, $ext, $ls, $le, $body);
                """;

            var candidates = WorkspaceScanner.EnumerateIndexableFiles(workspaceRoot).ToList();

            // Optional: respect .gitignore via `git check-ignore` if git is available.
            var relCandidates = new List<string>(capacity: candidates.Count);
            foreach (var absolute in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (WorkspaceScanner.ShouldExcludePath(absolute))
                {
                    skippedExcluded++;
                    AddSample(skippedSample, WorkspaceScanner.RelativePath(workspaceRoot, absolute), "denylist");
                    continue;
                }

                relCandidates.Add(WorkspaceScanner.RelativePath(workspaceRoot, absolute).Replace("\\", "/", StringComparison.Ordinal));
            }

            var ignoredByGit = GitCheckIgnore.GetIgnoredRelativePathsOrEmpty(workspaceRoot, relCandidates);

            foreach (var absolute in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (WorkspaceScanner.ShouldExcludePath(absolute))
                    continue;

                var rel = WorkspaceScanner.RelativePath(workspaceRoot, absolute).Replace("\\", "/", StringComparison.Ordinal);
                if (ignoredByGit.Contains(rel))
                {
                    skippedExcluded++;
                    AddSample(skippedSample, rel, "gitignore");
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(absolute);
                    if (info.Length > WorkspaceScanner.MaxIndexedFileBytes)
                    {
                        skippedLarge++;
                        AddSample(skippedSample, rel, "too_large");
                        continue;
                    }
                }
                catch
                {
                    skippedExcluded++;
                    AddSample(skippedSample, rel, "io_error");
                    continue;
                }

                using var fs = info.OpenRead();
                var probeSize = (int)Math.Min(8192, info.Length);
                var probe = new byte[probeSize];
                var read = fs.ReadAtLeast(probe.AsSpan(0, probeSize), probeSize, throwOnEndOfStream: false);
                if (WorkspaceScanner.LooksBinary(probe.AsSpan(0, read)))
                {
                    skippedBinary++;
                    AddSample(skippedSample, rel, "binary");
                    continue;
                }

                fs.Position = 0;
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();

                var ext = Path.GetExtension(absolute);

                var chunks = WorkspaceScanner.ChunkByLines(text);
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
        }

        sw.Stop();

        if (hasExisting)
            TryReplaceDatabase(tmpPath, dbPath);

        return new ReindexSummary(
            FormatVersion,
            dbPath,
            filesIndexed,
            skippedLarge,
            skippedBinary,
            skippedExcluded,
            skippedSample,
            sw.Elapsed);
    }

    private static void AddSample(List<SkippedPath> sample, string relPath, string reason)
    {
        if (sample.Count >= 50)
            return;
        sample.Add(new SkippedPath(relPath, reason));
    }

    private static void TryReplaceDatabase(string tmpPath, string dbPath)
    {
        // Best-effort: try a few times in case the old DB is momentarily locked.
        var backup = dbPath + ".bak";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Replace(tmpPath, dbPath, backup, ignoreMetadataErrors: true);
                    if (File.Exists(backup))
                        File.Delete(backup);
                }
                else
                {
                    File.Move(tmpPath, dbPath);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(120 * (attempt + 1));
            }
        }

        // If replace failed, keep tmp around for inspection.
        throw new IOException($"Could not replace index DB (in use?): '{dbPath}'. Temporary DB: '{tmpPath}'.");
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
             SELECT rowid, path, line_start, line_end, bm25(chunks), snippet(chunks, 4, '[', ']', ' … ', 24)
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
            var hitId = reader.GetInt64(0);
            var path = reader.GetString(1);
            var lineStart = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            var lineEnd = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var bm = reader.GetDouble(4);
            var snip = reader.IsDBNull(5) ? null : reader.GetString(5);
            hits.Add(new IndexHit(hitId, path, HitKinds.TextFts, bm, snip, lineStart, lineEnd));
        }

        return (new SearchResponse(FormatVersion, userQuery, dbPath, hits), null);
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
            SELECT rowid, path, line_start, line_end, extension, substr(body, 1, 1200)
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
        var lineStart = r.IsDBNull(2) ? 0 : r.GetInt32(2);
        var lineEnd = r.IsDBNull(3) ? 0 : r.GetInt32(3);
        var ext = r.IsDBNull(4) ? "" : r.GetString(4);
        var body = r.IsDBNull(5) ? null : r.GetString(5);

        var hit = new IndexHit(id, path, HitKinds.TextFts, 0, body, lineStart, lineEnd);
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
