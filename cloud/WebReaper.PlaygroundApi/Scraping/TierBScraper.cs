using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WebReaper.Builders;
using WebReaper.Cdp;

namespace WebReaper.PlaygroundApi.Scraping;

/// <summary>
/// Tier B: the gated, live browser climb. Where Tier A is a direct HTTP-to-Markdown
/// fetch with no climb (<see cref="TierAScraper"/>), Tier B builds a real WebReaper
/// engine so the ADR-0083 <c>EscalatingPageLoader</c> does the climbing: the start
/// page enters at the HTTP rung and auto-climbs to the vanilla browser rung on a
/// block. The live climb streams over the ADR-0085 <c>IClimbObserver</c> seam (not
/// log-scraping); the extracted Markdown arrives on an <c>IScraperSink</c>. Both
/// feed one bounded channel the SSE endpoint drains, so the same climb-viz the
/// canned hero uses renders live and user-driven. Emits the shared <c>ClimbEvent</c>
/// shape.
/// <para>
/// This is build-order step 2 (Phase 2 doc): HTTP + vanilla browser only, no
/// stealth rung yet (legally clean, bakeable). The stealth rung
/// (<c>WithCloakBrowser</c>) is appended in step 3, behind the secret gate until
/// the CloakHQ OEM license lands.
/// </para>
/// </summary>
public sealed class TierBScraper
{
    // A climb emits a handful of steps and the SSE reader drains immediately, so
    // the channel sits near-empty; the bound just caps it so a stalled reader
    // cannot grow it without limit.
    private const int ChannelCapacity = 64;

    // Per-job wall-clock ceiling (Phase 2 doc decision 3: the ~45s kill). Caps a
    // hostile or pathological page so one job cannot hold a pooled VM open.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(45);

    private readonly string? _chromiumPath;

    /// <param name="chromiumPath">Absolute path to the baked Chromium binary (the
    /// Docker image's Playwright Chromium). <c>null</c> lets the CDP launcher
    /// search PATH and conventional install locations (the local-dev path).</param>
    public TierBScraper(string? chromiumPath = null) => _chromiumPath = chromiumPath;

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
            // Observe the background task and let it tear the engine + browser
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
        // browser through the await using below.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RunTimeout);

        // We own the vanilla-browser rung's process. The lazy
        // WithCdpPageLoader(CdpLaunchOptions) overload would defer teardown to
        // transport disposal, but the engine never disposes page-load transports
        // (EscalatingPageLoader is not IAsyncDisposable and nothing registers the
        // transport via OnTeardown), so on a long-running host its spawned Chrome
        // leaks per request. Instead we launch lazily (only when the climb
        // actually reaches the browser rung, keeping an HTTP-success scrape
        // browser-free) and kill the process tree ourselves here, the model-B
        // per-job recycle (decision 3).
        await using var browser = new LazyBrowserRung(_chromiumPath);

        try
        {
            var observer = new ChannelClimbObserver(writer);
            var sink = new MarkdownResultSink(writer);

            // Composition is all public builder surface. Crawl (not CrawlWithBrowser)
            // so the start page enters at the HTTP rung and the viz shows HTTP
            // challenged before the climb. No link selectors => a single page (the
            // scrape-one-URL non-goal holds). WithLoadTransport is the public seam
            // WithCdpPageLoader wraps; we use it directly so we can hold the
            // browser handle and own its teardown.
            await using var engine = await ScraperEngineBuilder
                .Crawl(uri.ToString())
                .AsMarkdown()
                .WithLoadTransport((cookies, proxy, logger, actionResolver) =>
                    new CdpPageLoadTransport(browser.ProvideCdpUrlAsync, disposeUrlProvider: null,
                        cookies, proxy, logger, actionResolver))
                .WithClimbObserver(observer)
                .AddSink(sink)
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

    private static string DescribeFailure(Exception ex) => ex switch
    {
        // No Chromium baked or on PATH, or a bad PLAYGROUND_CHROMIUM_PATH.
        InvalidOperationException or FileNotFoundException =>
            "The browser climb could not start (no Chromium available).",
        // The browser launched but never published its CDP endpoint in time.
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
    /// A single browser rung whose Chromium process is launched lazily (on the
    /// first browser-rung load) and owned here, so it is killed deterministically
    /// on disposal regardless of how the run ends. The CDP-URL provider is handed
    /// to the <see cref="CdpPageLoadTransport"/>; the first call spawns Chromium
    /// with <c>--remote-debugging-port=0</c> and returns its WebSocket URL,
    /// subsequent calls reuse it.
    /// </summary>
    private sealed class LazyBrowserRung : IAsyncDisposable
    {
        private static readonly string[] ChromiumNames =
            ["google-chrome", "chromium", "chrome", "microsoft-edge", "msedge"];

        private readonly string? _chromiumPath;
        private readonly SemaphoreSlim _launchLock = new(1, 1);
        private LaunchedCdpEndpoint? _endpoint;

        public LazyBrowserRung(string? chromiumPath) => _chromiumPath = chromiumPath;

        public async Task<string> ProvideCdpUrlAsync(CancellationToken cancellationToken)
        {
            if (_endpoint is not null) return _endpoint.CdpUrl;
            await _launchLock.WaitAsync(cancellationToken);
            try
            {
                if (_endpoint is not null) return _endpoint.CdpUrl;
                var executable = _chromiumPath
                    ?? CdpLaunchHelpers.FindOnPath(ChromiumNames)
                    ?? throw new InvalidOperationException(
                        "No Chromium binary found. Set PLAYGROUND_CHROMIUM_PATH or install Chrome/Chromium.");
                _endpoint = await CdpLaunchHelpers.LaunchAsync(
                    new CdpLaunchSpec(
                        executable,
                        ["--headless=new", "--no-sandbox", "--disable-dev-shm-usage"],
                        StartupTimeout: TimeSpan.FromSeconds(20)),
                    cancellationToken);
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
