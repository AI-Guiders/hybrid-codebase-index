namespace HybridCodebaseIndex.Mcp.Tests;

public sealed class ManPagesTests
{
    [Fact]
    public void Toc_lists_search_status_reindex_watch()
    {
        var toc = ManPages.Resolve(null);
        Assert.Contains("codebase_index_search", toc, StringComparison.Ordinal);
        Assert.Contains("codebase_index_status", toc, StringComparison.Ordinal);
        Assert.Contains("codebase_index_reindex", toc, StringComparison.Ordinal);
        Assert.Contains("codebase_index_watch", toc, StringComparison.Ordinal);
        Assert.Contains("not shell", toc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_page_has_empty_hits_ladder_and_never_auto_reindex()
    {
        var page = ManPages.Resolve("codebase_index_search");
        Assert.Contains("WHAT EMPTY HITS MEAN", page, StringComparison.Ordinal);
        Assert.Contains("auto-reindex on every empty hitlist", page, StringComparison.Ordinal);
        Assert.Contains("codebase_index_status first", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codebase_index_status")]
    [InlineData("codebase_index_reindex")]
    [InlineData("codebase_index_watch")]
    public void Known_pages_are_non_empty(string tool)
    {
        var page = ManPages.Resolve(tool);
        Assert.StartsWith("NAME", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown man page", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_page_lists_known_tools()
    {
        var page = ManPages.Resolve("no_such_tool");
        Assert.Contains("Unknown man page", page, StringComparison.Ordinal);
        Assert.Contains("codebase_index_search", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Handle_man_via_ToolHandlers()
    {
        var text = ToolHandlers.Handle("man", new Dictionary<string, System.Text.Json.JsonElement>());
        Assert.Contains("PAGES", text, StringComparison.Ordinal);
    }
}
