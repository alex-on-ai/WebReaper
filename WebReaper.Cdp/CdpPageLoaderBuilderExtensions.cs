using WebReaper.Builders;

namespace WebReaper.Cdp;

/// <summary>
/// ADR-0052: registers the raw-CDP <see cref="CdpPageLoadTransport"/> for the
/// Dynamic (headless-browser) slot. The two overloads:
/// <list type="bullet">
/// <item><description><see cref="WithCdpPageLoader(ScraperEngineBuilder, string)"/>
/// — connect to an existing CDP endpoint (BYO browser; the path used by
/// every <c>WebReaper.Stealth.X</c> satellite after it spawns its fork's
/// binary; the path the CLI's <c>--browser-cdp-url</c> flag takes).</description></item>
/// <item><description><see cref="WithCdpPageLoader(ScraperEngineBuilder, CdpLaunchOptions)"/>
/// — launch-and-connect: the transport spawns Chromium with
/// <c>--remote-debugging-port=0</c> via <see cref="CdpLaunchHelpers"/> and
/// owns the process lifecycle.</description></item>
/// </list>
/// The seam is the existing ADR-0050 4-arg
/// <c>WithLoadTransport((cookies, proxy, logger, actionResolver) =&gt; …)</c>
/// factory — same shape as <c>WebReaper.Playwright</c>'s extension.
/// </summary>
public static class CdpPageLoaderBuilderExtensions
{
    /// <summary>Connect-to-existing: register a CDP transport pointing at
    /// <paramref name="cdpUrl"/>. The user owns the browser process; this
    /// transport just opens the WebSocket.</summary>
    public static ScraperEngineBuilder WithCdpPageLoader(this ScraperEngineBuilder builder, string cdpUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cdpUrl);
        return builder.WithLoadTransport((cookies, proxy, logger, actionResolver) =>
            new CdpPageLoadTransport(cdpUrl, cookies, proxy, logger, actionResolver));
    }

    /// <summary>Launch-and-connect: the transport spawns Chromium via
    /// <see cref="CdpLaunchHelpers"/> with the supplied
    /// <paramref name="options"/>, then connects. The transport is registered
    /// for engine teardown (ADR-0058), so disposing the engine kills the
    /// spawned process.</summary>
    public static ScraperEngineBuilder WithCdpPageLoader(this ScraperEngineBuilder builder, CdpLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The launch runs lazily on the first LoadAsync — captured by the
        // transport's url-provider delegate; the disposal handle is wired
        // through so the transport teardown kills the process.
        LaunchedCdpEndpoint? endpoint = null;
        var endpointLock = new SemaphoreSlim(1, 1);

        async Task<string> ProvideAsync(CancellationToken ct)
        {
            if (endpoint is not null) return endpoint.CdpUrl;
            await endpointLock.WaitAsync(ct);
            try
            {
                if (endpoint is not null) return endpoint.CdpUrl;
                var executable = options.ExecutablePath
                    ?? CdpLaunchHelpers.FindOnPath("google-chrome", "chromium", "chrome", "microsoft-edge", "msedge")
                    ?? throw new InvalidOperationException(
                        "No Chrome/Chromium/Edge binary found on PATH or in conventional install locations. " +
                        "Either install one, set CdpLaunchOptions.ExecutablePath, or use the BYO " +
                        "WithCdpPageLoader(string) overload.");
                var args = options.AdditionalArgs.ToList();
                if (options.Headless && !args.Any(a => a.StartsWith("--headless", StringComparison.Ordinal)))
                    args.Add("--headless=new");
                endpoint = await CdpLaunchHelpers.LaunchAsync(
                    new CdpLaunchSpec(executable, args, options.UserDataDir, options.StartupTimeout), ct);
                return endpoint.CdpUrl;
            }
            finally
            {
                endpointLock.Release();
            }
        }

        async ValueTask DisposeAsync()
        {
            if (endpoint is not null) await endpoint.DisposeAsync();
            endpointLock.Dispose();
        }

        return builder.WithLoadTransport((cookies, proxy, logger, actionResolver) =>
        {
            var transport = new CdpPageLoadTransport(ProvideAsync, DisposeAsync, cookies, proxy, logger, actionResolver);
            // ADR-0058: register the transport for engine teardown. The engine
            // does not dispose page-load transports itself (EscalatingPageLoader
            // is not IAsyncDisposable), so without this the lazily-spawned browser
            // process leaks on a long-running host (OS-reaped only on host exit,
            // the same gap WithCloakBrowser already closed for its subprocess).
            // This factory runs during BuildAsync, before the engine reads the
            // teardown-hook list, so the hook lands; disposal is idempotent and a
            // no-op when the browser was never launched (an HTTP-only scrape that
            // never reached the Dynamic rung).
            builder.OnTeardown(transport);
            return transport;
        });
    }
}
