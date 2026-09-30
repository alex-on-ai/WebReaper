using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;
using Testcontainers.MongoDb;
using WebReaper.Builders;
using WebReaper.Domain.Parsing;
using WebReaper.IntegrationTests.Fixtures;
using WebReaper.Mongo;
using WebReaper.Redis;
using WebReaper.Sinks.Models;
using WebReaper.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace WebReaper.IntegrationTests;

/// <summary>A real MongoDB via Testcontainers (mirrors the existing
/// <see cref="RedisContainerFixture"/>). Requires Docker.</summary>
public sealed class MongoContainerFixture : IAsyncLifetime
{
    // Explicit image: Testcontainers 4.10+ obsoletes the parameterless
    // builder; mongo:6.0 is the image that constructor defaulted to.
    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:6.0").Build();
    public string ConnectionString => _mongo.GetConnectionString();
    public Task InitializeAsync() => _mongo.StartAsync();
    public Task DisposeAsync() => _mongo.DisposeAsync().AsTask();
}

/// <summary>
/// End-to-end storage-adapter coverage against REAL Redis + MongoDB
/// (Testcontainers). Each test crawls the deterministic local site through an
/// adapter, then reads the data back with the native client to prove it landed
/// — round-trips, not just "the builder method compiled". Requires Docker;
/// tagged Container so the gate (LocalServer|Cli) skips it.
/// </summary>
[Collection("LocalSite")]
[Trait("Category", "Container")]
public sealed class StorageAdapterTests
    : IClassFixture<RedisContainerFixture>, IClassFixture<MongoContainerFixture>
{
    private readonly LocalTestSite _site;
    private readonly string _redis;
    private readonly string _mongo;
    private readonly ITestOutputHelper _output;

    public StorageAdapterTests(
        LocalSiteFixture site, RedisContainerFixture redis,
        MongoContainerFixture mongo, ITestOutputHelper output)
    {
        _site = site.Site;
        _redis = redis.ConnectionString;
        _mongo = mongo.ConnectionString;
        _output = output;
    }

    private static Schema ProductSchema() => new()
    {
        new("title", ".title"),
        new("price", ".price"),
    };

    [Fact]
    public async Task Redis_sink_persists_the_record_as_a_set_member()
    {
        var key = $"wr-it:sink:{Guid.NewGuid():N}";
        var records = new ConcurrentQueue<ParsedData>();

        await using (var engine = await ScraperEngineBuilder
            .Crawl(_site.Url("/static"))
            .Extract(ProductSchema())
            .WriteToRedis(_redis, key)
            .Subscribe(records.Enqueue)
            .WithLogger(new TestOutputLogger(_output))
            .StopWhenAllLinksProcessed()
            .BuildAsync())
        {
            await engine.RunAsync();
        }

        Assert.Single(records);

        // RedisSink does SetAddAsync(key, Data.ToString()) — read the set back.
        await using var mux = await ConnectionMultiplexer.ConnectAsync(_redis);
        var members = await mux.GetDatabase().SetMembersAsync(key);
        var member = Assert.Single(members);
        Assert.Contains("Widget Pro 3000", member.ToString());
    }

    [Fact]
    public async Task Redis_scheduler_drives_a_finite_crawl_to_completion()
    {
        var queue = $"wr-it:queue:{Guid.NewGuid():N}";
        var records = new ConcurrentQueue<ParsedData>();

        await using (var engine = await ScraperEngineBuilder
            .Crawl(_site.Url("/list?page=1"))
            .Extract(new Schema { new("title", ".title") })
            .Follow("a.item")
            .WithRedisScheduler(_redis, queue, dataCleanupOnStart: true)
            .Subscribe(records.Enqueue)
            .WithLogger(new TestOutputLogger(_output))
            .StopWhenAllLinksProcessed()
            .BuildAsync())
        {
            await engine.RunAsync();
        }

        Assert.Equal(3, records.Count);   // 3 item leaf pages, Redis-backed queue
    }

    [Fact]
    public async Task Mongo_sink_inserts_a_document_per_record()
    {
        const string db = "webreaper_it";
        var coll = $"items_{Guid.NewGuid():N}";

        await using (var engine = await ScraperEngineBuilder
            .Crawl(_site.Url("/static"))
            .Extract(ProductSchema())
            .WriteToMongoDb(_mongo, db, coll, dataCleanupOnStart: true)
            .WithLogger(new TestOutputLogger(_output))
            .StopWhenAllLinksProcessed()
            .BuildAsync())
        {
            await engine.RunAsync();
        }

        var client = new MongoClient(_mongo);
        var docs = await client.GetDatabase(db)
            .GetCollection<BsonDocument>(coll)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync();

        var doc = Assert.Single(docs);
        Assert.Equal("Widget Pro 3000", doc["title"].AsString.Trim());
    }
}
