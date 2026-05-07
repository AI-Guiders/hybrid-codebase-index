namespace HybridCodebaseIndex.Core;

using System.Text;

internal static class WorkspaceScanner
{
    internal const long MaxIndexedFileBytes = 512 * 1024;
    internal const int ChunkLines = 110;
    internal const int ChunkOverlapLines = 15;

    private static readonly string[] IndexableGlobExtensions =
    [
        ".md", ".mdx", ".csproj", ".slnx", ".props", ".targets", ".toml",
        ".editorconfig", ".json", ".yml", ".yaml", ".cs", ".razor",
        ".css", ".scss", ".html", ".axaml",
    ];

    internal static IEnumerable<string> EnumerateIndexableFiles(string workspaceRoot)
    {
        var normalized = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        if (!Directory.Exists(normalized))
            yield break;

        foreach (var ext in IndexableGlobExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(normalized, $"*{ext}", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    internal static bool ShouldExcludePath(string fullPath)
    {
        // Нормализуем для сравнения сегментов пути.
        foreach (var token in new[]
                 {
                     $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                     $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                     $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                     $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                     $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
                     $"{Path.DirectorySeparatorChar}.idea{Path.DirectorySeparatorChar}",
                     $"{Path.DirectorySeparatorChar}.cache{Path.DirectorySeparatorChar}",
                     $"{Path.DirectorySeparatorChar}.cascade-ide{Path.DirectorySeparatorChar}",
                 })
        {
            if (fullPath.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool LooksBinary(ReadOnlySpan<byte> probe)
    {
        foreach (var b in probe)
        {
            if (b == 0)
                return true;
        }

        return false;
    }

    internal static string RelativePath(string workspaceRoot, string filePath)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        filePath = Path.GetFullPath(filePath);
        return Path.GetRelativePath(workspaceRoot, filePath);
    }

    internal static IEnumerable<(int lineStart, int lineEnd, string body)> ChunkByLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        // Normalize line endings so line accounting is stable.
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 0)
            yield break;

        var chunk = Math.Max(20, ChunkLines);
        var overlap = Math.Clamp(ChunkOverlapLines, 0, chunk - 1);
        var step = Math.Max(1, chunk - overlap);

        for (var i = 0; i < lines.Length; i += step)
        {
            var endExclusive = Math.Min(lines.Length, i + chunk);
            var sb = new StringBuilder(capacity: 4096);
            for (var j = i; j < endExclusive; j++)
            {
                if (j > i) sb.Append('\n');
                sb.Append(lines[j]);
            }

            // 1-based inclusive line numbers.
            yield return (i + 1, endExclusive, sb.ToString());

            if (endExclusive == lines.Length)
                yield break;
        }
    }
}
