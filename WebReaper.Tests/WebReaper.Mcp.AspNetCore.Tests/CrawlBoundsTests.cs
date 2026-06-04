using WebReaper.Mcp;
using Xunit;

namespace WebReaper.Mcp.AspNetCore.Tests;

// ADR-0086: the crawl page-cap guard.
public class CrawlBoundsTests
{
    [Fact]
    public void Default_value_passes_through() =>
        Assert.Equal(50, CrawlBounds.Validate(CrawlBounds.DefaultMaxPages));

    [Fact]
    public void Within_range_is_unchanged() =>
        Assert.Equal(200, CrawlBounds.Validate(200));

    [Fact]
    public void Above_the_hard_cap_is_clamped() =>
        Assert.Equal(CrawlBounds.MaxAllowed, CrawlBounds.Validate(5000));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Non_positive_is_rejected(int n) =>
        Assert.Throws<ArgumentException>(() => CrawlBounds.Validate(n));

    [Fact]
    public void Default_is_small_relative_to_the_cap() =>
        Assert.True(CrawlBounds.DefaultMaxPages < CrawlBounds.MaxAllowed);
}
