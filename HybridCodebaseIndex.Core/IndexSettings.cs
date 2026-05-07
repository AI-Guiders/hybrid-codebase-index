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

    public static IndexSettings TryLoadFromIndexDirectory(string? indexDirectory)
    {
        _ = TryLoadFromIndexDirectoryWithDiagnostics(indexDirectory, out var settings, out _, out _);
        return settings;
    }

    public static bool TryLoadFromIndexDirectoryWithDiagnostics(
        string? indexDirectory,
        out IndexSettings settings,
        out string settingsSource,
        out string? settingsParseError)
    {
        settings = Default;
        settingsSource = "default";
        settingsParseError = null;

        if (string.IsNullOrWhiteSpace(indexDirectory))
            return false;

        var dir = Path.GetFullPath(indexDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var path = Path.Combine(dir, "settings.toml");

        var embeddedModel = TryReadEmbeddedModel(out var embeddedErr);
        string? diskErr = null;
        var diskModel = File.Exists(path) ? TryReadDiskModel(path, out diskErr) : null;

        settingsParseError = diskErr ?? embeddedErr;

        if (embeddedModel is not null && diskModel is not null)
            settingsSource = "embedded+disk";
        else if (diskModel is not null)
            settingsSource = "disk";
        else if (embeddedModel is not null)
            settingsSource = "embedded";

        // Merge: embedded = base, disk = overlay.
        var includeCs = ReadBool(diskModel, embeddedModel, "include_cs_in_fts") ?? Default.IncludeCsInFts;
        var extraRoots = ReadStringArray(diskModel, embeddedModel, "extra_include_roots") ?? [];
        var includeExt = NormalizeExtensions(ReadStringArray(diskModel, embeddedModel, "include_extensions"));
        var excludeExt = NormalizeExtensions(ReadStringArray(diskModel, embeddedModel, "exclude_extensions"));

        settings = new IndexSettings(includeCs, extraRoots, includeExt, excludeExt);
        return true;
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

    private static TomlTable? TryReadEmbeddedModel(out string? error)
    {
        error = null;
        try
        {
            if (!BundledContent.TryReadEmbeddedText("DefaultSettings/settings.default.toml", out var embedded))
                return null;
            return TomlSerializer.Deserialize<TomlTable>(embedded);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static TomlTable? TryReadDiskModel(string path, out string? error)
    {
        error = null;
        try
        {
            var text = File.ReadAllText(path);
            return TomlSerializer.Deserialize<TomlTable>(text);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static bool? ReadBool(TomlTable? overlay, TomlTable? baseModel, string key)
    {
        if (overlay is not null && overlay.TryGetValue(key, out var v) && v is bool b)
            return b;
        if (baseModel is not null && baseModel.TryGetValue(key, out v) && v is bool bb)
            return bb;
        return null;
    }

    private static List<string>? ReadStringArray(TomlTable? overlay, TomlTable? baseModel, string key)
    {
        TomlArray? arr = null;
        if (overlay is not null && overlay.TryGetValue(key, out var v) && v is TomlArray a)
            arr = a;
        else if (baseModel is not null && baseModel.TryGetValue(key, out v) && v is TomlArray aa)
            arr = aa;
        if (arr is null)
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

