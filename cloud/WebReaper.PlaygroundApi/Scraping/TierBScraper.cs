using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WebReaper.Builders;
using WebReaper.Cdp;
using WebReaper.Stealth.CloakBrowser;

namespace WebReaper.PlaygroundApi.Scraping;

/// <summary>
/// Tier B: the gated, live browser climb. Where Tier A is a direct HTTP-to-Markdown
/// fetch with no climb (<see cref="TierAScraper"/>), Tier B builds a real WebReaper
/// engine so the ADR-0083 <c>EscalatingPageLoader</c> does the climbing: the start
/// page enters at the HTTP rung and auto-climbs to the vanilla browser rung, then
/// (when a CloakBrowser binary is configured) the stealth rung, on each block. The
/// live climb streams over the ADR-0085 <c>IClimbObserver</c> seam (not
/// log-scraping); the extracted Markdown arrives on an <c>IScraperSink</c>. Both
/// feed one bounded channel the SSE endpoint drains, so the same climb-viz the
/// canned hero uses renders live and user-driven. Emits the shared <c>ClimbEvent</c>
/// shape.
/// <para>
/// The stealth rung is included only when a CloakBrowser binary path is supplied
/// (the baked binary, named in <c>PLAYGROUND_CLOAKBROWSER_PATH</c>); unset means
/// HTTP + vanilla browser only. Per the Phase 2 doc it stays behind the
/// <c>X-Playground-Secret</c> gate (never wired to the public edge route) until the
/// CloakHQ OEM license lands.
/// </para>
/// </summary>
public sealed class TierBScraper
{
    private static readonly string[] ChromiumNames =
        ["google-chrome", "chromium", "chrome", "microsoft-edge", "msedge"];

    // A climb emits a handful of steps and the SSE reader drains immediately, so
    // the channel sits near-empty; the bound just caps it so a stalled reader
    // cannot grow it without limit.
    private const int ChannelCapacity = 64;

    // Per-job wall-clock ceiling (decision 3): caps a hostile or pathological page
    // so one job cannot hold a pooled VM open. Configurable because a full HTTP to
    // vanilla to stealth climb against a slow bot-check (each browser rung can wait
    // up to 30s for the challenge page) needs more than the 45s default.
    private readonly TimeSpan _runTimeout;

    private readonly string? _chromiumPath;
    private readonly string? _cloakBrowserPath;

    /// <param name="chromiumPath">Absolute path to the baked Chromium binary (the
    /// Docker image's Playwright Chromium). <c>null</c> lets the CDP launcher
    /// search PATH and conventional install locations (the local-dev path).</param>
    /// <param name="cloakBrowserPath">Absolute path to the baked CloakBrowser
    /// binary. When set, a stealth rung is appended above the vanilla browser rung;
    /// <c>null</c> means HTTP + vanilla browser only.</param>
    /// <param name="jobSeconds">Per-job wall-clock budget in seconds (default 45).
    /// A full stealth climb against a slow Cloudflare challenge needs more.</param>
    public TierBScraper(string? chromiumPath = null, string? cloakBrowserPath = null, int jobSeconds = 45)
    {
        _chromiumPath = chromiumPath;
        _cloakBrowserPath = cloakBrowserPath;
        _runTimeout = TimeSpan.FromSeconds(jobSeconds > 0 ? jobSeconds : 45);
    }

    public async IAsyncEnumerable<object> StreamAsync(
        string url,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeUrl(url, out var uri))
        {
            yield return ClimbEvents.Error("Enter a valid http(s) URL.");
            yield break;
        }

        yield return ClimbEvents.Request(uri.ToString());

        var channel = Channel.CreateBounded<object>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            // The observer (load stage) and the sink (post-extraction) both write.
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Run the climb on a background task that owns the engine + browser
        // lifecycle and always completes the channel (writing an error event on
        // failure first), so the drain loop below needs no try/catch around its
        // yields.
        var climb = RunClimbAsync(uri, channel.Writer, cancellationToken);

        try
        {
            await foreach (var climbEvent in channel.Reader.ReadAllAsync(cancellationToken))
                yield return climbEvent;
        }
        finally
        {
            // Observe the background task and let it tear the engine + browsers
            // down (it has finished by the time the channel closes; on an early
            // disconnect the linked token cancels it).
            await climb;
        }
    }

    private async Task RunClimbAsync(Uri uri, ChannelWriter<object> writer, CancellationToken cancellationToken)
    {
        // Link the request-abort token with the per-job budget: a client
        // disconnect cancels cancellationToken; the 45s budget cancels via
        // CancelAfter. Either tears the engine down through RunAsync, and the
        // browsers through the await using below.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_runTimeout);

        // We own each browser rung's process. The lazy WithCdpPageLoader overloads
        // would defer teardown to transport disposal, but the engine never disposes
        // page-load transports (EscalatingPageLoader is not IAsyncDisposable and
        // nothing registers the transport via OnTeardown), so on a long-running
        // host a spawned browser leaks per request. Instead each rung launches
        // lazily (only when the climb actually reaches it, so an HTTP-success scrape
        // stays browser-free and most scrapes never spawn the stealth browser) and
        // we kill the process trees ourselves here, the model-B per-job recycle
        // (decision 3).
        await using var vanilla = new LazyCdpRung(LaunchVanillaAsync);
        await using var stealth = _cloakBrowserPath is not null ? new LazyCdpRung(LaunchStealthAsync) : null;

        try
        {
            var observer = new ChannelClimbObserver(writer);
            var sink = new MarkdownResultSink(writer);

            // Composition is all public builder surface. Crawl (not CrawlWithBrowser)
            // so the start page enters at the HTTP rung and the viz shows HTTP
            // challenged before the climb. No link selectors => a single page (the
            // scrape-one-URL non-goal holds). WithLoadTransport is the public seam
            // WithCdpPageLoader wraps; we use it directly so we can hold each browser
            // handle and own its teardown. Rung order is HTTP (0), vanilla (1),
            // stealth (2), the index the ChannelClimbObserver maps to a tier name.
            var builder = ScraperEngineBuilder
                .Crawl(uri.ToString())
                .AsMarkdown()
                .WithLoadTransport((cookies, proxy, logger, actionResolver) =>
                    new CdpPageLoadTransport(vanilla.ProvideCdpUrlAsync, disposeUrlProvider: null,
                        cookies, proxy, logger, actionResolver));

            if (stealth is not null)
                builder = builder.WithLoadTransport((cookies, proxy, logger, actionResolver) =>
                    new CdpPageLoadTransport(stealth.ProvideCdpUrlAsync, disposeUrlProvider: null,
                        cookies, proxy, logger, actionResolver));

            await using var engine = await builder
                .WithClimbObserver(observer)
                .AddSink(sink)
                // The climb already escalates HTTP -> vanilla -> stealth internally
                // on each block; the core default's four-attempt whole-crawl retry
                // would replay the entire multi-minute browser climb from the lifted
                // host floor on a transient transport throw (the duplicate
                // attempt(browser)). One attempt is the right bound for a single-URL
                // scrape.
                .WithRetryPolicy(new SingleAttemptRetryPolicy())
                .StopWhenAllLinksProcessed()
                .BuildAsync();

            await engine.RunAsync(timeout.Token);
            writer.TryComplete();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The 45s budget fired and the client is still connected; report the
            // stop as a real outcome rather than swallowing it.
            writer.TryWrite(ClimbEvents.Error("The climb exceeded the time budget and was stopped."));
            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            // The client disconnected (cancellationToken). No one is listening;
            // just close the channel so the drain loop ends.
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryWrite(ClimbEvents.Error(DescribeFailure(ex)));
            writer.TryComplete();
        }
    }

    // The vanilla browser rung (Apache/BSD Chromium). Launches lazily on the first
    // browser-rung load.
    private Task<LaunchedCdpEndpoint> LaunchVanillaAsync(CancellationToken cancellationToken)
    {
        var executable = _chromiumPath
            ?? CdpLaunchHelpers.FindOnPath(ChromiumNames)
            ?? throw new InvalidOperationException(
                "No Chromium binary found. Set PLAYGROUND_CHROMIUM_PATH or install Chrome/Chromium.");
        return CdpLaunchHelpers.LaunchAsync(
            new CdpLaunchSpec(
                executable,
                ["--headless=new", "--no-sandbox", "--disable-dev-shm-usage"],
                // The first browser launch on a scale-to-zero Fly Machine is a cold
                // start: the Chromium binary + shared libs page in from disk, which
                // exceeded the old 20s cap and threw a launch TimeoutException. 60s
                // gives cold-start headroom; a warm relaunch still returns in seconds.
                StartupTimeout: TimeSpan.FromSeconds(60)),
            cancellationToken);
    }

    // The stealth rung (CloakBrowser, gated on the configured binary). The vendor's
    // RecommendedArgs are hardened-Chromium sanity flags; the stealth is in the
    // binary. --no-sandbox is added because the container runs as root (the VM is
    // the trust boundary, decision 5).
    private Task<LaunchedCdpEndpoint> LaunchStealthAsync(CancellationToken cancellationToken)
    {
        var args = new List<string>(CloakBrowserLauncher.RecommendedArgs) { "--no-sandbox", "--headless=new" };
        return CdpLaunchHelpers.LaunchAsync(
            // 60s cold-start headroom, matching the vanilla rung (see LaunchVanillaAsync).
            new CdpLaunchSpec(_cloakBrowserPath!, args, StartupTimeout: TimeSpan.FromSeconds(60)),
            cancellationToken);
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        // No browser binary at the configured path / on PATH.
        InvalidOperationException or FileNotFoundException =>
            "The browser climb could not start (no browser binary available).",
        // A browser launched but never published its CDP endpoint in time.
        TimeoutException => "The browser did not start in time.",
        _ => "The climb failed before it could finish.",
    };

    private static bool TryNormalizeUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// One browser rung whose CDP process is launched lazily (on the first load
    /// that reaches it) by the supplied launcher and owned here, so it is killed
    /// deterministically on disposal regardless of how the run ends. The CDP-URL
    /// provider is handed to the <see cref="CdpPageLoadTransport"/>; the first call
    /// launches the browser and returns its WebSocket URL, subsequent calls reuse
    /// it.
    /// </summary>
    private sealed class LazyCdpRung : IAsyncDisposable
    {
        private readonly Func<CancellationToken, Task<LaunchedCdpEndpoint>> _launch;
        private readonly SemaphoreSlim _launchLock = new(1, 1);
        private LaunchedCdpEndpoint? _endpoint;

        public LazyCdpRung(Func<CancellationToken, Task<LaunchedCdpEndpoint>> launch) => _launch = launch;

        public async Task<string> ProvideCdpUrlAsync(CancellationToken cancellationToken)
        {
            if (_endpoint is not null) return _endpoint.CdpUrl;
            await _launchLock.WaitAsync(cancellationToken);
            try
            {
                if (_endpoint is not null) return _endpoint.CdpUrl;
                _endpoint = await _launch(cancellationToken);
                return _endpoint.CdpUrl;
            }
            finally
            {
                _launchLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Kills the spawned process tree and removes its temp user-data-dir.
            if (_endpoint is not null) await _endpoint.DisposeAsync();
            _launchLock.Dispose();
        }
    }
}
