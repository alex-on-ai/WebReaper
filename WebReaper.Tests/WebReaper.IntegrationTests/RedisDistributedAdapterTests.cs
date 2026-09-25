using Testcontainers.Redis;
using WebReaper.Redis;
using Xunit;

namespace WebReaper.IntegrationTests;

// ADR-0022 distributed adapters against a REAL Redis (Testcontainers).
//
// Placement (revised per maintainer): this is the dedicated integration-test
// project — the heavy/real, on-demand tier (it already runs live Puppeteer +
// network) — NOT the deliberately-offline WebReaper.Redis.Tests satellite
// suite. The in-memory analogs are pinned offline in WebReaper.UnitTests
// (OutstandingWorkLatchTests / VisitedLinkTrackerTests, which CI runs); these
// verify the Redis composition we actually wrote — atomic INCR/DECR, the
// SET-NX one-shot fence, atomic SADD — that ADR-0022 otherwise shipped on
// reasoning. Requires Docker; like the existing live-site tests it is not in
// the CI gate today (CI runs only WebReaper.UnitTests).
public sealed class RedisContainerFixture : IAsyncLifetime
{
    // Explicit image: Testcontainers 4.10+ obsoletes the parameterless
    // builder; redis:7.0 is the image that constructor defaulted to.
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.0").Build();

    public string ConnectionString => _redis.GetConnectionString();

    public Task InitializeAsync() => _redis.StartAsync();

    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();
}

public class RedisDistributedAdapterTests : IClassFixture<RedisContainerFixture>
{
    private readonly string _conn;

    public RedisDistributedAdapterTests(RedisContainerFixture fx) => _conn = fx.ConnectionString;

    private RedisOutstandingWorkLatch Latch() =>
        new(_conn, $"webreaper-it:latch:{Guid.NewGuid():N}");

    private RedisVisitedLinkTracker Tracker() =>
        new(_conn, $"webreaper-it:visited:{Guid.NewGuid():N}");

    [Fact]
    public async Task Latch_trips_exactly_once_when_credit_reaches_zero()
    {
        var latch = Latch();
        await latch.SeedAsync(3);

        Assert.False(await latch.SignalProcessedAsync(0)); // 3 -> 2
        Assert.False(await latch.SignalProcessedAsync(0)); // 2 -> 1
        Assert.True(await latch.SignalProcessedAsync(0));   // 1 -> 0  (trip)
    }

    [Fact]
    public async Task Latch_childful_registration_credits_and_returns_in_one_atomic_step()
    {
        var latch = Latch();
        await latch.SeedAsync(1);

        // Root done with 2 children: 2 credited + 1 returned in one INCRBY —
        // net +1, so 1 -> 2. The latch cannot trip here.
        Assert.False(await latch.SignalProcessedAsync(2)); // 1 -> 2 (NOT zero)
        Assert.False(await latch.SignalProcessedAsync(0)); // 2 -> 1
        Assert.True(await latch.SignalProcessedAsync(0));   // 1 -> 0 (trip)
    }

    [Fact]
    public async Task Latch_one_shot_is_SET_NX_fenced_against_a_redelivered_zero()
    {
        var latch = Latch();
        await latch.SeedAsync(1);

        Assert.True(await latch.SignalProcessedAsync(0));   // trip + completion claimed
        // At-least-once redelivery drives the counter to/below zero again; the
        // SET-NX fence must keep the one-shot from firing a second time.
        Assert.False(await latch.SignalProcessedAsync(0));
        Assert.False(await latch.SignalProcessedAsync(0));
    }

    [Fact]
    public async Task Latch_exactly_one_caller_trips_under_concurrency()
    {
        var latch = Latch();
        await latch.SeedAsync(200);

        var trips = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => latch.SignalProcessedAsync(0))));

        Assert.Equal(1, trips.Count(t => t));
    }

    [Fact]
    public async Task Tracker_TryAdd_is_true_once_per_distinct_url_then_false()
    {
        var tracker = Tracker();

        Assert.True(await tracker.TryAddVisitedLinkAsync("a"));
        Assert.False(await tracker.TryAddVisitedLinkAsync("a"));   // already a member
        Assert.True(await tracker.TryAddVisitedLinkAsync("b"));
        Assert.Equal(2, await tracker.GetVisitedLinksCount());
    }

    [Fact]
    public async Task Tracker_TryAdd_is_atomic_under_concurrency()
    {
        var tracker = Tracker();

        var results = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => tracker.TryAddVisitedLinkAsync("https://x.test/same"))));

        Assert.Equal(1, results.Count(won => won));
        Assert.Equal(1, await tracker.GetVisitedLinksCount());
    }
}
