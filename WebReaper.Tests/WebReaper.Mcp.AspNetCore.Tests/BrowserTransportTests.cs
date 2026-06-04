using WebReaper.Mcp;
using Xunit;

namespace WebReaper.Mcp.AspNetCore.Tests;

// ADR-0086: browser-transport selection is a closed truth table.
public class BrowserTransportTests
{
    [Fact]
    public void Browser_false_is_none()
    {
        var plan = BrowserTransport.Select(browser: false, cdpUrl: null);
        Assert.Equal(BrowserLaunchMode.None, plan.Mode);
        Assert.Null(plan.CdpUrl);
    }

    [Fact]
    public void Browser_true_without_cdp_url_launches()
    {
        var plan = BrowserTransport.Select(browser: true, cdpUrl: null);
        Assert.Equal(BrowserLaunchMode.Launch, plan.Mode);
        Assert.Null(plan.CdpUrl);
    }

    [Fact]
    public void Browser_true_with_cdp_url_connects()
    {
        var plan = BrowserTransport.Select(browser: true, cdpUrl: "  http://chrome:9222  ");
        Assert.Equal(BrowserLaunchMode.Connect, plan.Mode);
        Assert.Equal("http://chrome:9222", plan.CdpUrl);
    }

    [Fact]
    public void Cdp_url_is_ignored_when_browser_is_false()
    {
        var plan = BrowserTransport.Select(browser: false, cdpUrl: "http://chrome:9222");
        Assert.Equal(BrowserLaunchMode.None, plan.Mode);
    }

    [Fact]
    public void Whitespace_cdp_url_falls_back_to_launch()
    {
        Assert.Equal(BrowserLaunchMode.Launch, BrowserTransport.Select(true, "   ").Mode);
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData("", 4)]
    [InlineData("garbage", 4)]
    [InlineData("0", 4)]
    [InlineData("-2", 4)]
    [InlineData("1", 1)]
    [InlineData("16", 16)]
    public void ResolveMaxConcurrentBrowsers_parses_a_positive_int_or_defaults(string? raw, int expected) =>
        Assert.Equal(expected, BrowserTransport.ResolveMaxConcurrentBrowsers(raw));
}
