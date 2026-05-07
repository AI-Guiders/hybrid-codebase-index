using Tomlyn;
using Tomlyn.Model;

namespace HybridCodebaseIndex.Core;

public sealed record IndexSettings(
    bool IncludeCsInFts,
    IReadOnlyList<string> ExtraIncludeRoots,
    IReadOnlyList<string>? IncludeExtensions,
    IReadOnlyList<string>? ExcludeExtensions)
{
    public static IndexSettings Default { get; } = new(
        IncludeCsInFts: true,
        ExtraIncludeRoots: [],
        IncludeExtensions: null,
        ExcludeExtensions: null);

    internal static readonly string[] DefaultExtensionsWithoutCs =
    [
        ".md", ".mdx", ".csproj", ".slnx", ".props", ".targets", ".toml",
        ".editorconfig", ".json", ".yml", ".yaml", ".razor",
        ".css", ".scss", ".html", ".axaml",
    ];

    internal static readonly string[] DefaultExtensionsWithCs =
    [
        ..DefaultExtensionsWithoutCs,
        ".cs",
    ];

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
            var includeExt = NormalizeExtensions(ReadStringArray(model, "include_extensions"));
            var excludeExt = NormalizeExtensions(ReadStringArray(model, "exclude_extensions"));

            return new IndexSettings(includeCs, extraRoots, includeExt, excludeExt);
        }
        catch
        {
            return Default;
        }
    }

    public IReadOnlyList<string> GetEffectiveExtensions()
    {
        IReadOnlyList<string> baseList;
        if (IncludeExtensions is { Count: > 0 })
        {
            baseList = IncludeExtensions;
        }
        else
        {
            baseList = IncludeCsInFts ? DefaultExtensionsWithCs : DefaultExtensionsWithoutCs;
        }

        if (ExcludeExtensions is not { Count: > 0 })
            return baseList;

        var deny = new HashSet<string>(ExcludeExtensions, StringComparer.OrdinalIgnoreCase);
        return baseList.Where(e => !deny.Contains(e)).ToArray();
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

    private static List<string>? NormalizeExtensions(List<string>? raw)
    {
        if (raw is null || raw.Count == 0)
            return raw;

        var list = new List<string>(raw.Count);
        foreach (var s0 in raw)
        {
            var s = s0.Trim();
            if (s.Length == 0)
                continue;
            if (!s.StartsWith(".", StringComparison.Ordinal))
                s = "." + s;
            list.Add(s.ToLowerInvariant());
        }
        return list;
    }
}

