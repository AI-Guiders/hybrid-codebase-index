using Tomlyn;
using Tomlyn.Model;

namespace HybridCodebaseIndex.Core;

public sealed record IndexSettings(
    bool IncludeCsInFts,
    IReadOnlyList<string> ExtraIncludeRoots)
{
    public static IndexSettings Default { get; } = new(
        IncludeCsInFts: true,
        ExtraIncludeRoots: []);

    public static IndexSettings TryLoad(string workspaceRoot, string indexDirectoryRelative)
    {
        try
        {
            var root = Path.GetFullPath(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
            var dir = Path.Combine(root, indexDirectoryRelative.TrimStart(Path.DirectorySeparatorChar, '/'));
            var path = Path.Combine(dir, "settings.toml");
            if (!File.Exists(path))
                return Default;

            var text = File.ReadAllText(path);
            var model = Toml.ToModel(text) as TomlTable;
            if (model is null)
                return Default;

            var includeCs = ReadBool(model, "include_cs_in_fts") ?? Default.IncludeCsInFts;
            var extraRoots = ReadStringArray(model, "extra_include_roots") ?? [];

            return new IndexSettings(includeCs, extraRoots);
        }
        catch
        {
            return Default;
        }
    }

    private static bool? ReadBool(TomlTable t, string key)
        => t.TryGetValue(key, out var v) && v is bool b ? b : null;

    private static List<string>? ReadStringArray(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var v) || v is not TomlArray arr)
            return null;
        var list = new List<string>(arr.Count);
        foreach (var it in arr)
        {
            if (it is string s && !string.IsNullOrWhiteSpace(s))
                list.Add(s.Trim());
        }
        return list;
    }
}

