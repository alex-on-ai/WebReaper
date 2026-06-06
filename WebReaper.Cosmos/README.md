# WebReaper.Cosmos

Azure Cosmos DB sink for [WebReaper](https://github.com/alex-on-ai/WebReaper).

Satellite package (ADR-0009): the Cosmos sink is kept out of the WebReaper
core so the core stays dependency-light and Native-AOT-clean. The Cosmos SDK
drags Newtonsoft.Json and a native interop graph, so this package is
deliberately not AOT-guaranteed; installing it is opting into that.

## Install

```
dotnet add package WebReaper.Cosmos
```

Pulls `WebReaper` (the core) as a dependency.

## Usage

Adds `WriteToCosmosDb(...)` to `ScraperEngineBuilder`:

```csharp
using WebReaper.Builders;
using WebReaper.Cosmos;

var engine = await ScraperEngineBuilder
    .Crawl("https://example.com/catalog")
    .Extract(new() { new("title", "h1"), new("price", ".price") })
    .WriteToCosmosDb(
        endpointUrl: "https://my-account.documents.azure.com:443/",
        authorizationKey: "<key>",
        databaseId: "scrape",
        containerId: "items",
        dataCleanupOnStart: false)
    .BuildAsync();

await engine.RunAsync();
```

## License

MIT (ADR-0017). Part of the WebReaper project.
