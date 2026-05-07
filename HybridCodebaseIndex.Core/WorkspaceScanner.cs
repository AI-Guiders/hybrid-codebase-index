namespace HybridCodebaseIndex.Core;

internal static class WorkspaceScanner
{
    internal const long MaxIndexedFileBytes = 512 * 1024;

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
                if (ShouldExcludePath(file))
                    continue;
                var len = TryGetFileLength(file);
                if (len is null)
                    continue;
                yield return file;
            }
        }
    }

    private static long? TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return null;
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
}
