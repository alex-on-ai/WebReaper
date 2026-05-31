using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WebReaper.Core.Actions.Abstract;
using WebReaper.Core.Actions.Concrete;
using WebReaper.Core.CookieStorage.Abstract;
using WebReaper.Core.Loaders.Abstract;
using WebReaper.Domain.PageActions;
using WebReaper.Proxy.Abstract;

namespace WebReaper.Cdp;

/// <summary>
/// Raw CDP <see cref="IPageLoadTransport"/> (ADR-0052). Connects to a CDP
/// WebSocket (either BYO via <c>WithCdpPageLoader(cdpUrl)</c> or a
/// launch-and-connect spawn via <c>WithCdpPageLoader(CdpLaunchOptions)</c>),
/// creates a fresh target per request, navigates, runs the seven
/// <see cref="PageAction"/> arms via <c>Runtime.evaluate</c> + CDP primitives,
/// and returns the rendered HTML. AOT-clean — no PuppeteerSharp,
/// no Microsoft.Playwright.
/// </summary>
public sealed class CdpPageLoadTransport : IPageLoadTransport, IAsyncDisposable
{
    private readonly ICookiesStorage _cookiesStorage;
    private readonly IProxyProvider? _proxyProvider;
    private readonly ILogger _logger;
    private readonly SemanticActCoordinator _semanticActCoordinator;
    private readonly Func<CancellationToken, Task<string>> _cdpUrlProvider;
    private readonly Func<ValueTask>? _disposeUrlProvider;

    // The "browser" WebSocket is opened lazily on the first LoadAsync and
    // reused across requests; per-request page targets are created within it.
    private CdpClient? _browserClient;
    private string? _resolvedCdpUrl;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private bool _disposed;

    /// <summary>Construct against an already-resolved CDP URL (BYO scenario).</summary>
    public CdpPageLoadTransport(
        string cdpUrl,
        ICookiesStorage cookiesStorage,
        IProxyProvider? proxyProvider,
        ILogger logger,
        IActionResolver actionResolver)
        : this(_ => Task.FromResult(cdpUrl), null, cookiesStorage, proxyProvider, logger, actionResolver)
    {
        if (proxyProvider is not null)
            logger.LogWarning(
                "CdpPageLoadTransport: an IProxyProvider was registered with the connect-to-existing overload. " +
                "Proxy is ignored — the BYO browser owns its own proxy configuration.");
    }

    /// <summary>Construct with a CDP-URL provider (lazy launch-and-connect).
    /// The provider runs on first <see cref="LoadAsync"/>; its
    /// <paramref name="disposeUrlProvider"/> runs at transport disposal —
    /// the right place to tear down a launch-and-connect spawn.</summary>
    public CdpPageLoadTransport(
        Func<CancellationToken, Task<string>> cdpUrlProvider,
        Func<ValueTask>? disposeUrlProvider,
        ICookiesStorage cookiesStorage,
        IProxyProvider? proxyProvider,
        ILogger logger,
        IActionResolver actionResolver)
    {
        _cdpUrlProvider = cdpUrlProvider;
        _disposeUrlProvider = disposeUrlProvider;
        _cookiesStorage = cookiesStorage;
        _proxyProvider = proxyProvider;
        _logger = logger;
        _semanticActCoordinator = new SemanticActCoordinator(actionResolver, logger);
    }

    /// <inheritdoc />
    public async Task<PageLoadResult> LoadAsync(PageRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var browser = await EnsureConnectedAsync(cancellationToken);

        // Create a fresh page target for this request.
        var createTargetResult = await browser.SendAsync("Target.createTarget",
            new JsonObject { ["url"] = "about:blank" }, sessionId: null, cancellationToken);
        var targetId = createTargetResult["targetId"]?.GetValue<string>()
            ?? throw new CdpException("Target.createTarget returned no targetId.");

        // Hoisted out of try{} so the finally{} can deregister the
        // ADR-0057 per-session network tracker after the page closes.
        string? sessionId = null;
        try
        {
            // Attach with flatten: true so subsequent commands carry sessionId
            // on the same browser-level WebSocket.
            var attachResult = await browser.SendAsync("Target.attachToTarget",
                new JsonObject { ["targetId"] = targetId, ["flatten"] = true },
                sessionId: null, cancellationToken);
            sessionId = attachResult["sessionId"]?.GetValue<string>()
                ?? throw new CdpException("Target.attachToTarget returned no sessionId.");

            await browser.SendAsync("Page.enable", null, sessionId, cancellationToken);
            await browser.SendAsync("Runtime.enable", null, sessionId, cancellationToken);
            await browser.SendAsync("Network.enable", null, sessionId, cancellationToken);

            // Authenticated proxy: the browser is launched with --proxy-server but
            // Chromium has no proxy-auth flag, so enable the Fetch domain and let the
            // read loop answer the proxy's 407 with the IProxyProvider's credentials.
            // handleAuthRequests with no patterns intercepts requests (waved through
            // with Fetch.continueRequest) and surfaces auth challenges. Inert when no
            // proxy is configured.
            if (_proxyProvider is not null)
            {
                var webProxy = await _proxyProvider.GetProxyAsync();
                var cred = webProxy.Credentials as NetworkCredential
                    ?? (webProxy.Address is not null ? webProxy.Credentials?.GetCredential(webProxy.Address, "Basic") : null);
                if (cred is not null && !string.IsNullOrEmpty(cred.UserName))
                    browser.SetProxyAuth(cred.UserName, cred.Password);
                await browser.SendAsync("Fetch.enable",
                    new JsonObject { ["handleAuthRequests"] = true },
                    sessionId, cancellationToken);
            }

            // Apply stored cookies (best-effort; CDP rejects malformed entries
            // — we swallow per-cookie failures via a try/catch on the batch).
            await ApplyCookiesAsync(browser, sessionId, request.Url, cancellationToken);

            // Navigate. Wait for the load event with a generous timeout.
            await browser.SendAsync("Page.navigate",
                new JsonObject { ["url"] = request.Url }, sessionId, cancellationToken);
            try
            {
                await browser.WaitForEventAsync("Page.loadEventFired",
                    sessionId, TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("CdpPageLoadTransport: Page.loadEventFired timed out for {Url}; continuing with current DOM.", request.Url);
            }

            // Small settle delay — analogous to Puppeteer's Networkidle2 wait.
            await Task.Delay(200, cancellationToken);

            if (request.PageActions is { Count: > 0 })
            {
                for (var i = 0; i < request.PageActions.Count; i++)
                {
                    var action = request.PageActions[i];
                    _logger.LogInformation(
                        "Performing page action {Current} of {Count}: {Action}",
                        i, request.PageActions.Count - 1, action.GetType().Name);
                    await PerformAsync(browser, sessionId, action, cancellationToken);
                }
            }

            // Capture the rendered DOM. JS bot-checks (Cloudflare et al.) serve an
            // interstitial that loads, runs a challenge, then redirects to the real
            // content, so a single capture at the load event grabs the "verifying
            // you are human" page. When the DOM looks like an interstitial, poll
            // until it clears (a stealth browser that passes the check yields the
            // real DOM) or a cap elapses (a browser that cannot pass stays on the
            // interstitial, which the block detector then flags). Real pages capture
            // immediately: the marker check fails on the first read.
            var html = await CdpPageActionDispatcher.EvaluateAsync(browser, sessionId,
                "document.documentElement.outerHTML", cancellationToken);
            var interstitialDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (LooksLikeInterstitial(html) && DateTime.UtcNow < interstitialDeadline)
            {
                await Task.Delay(1500, cancellationToken);
                html = await CdpPageActionDispatcher.EvaluateAsync(browser, sessionId,
                    "document.documentElement.outerHTML", cancellationToken);
            }
            // ADR-0083 slice 1: the CDP transport returns the rendered DOM with a
            // null HttpStatus and no headers. Capturing the main-document status
            // via Network.responseReceived is the ADR-named follow-up; browser-
            // tier block detection works off body markers in this DOM meanwhile.
            return new PageLoadResult { Html = html ?? "" };
        }
        finally
        {
            // ADR-0057: drop the per-session network-activity tracker before
            // closing the target — keeps the tracker map bounded across a
            // long-lived browser-WebSocket / many short-lived sessions.
            if (sessionId is not null) browser.RemoveNetworkTracker(sessionId);
            try
            {
                await browser.SendAsync("Target.closeTarget",
                    new JsonObject { ["targetId"] = targetId }, sessionId: null, cancellationToken);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    // The JS-challenge interstitial pages a bot-check serves before redirecting to
    // the real content. Deliberately narrow: these strings appear on the "verifying
    // you are human" page, not on real content, so a stealth browser that clears
    // the challenge no longer matches and its real DOM is captured. Kept local so
    // the AOT-clean transport does not depend on core's IBlockDetector.
    private static readonly string[] InterstitialMarkers =
    [
        "Just a moment",
        "Performing security verification",
        "challenge-platform",
        "cf-browser-verification",
        "_cf_chl_opt",
    ];

    private static bool LooksLikeInterstitial(string? html) =>
        !string.IsNullOrEmpty(html)
        && Array.Exists(InterstitialMarkers, m => html.Contains(m, StringComparison.OrdinalIgnoreCase));

    // ADR-0035 closed-sum dispatch — delegated to CdpPageActionDispatcher
    // (extracted in ADR-0057 follow-up so the dispatch table is testable
    // against a FakeCdpSession in WebReaper.Cdp.Tests). The transport stays
    // responsible for life-cycle (the surrounding LoadAsync) but not for
    // per-arm CDP-primitive plumbing.
    private Task PerformAsync(CdpClient browser, string sessionId, PageAction action, CancellationToken ct) =>
        CdpPageActionDispatcher.PerformAsync(browser, sessionId, action, _semanticActCoordinator, ct);

    private async Task ApplyCookiesAsync(CdpClient browser, string sessionId, string url, CancellationToken ct)
    {
        var container = await _cookiesStorage.GetAsync();
        if (container is null) return;

        var cookies = container.GetAllCookies();
        if (cookies.Count == 0) return;

        var arr = new JsonArray();
        foreach (Cookie c in cookies)
        {
            var entry = new JsonObject
            {
                ["name"] = c.Name,
                ["value"] = c.Value,
                ["path"] = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                ["secure"] = c.Secure,
                ["httpOnly"] = c.HttpOnly,
            };
            if (!string.IsNullOrWhiteSpace(c.Domain)) entry["domain"] = c.Domain;
            else entry["url"] = url;
            if (c.Expires != DateTime.MinValue)
                entry["expires"] = new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds();
            // Use the explicit JsonNode overload of Add — the generic
            // Add<T>(T) variant carries IL2026/IL3050 (AOT-hostile).
            arr.Add((JsonNode)entry);
        }

        try
        {
            await browser.SendAsync("Network.setCookies",
                new JsonObject { ["cookies"] = arr }, sessionId, ct);
        }
        catch (CdpException ex)
        {
            _logger.LogWarning(ex, "CdpPageLoadTransport: Network.setCookies failed; continuing without preserved cookies.");
        }
    }

    private async Task<CdpClient> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_browserClient is not null) return _browserClient;
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_browserClient is not null) return _browserClient;
            var url = _resolvedCdpUrl ??= await _cdpUrlProvider(ct);
            var client = new CdpClient(url);
            await client.ConnectAsync(ct);
            _browserClient = client;
            return client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_browserClient is not null) await _browserClient.DisposeAsync();
        if (_disposeUrlProvider is not null) await _disposeUrlProvider();
        _connectLock.Dispose();
    }
}
