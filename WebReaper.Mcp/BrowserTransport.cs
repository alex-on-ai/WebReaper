using WebReaper.Builders;
using WebReaper.Cdp;

namespace WebReaper.Mcp;

// ADR-0086: how a browser=true tool gets a browser. Lives in the shared
// satellite (not the HTTP host) so both the stdio and HTTP hosts honor
// WEBREAPER_CDP_URL: connect to a shared Chromium / browserless sidecar when
// set, otherwise launch managed Chromium. The selection is a pure decision
// (a closed truth table under test); the apply + launch gate are the impure
// edges.

public enum BrowserLaunchMode { None, Launch, Connect }

public readonly record struct BrowserTransportPlan(BrowserLaunchMode Mode, string? CdpUrl);

public static class BrowserTransport
{
    public const string CdpUrlEnvVar = "WEBREAPER_CDP_URL";
    public const string MaxConcurrentBrowsersEnvVar = "WEBREAPER_MCP_MAX_CONCURRENT_BROWSERS";
    public const int DefaultMaxConcurrentBrowsers = 4;

    /// <summary>Pure: launch vs connect from the browser flag and an optional CDP url.</summary>
    public static BrowserTransportPlan Select(bool browser, string? cdpUrl)
    {
        if (!browser) return new BrowserTransportPlan(BrowserLaunchMode.None, null);
        var trimmed = string.IsNullOrWhiteSpace(cdpUrl) ? null : cdpUrl.Trim();
        return trimmed is null
            ? new BrowserTransportPlan(BrowserLaunchMode.Launch, null)
            : new BrowserTransportPlan(BrowserLaunchMode.Connect, trimmed);
    }

    /// <summary>Pure: a positive integer cap, else the default. Tolerates null / garbage.</summary>
    public static int ResolveMaxConcurrentBrowsers(string? raw) =>
        int.TryParse(raw, out var n) && n > 0 ? n : DefaultMaxConcurrentBrowsers;

    // Apply the plan to a builder. Connect uses the CDP-url overload (no
    // process spawn); Launch spawns managed Chromium; None leaves HTTP.
    internal static ScraperEngineBuilder ApplyBrowser(this ScraperEngineBuilder builder, BrowserTransportPlan plan) =>
        plan.Mode switch
        {
            BrowserLaunchMode.Connect => builder.WithCdpPageLoader(plan.CdpUrl!),
            BrowserLaunchMode.Launch => builder.WithCdpPageLoader(new CdpLaunchOptions()),
            _ => builder,
        };
}

// Caps concurrent managed-Chromium launches across tool calls (parallel HTTP
// requests would otherwise spawn unbounded browsers). Connect-mode calls do
// not launch, so they never take a permit. The semaphore is sized once from
// the environment on first use.
internal static class BrowserLaunchGate
{
    private static readonly Lazy<SemaphoreSlim> Gate = new(() =>
    {
        var max = BrowserTransport.ResolveMaxConcurrentBrowsers(
            Environment.GetEnvironmentVariable(BrowserTransport.MaxConcurrentBrowsersEnvVar));
        return new SemaphoreSlim(max, max);
    });

    public static Task WaitAsync() => Gate.Value.WaitAsync();
    public static void Release() => Gate.Value.Release();
}
