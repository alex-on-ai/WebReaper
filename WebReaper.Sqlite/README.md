# WebReaper.Sqlite

SQLite embedded-store adapters for [WebReaper](https://github.com/alex-on-ai/WebReaper):
a **local durable scheduler** (and, from the next slice, a visited-link
tracker) backed by SQLite via `Microsoft.Data.Sqlite`. "Resume" is a
`SELECT … WHERE consumed = 0` over an indexed table; not a hand-rolled
append-only job file plus a sidecar position file.

This is the **opt-in robust-local durability tier**, between the
zero-dependency core file adapters and the distributed Redis / Azure Service
Bus satellites:

| Tier | Package | Shape |
|---|---|---|
| File | `WebReaper` (core) | append + 300 ms poll + position file; the zero-dep default |
| **SQLite** | **`WebReaper.Sqlite`** | embedded store, "resume" is a query, no position file |
| Redis / Azure Service Bus | `WebReaper.Redis` / `.AzureServiceBus` | distributed |

Satellite package (ADR-0009 / ADR-0012): the SQLite adapters are kept out of
the WebReaper core so the core stays dependency-light and Native-AOT-clean
`Microsoft.Data.Sqlite` is a managed wrapper over a native `e_sqlite3`
(SQLitePCLRaw), and that native-interop graph is quarantined here. The core
file scheduler is unchanged and remains the zero-dependency local default.

## Install

```
dotnet add package WebReaper.Sqlite
```

Pulls `WebReaper` (the core) as a dependency.

## Usage

Adds `WithSqliteScheduler` to `ScraperEngineBuilder`, over the core's public
`WithScheduler` registration seam:

```csharp
using WebReaper.Builders;
using WebReaper.Sqlite;

var engine = await ScraperEngineBuilder
    .Crawl("https://example.com")
    .Extract(new Schema { /* … */ })
    .WithSqliteScheduler("crawl/state.db")
    .WriteToJsonFile("output.jsonl")
    .BuildAsync();

await engine.RunAsync();
```

Kill the process mid-crawl and run it again with the same `databasePath`:
every job that was queued but not yet claimed is still there, found by the
same query; no position file to keep in sync.

`dataCleanupOnStart: true` clears the job table at start (a fresh crawl):

```csharp
.WithSqliteScheduler("crawl/state.db", dataCleanupOnStart: true)
```

## Scope

This package currently ships the **scheduler**. The SQLite visited-link
tracker (`TrackVisitedLinksInSqlite`) is the next slice. The core's role
interfaces (`IScheduler`, `IVisitedLinkTracker`) are unchanged; SQLite is an
additional adapter, not a core change.
