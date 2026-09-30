using WebReaper.Cli;
using WebReaper.Cli.Commands;

namespace WebReaper.Cli.Tests;

/// <summary>The xUnit collection for tests that set process environment
/// variables. xUnit runs it after the parallel collections, never alongside
/// them: a CLI subprocess started meanwhile (<see cref="CliEndToEndTests"/>)
/// would inherit the mutated environment.</summary>
[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

/// <summary>
/// ADR-0056. The ScrapeCommand's flag-parsing contract: --browser,
/// --stealth, --auto-stealth, --no-auto-stealth, --browser-cdp-url +
/// their composition (e.g. --stealth implies --browser).
/// </summary>
[Collection("ProcessEnvironment")]
public class ScrapeContextTests
{
    [Fact]
    public void Plain_url_no_browser()
    {
        var ctx = Parse("scrape", "https://example.com");
        Assert.False(ctx.Browser);
        Assert.False(ctx.Stealth);
        Assert.False(ctx.AutoStealth);
        Assert.False(ctx.NoAutoStealth);
    }

    [Fact]
    public void Browser_flag_sets_browser()
    {
        var ctx = Parse("scrape", "https://example.com", "--browser");
        Assert.True(ctx.Browser);
    }

    [Fact]
    public void Bare_host_url_defaults_to_https()
    {
        var ctx = Parse("scrape", "alexpavlov.dev");
        Assert.Equal("https://alexpavlov.dev", ctx.Url);
    }

    [Fact]
    public void Explicit_scheme_url_is_preserved()
    {
        var ctx = Parse("scrape", "http://example.com");
        Assert.Equal("http://example.com", ctx.Url);
    }

    [Fact]
    public void Cdp_url_flag_implies_browser()
    {
        var ctx = Parse("scrape", "https://example.com", "--browser-cdp-url", "ws://localhost:9222");
        Assert.True(ctx.Browser);
        Assert.Equal("ws://localhost:9222", ctx.CdpUrl);
    }

    [Fact]
    public void Stealth_flag_implies_browser()
    {
        var ctx = Parse("scrape", "https://example.com", "--stealth");
        Assert.True(ctx.Browser);
        Assert.True(ctx.Stealth);
    }

    [Fact]
    public void Auto_stealth_flag()
    {
        var ctx = Parse("scrape", "https://example.com", "--browser", "--auto-stealth");
        Assert.True(ctx.AutoStealth);
    }

    [Fact]
    public void No_auto_stealth_flag()
    {
        var ctx = Parse("scrape", "https://example.com", "--browser", "--no-auto-stealth");
        Assert.True(ctx.NoAutoStealth);
    }

    [Fact]
    public void Auto_stealth_env_var_1_enables_auto_stealth()
    {
        // Snapshot + restore — the env-var read in ParseContext is unguarded.
        var prior = Environment.GetEnvironmentVariable("WEBREAPER_AUTO_STEALTH");
        try
        {
            Environment.SetEnvironmentVariable("WEBREAPER_AUTO_STEALTH", "1");
            var ctx = Parse("scrape", "https://example.com", "--browser");
            Assert.True(ctx.AutoStealth);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBREAPER_AUTO_STEALTH", prior);
        }
    }

    [Fact]
    public void Auto_stealth_env_var_true_enables_auto_stealth_case_insensitive()
    {
        var prior = Environment.GetEnvironmentVariable("WEBREAPER_AUTO_STEALTH");
        try
        {
            Environment.SetEnvironmentVariable("WEBREAPER_AUTO_STEALTH", "TRUE");
            var ctx = Parse("scrape", "https://example.com", "--browser");
            Assert.True(ctx.AutoStealth);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBREAPER_AUTO_STEALTH", prior);
        }
    }

    private static ScrapeCommand.ScrapeContext Parse(params string[] argv)
    {
        var args = Args.Parse(argv);
        return ScrapeCommand.ParseContext(args);
    }
}
