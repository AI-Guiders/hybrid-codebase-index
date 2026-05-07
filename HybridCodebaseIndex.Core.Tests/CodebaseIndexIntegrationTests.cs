using HybridCodebaseIndex.Core;

namespace HybridCodebaseIndex.Core.Tests;

public sealed class CodebaseIndexIntegrationTests
{
    [Fact]
    public async Task Reindex_then_search_find_hit()
    {
        var ws = Path.Combine(Path.GetTempPath(), "hca-index-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(ws);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(ws, "note.md"), """
# Sample
Hello hybrid codebase index FTS5 smoke.
""");

            var svc = new CodebaseIndexService(".test-hybrid-index");
            await svc.FullReindexAsync(ws);

            var (resp, err) = await svc.SearchAsync(ws, "hybrid FTS5", topN: 5);
            Assert.Null(err);
            Assert.Contains(resp.Hits, h => h.Path.Replace('\\', '/') == "note.md" && h.HitKind == HitKinds.TextFts);

            var st = await svc.GetStatusAsync(ws);
            Assert.True(st.DatabaseExists);
            Assert.True(st.DocumentCount >= 1);
        }
        finally
        {
            TryDelete(ws);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // temp cleanup best-effort
        }
    }
}
