using Ignore;

namespace HybridCodebaseIndex.Core;

internal static class GitIgnoreRules
{
    internal static Ignore.Ignore TryLoad(string workspaceRoot)
    {
        var ig = new Ignore.Ignore();

        // Baseline denylist already handled by WorkspaceScanner.ShouldExcludePath.
        // Here we only apply gitignore-like rules.
        TryAddFileRules(ig, Path.Combine(workspaceRoot, ".gitignore"));
        TryAddFileRules(ig, Path.Combine(workspaceRoot, ".git", "info", "exclude"));

        return ig;
    }

    internal static bool IsIgnored(Ignore.Ignore rules, string relativePathUnix)
        => rules.IsIgnored(relativePathUnix);

    private static void TryAddFileRules(Ignore.Ignore rules, string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            // The library accepts raw lines (comments/empties are handled by lib).
            var lines = File.ReadAllLines(path);
            rules.Add(lines);
        }
        catch
        {
            // best-effort; treat as "no extra rules"
        }
    }
}
