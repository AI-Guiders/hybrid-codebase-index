using HybridCodebaseIndex.Core;

namespace HybridCodebaseIndex.Core.Tests;

public sealed class IndexSettingsEffectiveExtensionsTests
{
    [Fact]
    public void Empty_include_extensions_in_disk_settings_does_not_wipe_embedded_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hci-settings-merge-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "settings.toml"),
                """
                [fts]
                include_extensions = []
                """);

            var settings = IndexSettings.TryLoadFromIndexDirectory(dir);
            var effective = settings.GetEffectiveExtensions();
            Assert.Contains(".cs", effective, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(".md", effective, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
