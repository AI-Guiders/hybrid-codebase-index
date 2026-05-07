using Tomlyn;
using Tomlyn.Model;

namespace HybridCodebaseIndex.Core;

public sealed record IndexSettings(
    bool IncludeCsInFts,
    IReadOnlyList<string> ExtraIncludeRoots,
    IReadOnlyList<string> ExcludeRoots,
    IReadOnlyList<string>? IncludeExtensions,
    IReadOnlyList<string>? ExcludeExtensions,
    IReadOnlyList<string> ExcludePathSegments,
    IReadOnlyList<string> IgnoreFiles,
    long MaxIndexedFileBytes,
    int ChunkLines,
    int ChunkOverlapLines,
    int BinaryProbeBytes)
{
    public static IndexSettings Default { get; } = new(
        IncludeCsInFts: true,
        ExtraIncludeRoots: [],
        ExcludeRoots: [],
        IncludeExtensions: null,
        ExcludeExtensions: null,
        ExcludePathSegments: [],
        IgnoreFiles: [],
        MaxIndexedFileBytes: 0,
        ChunkLines: 0,
        ChunkOverlapLines: 0,
        BinaryProbeBytes: 0);

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
        var excludeRoots = ReadStringArray(diskModel, embeddedModel, "exclude_roots") ?? [];
        var includeExt = NormalizeExtensions(ReadStringArray(diskModel, embeddedModel, "include_extensions"));
        var excludeExt = NormalizeExtensions(ReadStringArray(diskModel, embeddedModel, "exclude_extensions"));
        var excludeSegments = ReadStringArray(diskModel, embeddedModel, "exclude_path_segments") ?? [];
        var ignoreFiles = ReadStringArray(diskModel, embeddedModel, "ignore_files") ?? [];

        var maxBytes = ReadLong(diskModel, embeddedModel, "max_indexed_file_bytes") ?? 0;
        var chunkLines = ReadInt(diskModel, embeddedModel, "chunk_lines") ?? 0;
        var overlapLines = ReadInt(diskModel, embeddedModel, "chunk_overlap_lines") ?? 0;
        var probeBytes = ReadInt(diskModel, embeddedModel, "binary_probe_bytes") ?? 0;

        settings = new IndexSettings(includeCs, extraRoots, excludeRoots, includeExt, excludeExt, excludeSegments, ignoreFiles, maxBytes, chunkLines, overlapLines, probeBytes);
        return true;
    }

    public long GetEffectiveMaxIndexedFileBytes()
        => MaxIndexedFileBytes > 0 ? MaxIndexedFileBytes : 512 * 1024;

    public int GetEffectiveChunkLines()
        => ChunkLines > 0 ? ChunkLines : 110;

    public int GetEffectiveChunkOverlapLines()
        => ChunkOverlapLines > 0 ? ChunkOverlapLines : 15;

    public int GetEffectiveBinaryProbeBytes()
        => BinaryProbeBytes > 0 ? BinaryProbeBytes : 8192;

    public IReadOnlyList<string> GetEffectiveExtensions()
    {
        IReadOnlyList<string> baseList;
        if (IncludeExtensions is { Count: > 0 })
        {
            baseList = IncludeExtensions;
        }
        else
        {
            // Embedded TOML is the canonical source of defaults; if it failed to load,
            // we prefer an explicit empty set instead of silently indexing an implicit list.
            baseList = [];
        }

        IEnumerable<string> filtered = baseList;
        if (ExcludeExtensions is { Count: > 0 })
        {
            var deny = new HashSet<string>(ExcludeExtensions, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(e => !deny.Contains(e));
        }

        // include_cs_in_fts is a first-class toggle; treat it as stronger than include_extensions defaults.
        if (!IncludeCsInFts)
            filtered = filtered.Where(static e => !string.Equals(e, ".cs", StringComparison.OrdinalIgnoreCase));
        else if (baseList.Count > 0 && !filtered.Contains(".cs", StringComparer.OrdinalIgnoreCase))
            filtered = filtered.Concat([".cs"]);

        return filtered.ToArray();
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

    private static int? ReadInt(TomlTable? overlay, TomlTable? baseModel, string key)
    {
        if (overlay is not null && overlay.TryGetValue(key, out var v) && v is long l)
            return l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
        if (baseModel is not null && baseModel.TryGetValue(key, out v) && v is long ll)
            return ll is >= int.MinValue and <= int.MaxValue ? (int)ll : null;
        return null;
    }

    private static long? ReadLong(TomlTable? overlay, TomlTable? baseModel, string key)
    {
        if (overlay is not null && overlay.TryGetValue(key, out var v) && v is long l)
            return l;
        if (baseModel is not null && baseModel.TryGetValue(key, out v) && v is long ll)
            return ll;
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

