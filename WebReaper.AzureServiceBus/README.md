# WebReaper.AzureServiceBus

Azure Service Bus scheduler for
[WebReaper](https://github.com/alex-on-ai/WebReaper): a distributed job queue
backed by an Azure Service Bus queue, for sharing crawl state across workers
and serverless functions.

Satellite package (ADR-0009): the Azure Service Bus scheduler is kept out of
the WebReaper core so the core stays dependency-light and Native-AOT-clean.

## Install

```
dotnet add package WebReaper.AzureServiceBus
```

Pulls `WebReaper` (the core) as a dependency.

## Usage

Adds `WithAzureServiceBusScheduler(...)` to `ScraperEngineBuilder`:

```csharp
using WebReaper.Builders;
using WebReaper.AzureServiceBus;

var engine = await ScraperEngineBuilder
    .Crawl("https://example.com/catalog")
    .Extract(new() { new("title", "h1"), new("price", ".price") })
    .WithAzureServiceBusScheduler(
        connectionString: "<service-bus-connection-string>",
        queueName: "scrape-jobs")
    .BuildAsync();

await engine.RunAsync();
```

## License

MIT (ADR-0017). Part of the WebReaper project.
