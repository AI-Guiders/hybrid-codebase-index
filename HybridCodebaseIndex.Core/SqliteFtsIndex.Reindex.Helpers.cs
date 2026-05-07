using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace HybridCodebaseIndex.Core;

internal static partial class SqliteFtsIndex
{
    private static string BuildArtifactAugmentationHeader(
        string workspaceRootNormalized,
        string absolutePath,
        string relPathUnix,
        string ext,
        string text)
    {
        // Keep this cheap and best-effort: it must never break indexing.
        // The goal is to improve searchability for Razor/AXAML without adding a full parser.
        try
        {
            ext = ext.ToLowerInvariant();
            if (ext is not ".razor" and not ".axaml" and not ".cs")
                return "";

            var sb = new StringBuilder(capacity: 512);

            // Pairing: .razor <-> .razor.cs, .axaml <-> .axaml.cs
            if (ext is ".razor" or ".axaml")
            {
                var codeBehind = absolutePath + ".cs";
                if (File.Exists(codeBehind))
                {
                    var rel = WorkspaceScanner.RelativePath(workspaceRootNormalized, codeBehind).Replace("\\", "/", StringComparison.Ordinal);
                    sb.Append("__hci_pair:");
                    sb.Append(rel);
                    sb.AppendLine();
                }
            }
            else if (ext == ".cs")
            {
                if (absolutePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
                {
                    var razor = absolutePath[..^3]; // drop ".cs"
                    if (File.Exists(razor))
                    {
                        var rel = WorkspaceScanner.RelativePath(workspaceRootNormalized, razor).Replace("\\", "/", StringComparison.Ordinal);
                        sb.Append("__hci_pair:");
                        sb.Append(rel);
                        sb.AppendLine();
                    }
                }
                else if (absolutePath.EndsWith(".axaml.cs", StringComparison.OrdinalIgnoreCase))
                {
                    var axaml = absolutePath[..^3]; // drop ".cs"
                    if (File.Exists(axaml))
                    {
                        var rel = WorkspaceScanner.RelativePath(workspaceRootNormalized, axaml).Replace("\\", "/", StringComparison.Ordinal);
                        sb.Append("__hci_pair:");
                        sb.Append(rel);
                        sb.AppendLine();
                    }
                }
            }

            if (ext == ".razor")
            {
                sb.AppendLine("__hci_kind:razor");

                foreach (Match m in Regex.Matches(text, @"(?m)^\s*@page\s+(?<route>.+?)\s*$"))
                {
                    var route = m.Groups["route"].Value.Trim().Trim('"', '\'');
                    if (route.Length > 0)
                    {
                        sb.Append("__hci_page:");
                        sb.Append(route);
                        sb.AppendLine();
                    }
                }

                foreach (Match m in Regex.Matches(text, @"(?m)^\s*@inject\s+(?<type>\S+)\s+(?<name>\S+)\s*$"))
                {
                    var type = m.Groups["type"].Value.Trim();
                    var name = m.Groups["name"].Value.Trim();
                    if (type.Length > 0 && name.Length > 0)
                    {
                        sb.Append("__hci_inject:");
                        sb.Append(type);
                        sb.Append(' ');
                        sb.Append(name);
                        sb.AppendLine();
                    }
                }

                var comps = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in Regex.Matches(text, @"<(?<tag>[A-Z][A-Za-z0-9_\.]+)\b"))
                {
                    var tag = m.Groups["tag"].Value;
                    if (tag.Length > 0)
                        comps.Add(tag);
                    if (comps.Count >= 50)
                        break;
                }

                foreach (var c in comps)
                {
                    sb.Append("__hci_component:");
                    sb.Append(c);
                    sb.AppendLine();
                }
            }

            if (ext == ".axaml")
            {
                sb.AppendLine("__hci_kind:axaml");

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(text, @"\bx:Name\s*=\s*""(?<n>[^""]+)"""))
                {
                    var n = m.Groups["n"].Value.Trim();
                    if (n.Length > 0)
                        names.Add(n);
                    if (names.Count >= 80)
                        break;
                }

                foreach (var n in names)
                {
                    sb.Append("__hci_xname:");
                    sb.Append(n);
                    sb.AppendLine();
                }

                var binds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(text, @"\{Binding\s+(?<p>[^}\s,]+)"))
                {
                    var p = m.Groups["p"].Value.Trim();
                    if (p.Length > 0)
                        binds.Add(p);
                    if (binds.Count >= 80)
                        break;
                }

                foreach (var b in binds)
                {
                    sb.Append("__hci_binding:");
                    sb.Append(b);
                    sb.AppendLine();
                }

                foreach (Match m in Regex.Matches(text, @"\bClasses\s*=\s*""(?<c>[^""]+)"""))
                {
                    var cls = m.Groups["c"].Value.Trim();
                    if (cls.Length > 0)
                    {
                        sb.Append("__hci_classes:");
                        sb.Append(cls);
                        sb.AppendLine();
                    }
                }

                foreach (Match m in Regex.Matches(text, @"avares:[^\s""']+"))
                {
                    var uri = m.Value.Trim();
                    if (uri.Length > 0)
                    {
                        sb.Append("__hci_avares:");
                        sb.Append(uri);
                        sb.AppendLine();
                    }
                }
            }

            if (sb.Length == 0)
                return "";

            // Separate header from original content, so snippets remain readable.
            sb.AppendLine();
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

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

