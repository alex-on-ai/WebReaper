# WebReaper.Redis

Redis adapters for [WebReaper](https://github.com/alex-on-ai/WebReaper): a
distributed scheduler, visited-link tracker, result sink, and Redis-backed
scraper-config and cookie storage. Swapping the scheduler + config storage +
link tracker to Redis lets multiple workers share crawl state.

Satellite package (ADR-0009): the Redis adapters are kept out of the WebReaper
core so the core stays dependency-light and Native-AOT-clean. All adapters in
this package share one `ConnectionMultiplexer` per connection string
(ADR-0005), preserved intra-package.

## Install

```
dotnet add package WebReaper.Redis
```

Pulls `WebReaper` (the core) as a dependency.

## Usage

Adds `WriteToRedis`, `WithRedisScheduler`, `TrackVisitedLinksInRedis`,
`WithRedisConfigStorage` and `WithRedisCookieStorage` to
`ScraperEngineBuilder`:

```csharp
using WebReaper.Builders;
using WebReaper.Redis;

var engine = await ScraperEngineBuilder
    .Crawl("https://example.com/catalog")
    .Extract(new() { new("title", "h1"), new("price", ".price") })
    .WithRedisScheduler("localhost:6379", queueName: "jobs")
    .TrackVisitedLinksInRedis("localhost:6379", redisKey: "visited")
    .WriteToRedis("localhost:6379", redisKey: "results")
    .BuildAsync();

await engine.RunAsync();
```

## License

MIT (ADR-0017). Part of the WebReaper project.
