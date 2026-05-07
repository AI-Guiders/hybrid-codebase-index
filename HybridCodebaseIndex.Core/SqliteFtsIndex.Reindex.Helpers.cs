using Microsoft.Data.Sqlite;

namespace HybridCodebaseIndex.Core;

internal static partial class SqliteFtsIndex
{
    private static bool IsFileChanged(SqliteConnection conn, SqliteTransaction tx, string path, long sizeBytes, long lastWriteUtcTicks)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT size_bytes, last_write_utc_ticks FROM file_state WHERE path=$p LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return true;
        var prevSize = r.GetInt64(0);
        var prevTicks = r.GetInt64(1);
        return prevSize != sizeBytes || prevTicks != lastWriteUtcTicks;
    }

    private static void UpsertFileState(SqliteConnection conn, SqliteTransaction tx, string path, long sizeBytes, long lastWriteUtcTicks)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO file_state(path, size_bytes, last_write_utc_ticks)
            VALUES($p, $s, $t)
            ON CONFLICT(path) DO UPDATE SET size_bytes=excluded.size_bytes, last_write_utc_ticks=excluded.last_write_utc_ticks;
            """;
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$s", sizeBytes);
        cmd.Parameters.AddWithValue("$t", lastWriteUtcTicks);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteFileState(SqliteConnection conn, SqliteTransaction tx, string path)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM file_state WHERE path=$p;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    private static IEnumerable<string> EnumerateStalePaths(SqliteConnection conn, SqliteTransaction tx, HashSet<string> seen)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT path FROM file_state;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var p = r.GetString(0);
            if (!seen.Contains(p))
                yield return p;
        }
    }

    private static void DeleteChunksForPath(SqliteConnection conn, SqliteTransaction tx, string path)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM chunks WHERE path=$p;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    private static void InsertChunk(
        SqliteConnection conn,
        SqliteTransaction tx,
        string path,
        string ext,
        int lineStart,
        int lineEnd,
        string body)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO chunks(path, extension, line_start, line_end, body)
            VALUES ($path, $ext, $ls, $le, $body);
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$ext", ext);
        cmd.Parameters.AddWithValue("$ls", lineStart);
        cmd.Parameters.AddWithValue("$le", lineEnd);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.ExecuteNonQuery();
    }

    private static void AddSkip(
        List<SkippedPath> sample,
        Dictionary<string, int> reasonCounts,
        Dictionary<string, int> prefixCounts,
        string relPath,
        string reason)
    {
        reasonCounts[reason] = reasonCounts.TryGetValue(reason, out var c) ? c + 1 : 1;
        var pfx = GetPathPrefix(relPath);
        prefixCounts[pfx] = prefixCounts.TryGetValue(pfx, out var pc) ? pc + 1 : 1;

        if (sample.Count >= 50)
            return;
        sample.Add(new SkippedPath(relPath, reason));
    }

    private static string GetPathPrefix(string relPath)
    {
        var p = relPath.Replace("\\", "/", StringComparison.Ordinal);
        var idx = p.IndexOf('/', StringComparison.Ordinal);
        return idx <= 0 ? p : p[..idx];
    }

    private static IReadOnlyList<(string PathPrefix, int Count)> TopPrefixes(Dictionary<string, int> prefixCounts)
        => prefixCounts
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(static kv => (kv.Key, kv.Value))
            .ToArray();
}

