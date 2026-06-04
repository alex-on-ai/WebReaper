using WebReaper.Mcp;
using Xunit;

namespace WebReaper.Mcp.AspNetCore.Tests;

// ADR-0086: LLM config resolution (per-call model override + env) and the
// actionable-error validation, as a truth table. No env reads here.
public class LlmConfigTests
{
    [Fact]
    public void Per_call_model_wins_over_env()
    {
        var c = LlmConfig.Resolve("gpt-4o-mini", "env-model", "https://api.openai.com/v1", "key");
        Assert.Equal("gpt-4o-mini", c.Model);
    }

    [Fact]
    public void Env_model_used_when_no_override()
    {
        var c = LlmConfig.Resolve(null, "env-model", "https://host/v1", null);
        Assert.Equal("env-model", c.Model);
        Assert.Null(c.ApiKey);
    }

    [Fact]
    public void Blank_override_falls_back_to_env_model()
    {
        Assert.Equal("env-model", LlmConfig.Resolve("   ", "env-model", "https://host", "k").Model);
    }

    [Fact]
    public void Missing_model_throws_actionably()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LlmConfig.Resolve(null, null, "https://host", "k"));
        Assert.Contains(LlmConfig.ModelEnvVar, ex.Message);
    }

    [Fact]
    public void Missing_base_url_throws_actionably()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LlmConfig.Resolve("model", null, null, "k"));
        Assert.Contains(LlmConfig.BaseUrlEnvVar, ex.Message);
    }

    [Fact]
    public void Blank_api_key_becomes_null()
    {
        Assert.Null(LlmConfig.Resolve("model", null, "https://host", "   ").ApiKey);
    }

    [Fact]
    public void Base_url_is_trimmed()
    {
        Assert.Equal("https://host", LlmConfig.Resolve("model", null, "  https://host  ", null).BaseUrl);
    }
}
