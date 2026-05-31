using Microsoft.Extensions.Logging;
using WebReaper.Core.Blocking.Abstract;
using WebReaper.Core.Loaders.Abstract;
using WebReaper.Domain.Selectors;

namespace WebReaper.Core.Loaders.Concrete;

/// <summary>
/// The one <see cref="IPageLoader"/> (ADR-0004), now block-aware and climbing
/// (ADR-0083 part 4). It composes an ordered ladder of <see cref="PageLoadTier"/>
/// rungs (HTTP then headless browser then, with the CLI flag wiring, stealth),
/// the <see cref="IBlockDetector"/>, a per-run <see cref="HostTierFloor"/>, and
/// the <see cref="IPageCache"/>. For one page it starts at the host floor, loads
/// at the current rung, runs the detector, and if the result looks blocked and a
/// higher real rung exists it climbs and reloads; it returns the best
/// <see cref="PageLoadResult"/> it reached.
/// <para>
/// Because the whole climb lives inside one <see cref="LoadAsync"/> call, the
/// Crawl driver, scheduler, and visited-link tracker are untouched (ADR-0022's
/// transport-blind driver holds) — so <c>scrape</c> and <c>crawl</c> both get
/// climbing for free. The loader does not return the verdict: the Spider
/// re-runs the same pure <see cref="IBlockDetector"/> on the returned result for
/// the Job report, which yields the final rung's verdict by construction.
/// </para>
/// <para>
/// Quarantine-clean (ADR-0009): the browser / stealth rungs are injected
/// <see cref="IPageLoadTransport"/>s supplied by the Cdp / Playwright satellites;
/// core never references them. The only rung type core knows by name is the
/// <see cref="BrowserNotConfiguredPageLoadTransport"/> sentinel, which is treated
/// as "no real rung" so auto-escalation never launches a browser the consumer
/// did not configure.
/// </para>
/// </summary>
internal sealed class EscalatingPageLoader : IPageLoader
{
    private readonly IReadOnlyList<PageLoadTier> _tiers;
    private readonly IBlockDetector _blockDetector;
    private readonly HostTierFloor _hostTierFloor;
    private readonly IPageCache _cache;
    private readonly ILogger _logger;
    private readonly IClimbObserver _observer;

    public EscalatingPageLoader(
        IReadOnlyList<PageLoadTier> tiers,
        IBlockDetector blockDetector,
        HostTierFloor hostTierFloor,
        ILogger logger,
        IPageCache? cache = null,
        IClimbObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Count == 0)
            throw new ArgumentException("The escalating loader needs at least one tier.", nameof(tiers));
        ArgumentNullException.ThrowIfNull(blockDetector);
        ArgumentNullException.ThrowIfNull(hostTierFloor);
        ArgumentNullException.ThrowIfNull(logger);

        _tiers = tiers;
        _blockDetector = blockDetector;
        _hostTierFloor = hostTierFloor;
        _logger = logger;
        // ADR-0041: NullPageCache preserves the no-cache behaviour exactly.
        _cache = cache ?? new NullPageCache();
        // ADR-0085: NullClimbObserver is the zero-overhead default.
        _observer = observer ?? NullClimbObserver.Instance;
    }

    public async Task<PageLoadResult> LoadAsync(PageRequest request, CancellationToken cancellationToken = default)
    {
        var host = HostOf(request.Url);

        // ADR-0041 + ADR-0083: the cache-aside read happens once, before the
        // climb. A hit is always a clean (non-blocked) result — a blocked result
        // is never written — so it is safe to serve without re-detecting.
        var cached = await _cache.TryReadAsync(request.Url, request.PageType, cancellationToken);
        if (cached is not null)
        {
            _logger.LogInformation("Page cache hit for {Url}", request.Url);
            return cached;
        }

        var start = StartTier(request.PageType, _hostTierFloor.FloorFor(host));

        PageLoadResult result = null!;
        for (var tier = start; tier < _tiers.Count; tier++)
        {
            _logger.LogInformation(
                "Loading {PageType} page {Url} at tier {Tier}",
                _tiers[tier].PageType, request.Url, tier);
            _observer.OnStep(new ClimbStep(
                ClimbPhase.Attempt, request.Url, tier, _tiers[tier].PageType, null, null));

            // ADR-0083: a climb bypasses the cache for the higher rung (no read
            // here), so a stale cached lower-tier result can never silently
            // defeat a climb. A transport fault (no response) throws a
            // PageLoadException and propagates to the retry policy — only a
            // completed, detector-flagged response climbs.
            result = await _tiers[tier].Transport.LoadAsync(request, cancellationToken);

            var verdict = _blockDetector.Detect(result);
            if (!verdict.IsBlocked)
            {
                // Only clean results are cached (so the read above is always
                // safe to serve). A cache-write failure must not fail the load.
                await TryWriteCacheAsync(request, result, cancellationToken);
                _observer.OnStep(new ClimbStep(
                    ClimbPhase.Succeeded, request.Url, tier, _tiers[tier].PageType, result.HttpStatus, null));
                return result;
            }

            // Blocked. A real higher rung is one that is configured — the
            // BrowserNotConfigured sentinel does not count, so auto-escalation
            // never launches a browser the consumer did not wire (an explicit
            // Dynamic-start load still hits the sentinel's actionable throw).
            var hasHigherTier =
                tier + 1 < _tiers.Count && IsRealTier(_tiers[tier + 1]);

            // ADR-0083 part 5: only a high-confidence block lifts the host floor
            // (a weak body-marker is too unreliable to promote a whole host),
            // and only to a real rung — never strand the host on the sentinel.
            if (hasHigherTier)
                _hostTierFloor.Lift(host, tier + 1, verdict.Confidence);

            if (!hasHigherTier)
            {
                // Ceiling: the top reachable rung is still blocked. Return the
                // residual-blocked result UNCACHED; the driver's block drop
                // policy (slice 3) suppresses and tallies it.
                _logger.LogInformation(
                    "Page {Url} still blocked at the top tier: {Reason}",
                    request.Url, verdict.Reason);
                _observer.OnStep(new ClimbStep(
                    ClimbPhase.Exhausted, request.Url, tier, _tiers[tier].PageType, result.HttpStatus, verdict.Reason));
                return result;
            }

            _logger.LogInformation(
                "Page {Url} blocked at tier {Tier} ({Reason}); climbing to tier {Next}",
                request.Url, tier, verdict.Reason, tier + 1);
            _observer.OnStep(new ClimbStep(
                ClimbPhase.Blocked, request.Url, tier, _tiers[tier].PageType, result.HttpStatus, verdict.Reason));
            _observer.OnStep(new ClimbStep(
                ClimbPhase.Climbing, request.Url, tier, _tiers[tier].PageType, null, null));
        }

        // Unreachable: the loop returns on the first clean load or at the
        // ceiling, and StartTier keeps the start index in range.
        return result;
    }

    // The starting rung for a request: a Dynamic page cannot be served by an
    // HTTP (Static) rung, so it starts at the first Dynamic-capable rung; a
    // Static page can start anywhere and so starts at the host floor. Never
    // below the floor, never past the top rung.
    private int StartTier(PageType pageType, int floor)
    {
        var min = pageType == PageType.Dynamic ? FirstDynamicTier() : 0;
        var start = Math.Max(floor, min);
        return Math.Min(start, _tiers.Count - 1);
    }

    private int FirstDynamicTier()
    {
        for (var i = 0; i < _tiers.Count; i++)
            if (_tiers[i].PageType == PageType.Dynamic) return i;

        // No Dynamic rung in the ladder (an HTTP-only wiring). Clamp to the top
        // rung; in practice the Dynamic slot is always present (a real browser
        // transport or the actionable sentinel), so this fallback never trips.
        return _tiers.Count - 1;
    }

    // The BrowserNotConfigured sentinel is the absence of a configured browser,
    // not a real rung to auto-escalate into.
    private static bool IsRealTier(PageLoadTier tier) =>
        tier.Transport is not BrowserNotConfiguredPageLoadTransport;

    private async Task TryWriteCacheAsync(
        PageRequest request, PageLoadResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.WriteAsync(request.Url, request.PageType, result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Page cache write failed for {Url}", request.Url);
        }
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
