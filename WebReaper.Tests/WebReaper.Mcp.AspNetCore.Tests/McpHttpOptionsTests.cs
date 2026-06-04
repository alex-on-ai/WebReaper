using WebReaper.Mcp.AspNetCore;
using Xunit;

namespace WebReaper.Mcp.AspNetCore.Tests;

// ADR-0086: the config validator is a closed truth table (no IO). The
// load-bearing rule is "refuse a non-loopback bind without a token".
public class McpHttpOptionsTests
{
    [Fact]
    public void Loopback_without_token_is_allowed_and_auth_is_off()
    {
        var options = McpHttpOptions.Validate(null, ["http://localhost:5000"]);
        Assert.Null(options.BearerToken);
        Assert.False(options.RequireAuth);
    }

    [Fact]
    public void Loopback_with_token_enables_auth()
    {
        var options = McpHttpOptions.Validate("secret", ["http://127.0.0.1:8080"]);
        Assert.Equal("secret", options.BearerToken);
        Assert.True(options.RequireAuth);
    }

    [Fact]
    public void Non_loopback_without_token_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => McpHttpOptions.Validate(null, ["http://0.0.0.0:8080"]));
        Assert.Contains(McpHttpOptions.TokenEnvVar, ex.Message);
    }

    [Fact]
    public void Non_loopback_with_token_is_allowed()
    {
        var options = McpHttpOptions.Validate("secret", ["http://0.0.0.0:8080"]);
        Assert.True(options.RequireAuth);
    }

    [Fact]
    public void Whitespace_token_is_treated_as_no_token()
    {
        Assert.Throws<InvalidOperationException>(
            () => McpHttpOptions.Validate("   ", ["http://example.com"]));
        Assert.False(McpHttpOptions.Validate("  ", ["http://localhost"]).RequireAuth);
    }

    [Theory]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("https://localhost", true)]
    [InlineData("http://0.0.0.0:8080", false)]
    [InlineData("http://+:80", false)]
    [InlineData("http://*:80", false)]
    [InlineData("http://example.com", false)]
    [InlineData("http://192.168.1.10:8080", false)]
    public void IsLoopback_classifies_bind_urls(string url, bool expected) =>
        Assert.Equal(expected, McpHttpOptions.IsLoopback(url));

    [Fact]
    public void Mixed_binds_require_a_token_if_any_is_non_loopback()
    {
        Assert.Throws<InvalidOperationException>(
            () => McpHttpOptions.Validate(null, ["http://localhost:5000", "http://0.0.0.0:8080"]));
    }
}
