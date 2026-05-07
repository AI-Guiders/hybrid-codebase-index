using HybridCodebaseIndex.Core;

namespace HybridCodebaseIndex.Core.Tests;

public sealed class SqliteFtsMatchQueryTests
{
    [Fact]
    public void BuildMatchQuery_null_for_empty_whitespace()
    {
        Assert.Null(SqliteFtsIndex.BuildMatchQuery(""));
        Assert.Null(SqliteFtsIndex.BuildMatchQuery("   "));
    }

    [Fact]
    public void BuildMatchQuery_joins_tokens_with_and_and_prefix_star()
    {
        var q = SqliteFtsIndex.BuildMatchQuery("foo Bar");
        Assert.Equal("\"foo\"* AND \"Bar\"*", q);
    }
}
