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

    private static ReindexSummary FullRebuild(string workspaceRoot, string dbPath, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));

        var filesIndexed = 0;
        var skippedLarge = 0;
        var skippedBinary = 0;
        var skippedExcluded = 0;
        var skippedSample = new List<SkippedPath>(capacity: 64);

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

            var candidates = new List<string>(capacity: 8192);
            foreach (var root in roots)
                candidates.AddRange(WorkspaceScanner.EnumerateIndexableFiles(root, extensions));

            var gitIgnore = GitIgnoreRules.TryLoad(workspaceRoot);

            foreach (var absolute in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (WorkspaceScanner.ShouldExcludePath(absolute, settings.ExcludePathSegments))
                {
                    skippedExcluded++;
                    AddSample(skippedSample, WorkspaceScanner.RelativePath(workspaceRoot, absolute), "denylist");
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
            skippedSample,
            sw.Elapsed);
    }

    private static void AddSample(List<SkippedPath> sample, string relPath, string reason)
    {
        if (sample.Count >= 50)
            return;
        sample.Add(new SkippedPath(relPath, reason));
    }

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
        {
            IndexSettings.TryLoadFromIndexDirectoryWithDiagnostics(
                Path.GetDirectoryName(dbPath),
                out _,
                out var src,
                out var err);
            return new IndexStatus(FormatVersion, dbPath, false, 0, false, null, workspaceRoot, null, null, src, err, null, null);
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
            out _,
            out var settingsSource,
            out var settingsParseError);
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT count(*) FROM chunks;";
        var docCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
        var mayBeStale = string.Equals(reindexState, "running", StringComparison.OrdinalIgnoreCase);
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
