using WebReaper.Mcp.AspNetCore;
using Xunit;

namespace WebReaper.Mcp.AspNetCore.Tests;

// ADR-0086: the bearer-auth decision as a truth table.
public class BearerAuthTests
{
    [Fact]
    public void No_configured_token_authorizes_everything()
    {
        Assert.True(BearerAuth.IsAuthorized(null, null));
        Assert.True(BearerAuth.IsAuthorized(null, "Bearer anything"));
        Assert.True(BearerAuth.IsAuthorized("", "whatever"));
    }

    [Fact]
    public void Matching_bearer_token_is_authorized()
    {
        Assert.True(BearerAuth.IsAuthorized("secret", "Bearer secret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret")]              // missing the Bearer scheme
    [InlineData("Bearer wrong")]        // wrong token
    [InlineData("bearer secret")]       // scheme is case-sensitive
    [InlineData("Basic secret")]        // wrong scheme
    public void Missing_or_wrong_credentials_are_rejected(string? header)
    {
        Assert.False(BearerAuth.IsAuthorized("secret", header));
    }
}
