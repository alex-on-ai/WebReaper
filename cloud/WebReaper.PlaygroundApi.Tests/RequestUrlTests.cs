using WebReaper.PlaygroundApi.Ssrf;
using Xunit;

namespace WebReaper.PlaygroundApi.Tests;

// Mirrors SsrfPolicyTests' Theory/InlineData style. RequestUrl.TryNormalizeUrl
// is the request-URL shape guard shared by the Tier A and Tier B scrapers,
// deduped from two byte-identical private copies. SsrfPolicy guards the
// resolved address; RequestUrl guards the URL shape.
public class RequestUrlTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path?q=1#frag")]
    [InlineData("  https://example.com  ")] // surrounding whitespace is trimmed
    [InlineData("HTTPS://EXAMPLE.COM")]      // scheme compared case-insensitively
    public void Accepts_absolute_http_and_https(string url)
    {
        Assert.True(RequestUrl.TryNormalizeUrl(url, out var uri));
        Assert.True(uri.IsAbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]          // no scheme, not absolute
    [InlineData("/path/only")]           // relative
    [InlineData("ftp://example.com")]    // non-http(s) scheme
    [InlineData("file:///etc/passwd")]   // non-http(s) scheme
    [InlineData("javascript:alert(1)")]  // non-http(s) scheme
    public void Rejects_empty_relative_and_non_http_schemes(string? url)
    {
        Assert.False(RequestUrl.TryNormalizeUrl(url, out var uri));
        Assert.Null(uri);
    }

    [Fact]
    public void Trims_whitespace_before_parsing()
    {
        Assert.True(RequestUrl.TryNormalizeUrl("\t https://example.com/x \n", out var uri));
        Assert.Equal("https://example.com/x", uri.ToString());
    }
}
