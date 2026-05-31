using Microsoft.Extensions.Logging.Abstractions;
using WebReaper.Core.Blocking.Abstract;
using WebReaper.Core.Loaders.Abstract;
using WebReaper.Core.Loaders.Concrete;
using WebReaper.Domain.Selectors;
using Xunit;

namespace WebReaper.UnitTests;

// ADR-0083 slice 4: the per-page climb. Fake transports + a stub detector — no
// HTTP, no browser. Each transport returns a result tagged by Html so the stub
// detector can decide "this rung is blocked" and the assertions can tell which
// rung produced the returned result.
public class EscalatingPageLoaderTests
{
    private sealed class CountingTransport(string html, int? status = 200) : IPageLoadTransport
    {
        private readonly PageLoadResult _result = new() { Html = html, HttpStatus = status };
        public int Calls { get; private set; }
        public PageRequest? LastRequest { get; private set; }

        public Task<PageLoadResult> LoadAsync(PageRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingTransport : IPageLoadTransport
    {
        public Task<PageLoadResult> LoadAsync(PageRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This transport must not be called.");
    }

    private sealed class StubDetector(Func<PageLoadResult, BlockVerdict> verdict) : IBlockDetector
    {
        public BlockVerdict Detect(PageLoadResult result) => verdict(result);
    }

    private static readonly IBlockDetector NeverBlocked = new StubDetector(_ => BlockVerdict.None);

    // Blocks any result whose Html matches one of the given tags, at High
    // confidence; everything else is clean.
    private static IBlockDetector BlockHigh(params string[] blockedTags) =>
        new StubDetector(r => blockedTags.Contains(r.Html)
            ? new BlockVerdict(BlockConfidence.High, $"blocked: {r.Html}")
            : BlockVerdict.None);

    private static EscalatingPageLoader Loader(
        IBlockDetector detector, HostTierFloor floor, IPageCache? cache, params PageLoadTier[] tiers)
        => new(tiers, detector, floor, NullLogger.Instance, cache);

    private static PageLoadTier Http(IPageLoadTransport t) => new(PageType.Static, t);
    private static PageLoadTier Browser(IPageLoadTransport t) => new(PageType.Dynamic, t);

    [Fact]
    public async Task A_clean_first_load_returns_without_climbing()
    {
        var http = new CountingTransport("http");
        var browser = new CountingTransport("browser");
        var loader = Loader(NeverBlocked, new HostTierFloor(), cache: null, Http(http), Browser(browser));

        var result = await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        Assert.Equal("http", result.Html);
        Assert.Equal(1, http.Calls);
        Assert.Equal(0, browser.Calls);                       // no over-climbing
        Assert.Equal("https://x.test/", http.LastRequest!.Url); // request forwarded
    }

    [Fact]
    public async Task A_blocked_load_climbs_to_the_next_tier_and_returns_the_clean_result()
    {
        var http = new CountingTransport("http");
        var browser = new CountingTransport("browser");
        var loader = Loader(BlockHigh("http"), new HostTierFloor(), cache: null, Http(http), Browser(browser));

        var result = await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        Assert.Equal("browser", result.Html); // climbed past the blocked HTTP rung
        Assert.Equal(1, http.Calls);
        Assert.Equal(1, browser.Calls);
    }

    [Fact]
    public async Task Still_blocked_at_the_top_tier_returns_the_residual_blocked_result()
    {
        var http = new CountingTransport("http");
        var browser = new CountingTransport("browser");
        var loader = Loader(BlockHigh("http", "browser"), new HostTierFloor(), cache: null,
            Http(http), Browser(browser));

        var result = await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        Assert.Equal("browser", result.Html); // the top rung's result, still blocked
        Assert.Equal(1, http.Calls);
        Assert.Equal(1, browser.Calls);
    }

    [Fact]
    public async Task A_dynamic_request_starts_at_the_browser_tier_skipping_http()
    {
        var http = new CountingTransport("http");
        var browser = new CountingTransport("browser");
        var loader = Loader(NeverBlocked, new HostTierFloor(), cache: null, Http(http), Browser(browser));

        var result = await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Dynamic));

        Assert.Equal("browser", result.Html);
        Assert.Equal(0, http.Calls); // a Dynamic page never touches the HTTP rung
        Assert.Equal(1, browser.Calls);
    }

    [Fact]
    public async Task A_high_block_lifts_the_host_floor_so_the_next_same_host_page_skips_the_blocked_tier()
    {
        var http = new CountingTransport("http");
        var browser = new CountingTransport("browser");
        var floor = new HostTierFloor();
        var loader = Loader(BlockHigh("http"), floor, cache: null, Http(http), Browser(browser));

        await loader.LoadAsync(new PageRequest("https://h.test/a", PageType.Static)); // climbs http->browser
        await loader.LoadAsync(new PageRequest("https://h.test/b", PageType.Static)); // starts at browser

        Assert.Equal(1, http.Calls);    // only the first page paid the blocked HTTP rung
        Assert.Equal(2, browser.Calls); // both pages served by the browser rung
        Assert.Equal(1, floor.FloorFor("h.test"));
    }

    [Fact]
    public async Task A_weak_block_does_not_lift_the_floor_so_each_page_re_climbs()
    {
        var http = new CountingTransport("http");
        var browser = new CountingTransport("browser");
        var floor = new HostTierFloor();
        // Weak (body-marker) block on the HTTP rung: climb, but never promote the host.
        var detector = new StubDetector(r => r.Html == "http"
            ? new BlockVerdict(BlockConfidence.Weak, "weak marker")
            : BlockVerdict.None);
        var loader = Loader(detector, floor, cache: null, Http(http), Browser(browser));

        await loader.LoadAsync(new PageRequest("https://h.test/a", PageType.Static));
        await loader.LoadAsync(new PageRequest("https://h.test/b", PageType.Static));

        Assert.Equal(2, http.Calls);    // each page re-tries the HTTP rung (floor unlifted)
        Assert.Equal(2, browser.Calls);
        Assert.Equal(0, floor.FloorFor("h.test"));
    }

    [Fact]
    public async Task An_auto_climb_never_launches_the_browser_not_configured_sentinel()
    {
        // A blocked Static page with no browser configured stops at HTTP and
        // returns the residual-blocked result — it does not climb into (and
        // throw from) the sentinel.
        var http = new CountingTransport("http");
        var loader = Loader(BlockHigh("http"), new HostTierFloor(), cache: null,
            Http(http), Browser(new BrowserNotConfiguredPageLoadTransport()));

        var result = await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        Assert.Equal("http", result.Html);            // residual-blocked, no throw
        Assert.Equal(0, new HostTierFloor().FloorFor("x.test")); // (sanity) fresh floors start at 0
    }

    [Fact]
    public async Task An_explicit_dynamic_request_with_no_browser_throws_the_actionable_message()
    {
        var loader = Loader(NeverBlocked, new HostTierFloor(), cache: null,
            Http(new CountingTransport("http")), Browser(new BrowserNotConfiguredPageLoadTransport()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(new PageRequest("https://x.test/", PageType.Dynamic)));
    }

    [Fact]
    public async Task A_blocked_result_is_never_written_to_the_cache()
    {
        var http = new CountingTransport("http");
        var cache = new InMemoryPageCache(TimeSpan.FromMinutes(10));
        var loader = Loader(BlockHigh("http"), new HostTierFloor(), cache, Http(http)); // single rung => residual

        await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        Assert.Null(await cache.TryReadAsync("https://x.test/", PageType.Static, default));
    }

    [Fact]
    public async Task A_clean_result_is_written_to_the_cache()
    {
        var cache = new InMemoryPageCache(TimeSpan.FromMinutes(10));
        var loader = Loader(NeverBlocked, new HostTierFloor(), cache, Http(new CountingTransport("http")));

        await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        var cached = await cache.TryReadAsync("https://x.test/", PageType.Static, default);
        Assert.Equal("http", cached!.Html);
    }

    [Fact]
    public async Task A_cache_hit_short_circuits_the_climb_without_touching_a_transport()
    {
        var cache = new InMemoryPageCache(TimeSpan.FromMinutes(10));
        await cache.WriteAsync("https://x.test/", PageType.Static,
            new PageLoadResult { Html = "cached" }, default);
        // Both rungs throw — the test passes only because the cache short-circuits.
        var loader = Loader(NeverBlocked, new HostTierFloor(), cache,
            Http(new ThrowingTransport()), Browser(new ThrowingTransport()));

        var result = await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        Assert.Equal("cached", result.Html);
    }

    [Fact]
    public async Task A_cache_write_failure_does_not_fail_the_load()
    {
        var loader = Loader(NeverBlocked, new HostTierFloor(), new ThrowingWriteCache(),
            Http(new CountingTransport("http")));

        var result = await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        Assert.Equal("http", result.Html); // the successful load is still returned
    }

    // --- ADR-0085: the climb-progress observer seam ---

    private sealed class RecordingClimbObserver : IClimbObserver
    {
        public List<ClimbStep> Steps { get; } = [];
        public void OnStep(ClimbStep step) => Steps.Add(step);
    }

    [Fact]
    public async Task Observer_sees_attempt_then_succeeded_on_a_clean_load()
    {
        var observer = new RecordingClimbObserver();
        var loader = new EscalatingPageLoader(
            [Http(new CountingTransport("http", 200)), Browser(new CountingTransport("browser"))],
            NeverBlocked, new HostTierFloor(), NullLogger.Instance, null, observer);

        await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        Assert.Collection(observer.Steps,
            s => { Assert.Equal(ClimbPhase.Attempt, s.Phase); Assert.Equal(0, s.TierIndex); },
            s =>
            {
                Assert.Equal(ClimbPhase.Succeeded, s.Phase);
                Assert.Equal(0, s.TierIndex);
                Assert.Equal(200, s.HttpStatus);
            });
    }

    [Fact]
    public async Task Observer_sees_attempt_blocked_climbing_then_succeeded_on_a_climb()
    {
        var observer = new RecordingClimbObserver();
        var loader = new EscalatingPageLoader(
            [Http(new CountingTransport("http", 403)), Browser(new CountingTransport("browser", 200))],
            BlockHigh("http"), new HostTierFloor(), NullLogger.Instance, null, observer);

        await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        Assert.Collection(observer.Steps,
            s => { Assert.Equal(ClimbPhase.Attempt, s.Phase); Assert.Equal(0, s.TierIndex); },
            s =>
            {
                Assert.Equal(ClimbPhase.Blocked, s.Phase);
                Assert.Equal(0, s.TierIndex);
                Assert.Equal(403, s.HttpStatus);
                Assert.NotNull(s.Reason);
            },
            s => { Assert.Equal(ClimbPhase.Climbing, s.Phase); Assert.Equal(0, s.TierIndex); },
            s => { Assert.Equal(ClimbPhase.Attempt, s.Phase); Assert.Equal(1, s.TierIndex); },
            s =>
            {
                Assert.Equal(ClimbPhase.Succeeded, s.Phase);
                Assert.Equal(1, s.TierIndex);
                Assert.Equal(200, s.HttpStatus);
            });
    }

    [Fact]
    public async Task Observer_sees_exhausted_when_blocked_at_the_top_tier()
    {
        var observer = new RecordingClimbObserver();
        var loader = new EscalatingPageLoader(
            [Http(new CountingTransport("http", 403)), Browser(new CountingTransport("browser", 403))],
            BlockHigh("http", "browser"), new HostTierFloor(), NullLogger.Instance, null, observer);

        await loader.LoadAsync(new PageRequest("https://x.test/", PageType.Static));

        var last = observer.Steps[^1];
        Assert.Equal(ClimbPhase.Exhausted, last.Phase);
        Assert.Equal(1, last.TierIndex);
        Assert.Equal(403, last.HttpStatus);
        Assert.Contains(observer.Steps, s => s.Phase == ClimbPhase.Climbing);
        Assert.DoesNotContain(observer.Steps, s => s.Phase == ClimbPhase.Succeeded);
    }

    private sealed class ThrowingWriteCache : IPageCache
    {
        public Task<PageLoadResult?> TryReadAsync(string url, PageType pageType, CancellationToken cancellationToken)
            => Task.FromResult<PageLoadResult?>(null);
        public Task WriteAsync(string url, PageType pageType, PageLoadResult document, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Cache backend down.");
    }
}
