# WebReaper.Mongo

MongoDB adapters for [WebReaper](https://github.com/alex-on-ai/WebReaper): a
result sink, plus MongoDB-backed scraper-config and cookie storage.

Satellite package (ADR-0009): the Mongo adapters are kept out of the WebReaper
core so the core stays dependency-light and Native-AOT-clean. MongoDB.Driver's
BSON serialization is reflection-based and not trim/AOT-clean, so this package
is deliberately not AOT-guaranteed.

## Install

```
dotnet add package WebReaper.Mongo
```

Pulls `WebReaper` (the core) as a dependency.

## Usage

Adds `WriteToMongoDb`, `WithMongoDbConfigStorage` and
`WithMongoDbCookieStorage` to `ScraperEngineBuilder`:

```csharp
using WebReaper.Builders;
using WebReaper.Mongo;

var engine = await ScraperEngineBuilder
    .Crawl("https://example.com/catalog")
    .Extract(new() { new("title", "h1"), new("price", ".price") })
    .WriteToMongoDb(
        connectionString: "mongodb://localhost:27017",
        databaseName: "scrape",
        collectionName: "items",
        dataCleanupOnStart: false)
    .BuildAsync();

await engine.RunAsync();
```

## License

MIT (ADR-0017). Part of the WebReaper project.
