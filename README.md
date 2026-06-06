![logo](https://user-images.githubusercontent.com/6662454/221978697-3f35564a-f442-46e6-9182-f2604a17e1f6.png)

# WebReaper

[![NuGet](https://img.shields.io/nuget/v/WebReaper)](https://www.nuget.org/packages/WebReaper)
[![CI](https://github.com/pavlovtech/WebReaper/actions/workflows/CI.yml/badge.svg)](https://github.com/pavlovtech/WebReaper/actions/workflows/CI.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)

AI-native web scraper. Single binary with a bundled Claude Code skill.

## Install

**macOS / Linux (Homebrew):**

```bash
brew install pavlovtech/webreaper/webreaper
```

**Any POSIX shell (install.sh):**

```bash
curl -fsSL https://raw.githubusercontent.com/pavlovtech/WebReaper/master/scripts/install.sh | sh
```

**.NET library:**

```bash
dotnet add package WebReaper
```

Windows binaries are on the [GitHub Releases page](https://github.com/pavlovtech/WebReaper/releases/latest); `winget` and `Scoop` are on the v10.1 roadmap.

**Updating:** `brew upgrade webreaper` (Homebrew), or re-run the install.sh line with `--upgrade` appended (`| sh -s -- --upgrade`), or `dotnet add package WebReaper` (library). When a newer release exists, `scrape` / `crawl` / `map` print a one-line upgrade hint on stderr (interactive terminals only, never in a pipe or CI); disable that check with `WEBREAPER_NO_UPDATE_CHECK=1`.

## 30-second demo

```bash
$ webreaper scrape https://news.ycombinator.com
# Hacker News

- [Show HN: ...](https://news.ycombinator.com/item?id=...)
- [Ask HN: ...](https://news.ycombinator.com/item?id=...)
...

$ webreaper init
Wrote WebReaper Agent Skill to .claude/skills/webreaper/SKILL.md

Try it out:
  webreaper scrape https://example.com
  webreaper map https://example.com
```

After `webreaper init`, the next Claude Code session picks up the skill and routes scraping intents (*"give me the markdown of X"*, *"what blog posts are on Y"*, *"scrape the top 5 articles"*) to `webreaper` automatically.

## Table of contents

- [Why WebReaper](#why-webreaper)
- [Quick start](#quick-start)
- [Bot protection that just works](#bot-protection-that-just-works)
- [Power your agent](#power-your-agent)
- [AI features](#ai-features)
- [Use cases](#use-cases)
- [Packages](#packages)
- [Compared to Firecrawl, Crawl4AI, and Crawlee](#compared-to-firecrawl-crawl4ai-and-crawlee)
- [API overview](#api-overview)
- [Architecture and interfaces](docs/architecture.md)
- [Repository structure](#repository-structure)
- [License](#license)

## Why WebReaper

<table>
<tr>
<td width="33%" valign="top"><strong>🪶 Drop on PATH, run.</strong><br><br>No Docker, no Postgres, no signup. ~12 MB binary.</td>
<td width="33%" valign="top"><strong>🤖 AI-native by composition.</strong><br><br>Markdown by default. Schema extraction, LLM fallback, self-healing selectors, autonomous agent. Stack with <code>.With…()</code>.</td>
<td width="33%" valign="top"><strong>🔌 Bring any LLM.</strong><br><br>OpenAI, Anthropic, Ollama, Azure OpenAI, llamafile, via <code>Microsoft.Extensions.AI</code>.</td>
</tr>
<tr>
<td width="33%" valign="top"><strong>🛡 Bot-checks handled automatically.</strong><br><br>A blocked page climbs HTTP → browser → stealth on its own, per page and host-sticky. Challenge pages are dropped, never returned as data. No flag needed for the browser fallback.</td>
<td width="33%" valign="top"><strong>📡 Distributed when needed.</strong><br><br>Swap scheduler, tracker, sink to Redis, MongoDB, SQLite, Azure Service Bus, Cosmos. Same code.</td>
<td width="33%" valign="top"><strong>📜 MIT, not AGPL.</strong><br><br>Embed in commercial software, fork, modify, redistribute. Firecrawl's AGPL requires open-sourcing your service or paying for a commercial license.</td>
</tr>
</table>

## Quick start

### CLI

```bash
# One page as Markdown
webreaper scrape https://example.com

# Save Markdown to a file
webreaper scrape https://example.com --output page.md

# Discover URLs on a site
webreaper map https://example.com --search /blog/ --max-urls 50

# Crawl a whole site recursively (every on-domain page) to JSON Lines
webreaper crawl https://example.com > pages.jsonl

# Structured fields with a JSON schema (output: JSON; multi-page: JSON Lines)
webreaper scrape https://example.com --schema schema.json

# Schema-free extraction with an LLM (bring your own OpenAI-compatible endpoint)
webreaper scrape https://example.com --prompt "title and author" \
  --model gpt-4o-mini --llm-url https://api.openai.com/v1

# Whole site, fields, cheaply: infer a schema once, then extract the rest
webreaper crawl https://example.com --infer "product name and price" \
  --model gpt-4o-mini --llm-url https://api.openai.com/v1 --output-dir ./out

# JS-rendered single-page app
webreaper scrape https://example.com --browser

# Bot-protected site: a plain scrape already auto-climbs HTTP -> browser on a
# block; --stealth starts at a stealth backend (--auto-stealth = no prompt, for CI)
webreaper scrape https://example.com --stealth

# Install the Claude Code skill
webreaper init
```

The CLI is built Native-AOT (ADR-0043), ships as a single binary on every tagged GitHub release across six RIDs (`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-arm64`), and is block-aware with automatic browser/stealth escalation (ADR-0083). The macOS binaries are Apple codesigned and notarized (ADR-0071); Homebrew installs run without Gatekeeper warnings on a clean machine.

### Library

```csharp
using WebReaper.Builders;

var engine = await ScraperEngineBuilder
    .Crawl("https://news.ycombinator.com")
    .AsMarkdown()
    .WriteToConsole()
    .BuildAsync();

await engine.RunAsync();
```

That is HTTP-only, no extra packages, no schema. For structured fields, swap `AsMarkdown()` for `Extract(schema)`; for JS-rendered pages, `Crawl` for `CrawlWithBrowser` plus a transport satellite ([`WebReaper.Playwright`](#packages) or [`WebReaper.Cdp`](#packages)). The full surface is in the [API overview](#api-overview).

## Bot protection that just works

Most scrapers make you opt into a browser up front and guess when a site is blocking you. WebReaper detects the block and escalates on its own, one page at a time:

```
HTTP  ->  browser (Chromium)  ->  stealth (CloakBrowser)
          (climbs only when a page actually looks blocked)
```

- **Automatic.** A plain `scrape` or `crawl` starts on a fast HTTP fetch and climbs to a real browser only when a page looks blocked (a challenge status, response header, or body marker). No flag needed for the browser fallback.
- **Per page, host-sticky.** The first confirmed block on a host lifts that host's floor, so the rest of a whole-site crawl starts at the working tier instead of re-paying the failed one. You pay for the climb once, not per page.
- **No garbage in your data.** A page still blocked at the top tier is dropped, never written to your output, and the run exits non-zero so an unattended job knows. Clean data or a clear signal, never a challenge page masquerading as content.
- **You decide on stealth.** `--stealth` starts at the stealth backend; `--auto-stealth` (or `WEBREAPER_AUTO_STEALTH=1`) enables it unattended; `--no-auto-stealth` caps the climb at a vanilla browser. The ~220 MB stealth backend downloads only if you opt in.

Self-hosted, single binary, no cloud round-trip ([ADR-0083](docs/adr/0083-escalating-page-loader.md)).

## Power your agent

### Claude Code skill

```bash
webreaper init
```

Writes a polished `SKILL.md` to `.claude/skills/webreaper/`. Claude Code loads it on the next session and routes scraping intents to the CLI: *"scrape the top 5 stories on HN and summarize each"*, *"give me the markdown of this article"*, *"this Cloudflare-protected site is blocking me"*. The skill describes when to prefer `webreaper` over the built-in `WebFetch` (artifacts and structured data go through `webreaper`; conversational answers stay on `WebFetch`).

### MCP server

Two MCP servers expose the same tools (`scrape`, `map`, `extract`, `extract_with_prompt`, `extract_inferred`, `crawl`) for clients that speak the Model Context Protocol:

- **[`WebReaper.Mcp`](https://www.nuget.org/packages/WebReaper.Mcp)** (ADR-0049) speaks **stdio**, for local clients that spawn a process: Cursor, Claude Desktop, Copilot Studio.
- **[`WebReaper.Mcp.AspNetCore`](https://www.nuget.org/packages/WebReaper.Mcp.AspNetCore)** (ADR-0086) speaks **Streamable HTTP**, for clients that connect to a URL. The headline case is **n8n**, whose MCP Client node is URL-only and cannot reach a stdio server. Run the Chromium-baked image, point n8n's MCP Client node at the URL with a bearer token, and call the tools from a workflow:

  ```bash
  docker run -p 8080:8080 -e WEBREAPER_MCP_TOKEN=your-secret \
    ghcr.io/pavlovtech/webreaper-mcp-http:latest
  ```

  See the [n8n quickstart](docs/mcp-http-quickstart.md).

Both are thin facades over the library API; the primary agent surface remains the CLI.

### CLI inside any agent harness

The single binary works inside any shell-spawning agent: LangChain `ShellTool`, OpenAI Assistants code-interpreter, GitHub Actions, internal scripts. Zero runtime to install; one syscall to invoke.

## AI features

### 1. LLM-ready Markdown, no schema

```csharp
await ScraperEngineBuilder
    .Crawl("https://example.com")
    .AsMarkdown()                   // ADR-0040 + ADR-0063
    .WriteToConsole()
    .BuildAsync()
    .Result.RunAsync();
```

The Markdown extractor (`HtmlToMarkdown` primitive, ADR-0063; `MarkdownContentExtractor` adapter, ADR-0040) emits `{title, markdown}` per page. Pass the output straight into a follow-up LLM prompt.

### 2. Source-gen schemas with compile-time guards

```csharp
using WebReaper.Extraction.Attributes;

[ScrapeSchema]
public partial class Article
{
    [ScrapeField("h1")]                                              public string? Title { get; set; }
    [ScrapeField(".views", Type = SchemaFieldType.Integer)]          public int Views { get; set; }
    [ScrapeField(".tag", IsList = true)]                             public List<string> Tags { get; set; } = new();
}

// Emitted at compile time, reflection-free, AOT-clean:
//   public static Schema Schema { get; }
//   public static Article Materialize(JsonObject json)

var engine = await ScraperEngineBuilder
    .Crawl("https://example.com/post")
    .Extract(Article.Schema)
    .Subscribe(p => HandleArticle(Article.Materialize(p.Data)))
    .BuildAsync();
```

The [`WebReaper.Extraction.Generators`](https://www.nuget.org/packages/WebReaper.Extraction.Generators) Roslyn analyzer (ADR-0045) emits the schema and a `Materialize` function. Schema typos are compile errors; the generated path uses no reflection so it's AOT-clean.

### 3. LLM safety net for deterministic extraction

Three composable patterns where the LLM only fires when the deterministic path can't deliver. Same proposer-validator shape across all three.

```csharp
using WebReaper.AI;

// (a) Fire LLM only when a field returns empty (ADR-0046)
.WithLlmFallback(chatClient)

// (b) Repair a broken selector once per (Schema, field), cache forever (ADR-0047)
.WithLlmSelfHealing(chatClient)

// (c) No schema at all: infer it from the URL, re-infer on validator failure (ADR-0067 + ADR-0069)
.UseAi(chatClient, AiPolicyMode.Inferred)
```

Stable pages cost zero LLM calls; broken pages cost one call per (page, field) and cache. Schema inference is one call per site (cached for the run). The `WebReaper.AI` satellite is built on `Microsoft.Extensions.AI` so any `IChatClient` works.

### 4. Autonomous agent: page selection by goal

```csharp
using WebReaper.AI;

var result = await LlmAgent.RunAsync(
    "https://example.com",
    goal: "Find the contact email and phone number for the support team.",
    chatClient);
```

`AgentEngine` (ADR-0051) runs a sequential `decide → persist → execute` loop over a closed-sum `AgentDecision` (`Extract | Follow | Act | Stop`). The brain picks each step from the bounded `AgentState` view; the engine validates (visited-link enforcement, MaxSteps cap), persists (`IAgentRunStore`), and dispatches. Durable resume across process restarts.

### 5. Semantic page actions

```csharp
ScraperEngineBuilder
    .CrawlWithBrowser(url, actions => actions
        .Do(PageAction.SemanticAct("click 'sign in'"))   // ADR-0050
        .Do(PageAction.WaitForNetworkIdle())
        .Build())
    .WithLlmActionResolver(chatClient)
    // ...
```

A natural-language `PageAction.SemanticAct(intent)` is one of the ten closed-sum arms (ADR-0050 added it; ADR-0074 added `Fill` / `Press` / `ScrollIntoView` for form interactions). The transport resolves it once via the registered `IActionResolver` (LLM-backed by default), dispatches the concrete arm, and caches the resolution per crawl by intent string. First page pays the LLM, subsequent same-intent pages dispatch the cached arm with no LLM call.

### Runnable end-to-end demo

[`Examples/WebReaper.AiNativeShowcase`](Examples/WebReaper.AiNativeShowcase/Program.cs) wires every feature in this section:

```bash
dotnet run --project Examples/WebReaper.AiNativeShowcase -- markdown
dotnet run --project Examples/WebReaper.AiNativeShowcase -- sourcegen
dotnet run --project Examples/WebReaper.AiNativeShowcase -- llm
dotnet run --project Examples/WebReaper.AiNativeShowcase -- router
dotnet run --project Examples/WebReaper.AiNativeShowcase -- changetrack
```

## Use cases

- **Build LLM context from blog or docs sites.** `webreaper map` plus `webreaper scrape` per URL, piped into a prompt or a vector DB.
- **Monitor competitor pricing or status pages for changes.** Schedule the CLI with cron or a worker, store records in MongoDB or SQLite, plug in `.WithChangeTracking()` (ADR-0048) so the sink fires only on diff. Hash-based dedup; cron-friendly.
- **Run an autonomous research agent.** `LlmAgent.RunAsync(url, goal, chatClient)` decides which links to follow until the goal is met. Durable resume across restarts.
- **Scrape Cloudflare-protected catalogs.** A plain `scrape` auto-climbs HTTP to a browser on a block; add `--stealth` (or `--auto-stealth` for unattended runs) to escalate to a stealth backend. Blocked pages are dropped, never emitted as challenge-page garbage.
- **Generate clean datasets from semi-structured pages.** `[ScrapeSchema]` POCO plus the source generator; reflection-free, AOT-compiles into a native binary.
- **Embed a scraping primitive in your own app.** `dotnet add package WebReaper`; the public registration seam lets you plug Redis, Cosmos DB, your own sink.

## Packages

The release ships fifteen packages (one core, fourteen satellites), all versioned in lockstep. The core stays dependency-light and Native-AOT-publishable with zero warnings; satellites bring their own SDK dependencies and quarantine them off the core graph (ADR-0009).

| Package | Add it for | Key builder calls |
|---|---|---|
| **WebReaper** | Core. HTTP crawl and parse, in-memory and file scheduler / visited-link tracker / cookie and config storage, Console / CSV / JSON-Lines sinks, Markdown extractor, schema fold. Dependency-light, Native-AOT-ready, Newtonsoft-free. | `Crawl` `Extract` `AsMarkdown` `Follow` `Paginate` `Sweep` `WriteToJsonFile` `WriteToCsvFile` `WriteToConsole` `Subscribe` |
| **WebReaper.Cdp** | Raw CDP `IPageLoadTransport` (ADR-0052). AOT-clean (no PuppeteerSharp / Playwright dependency); System.Net.WebSockets plus System.Text.Json source-gen. Bedrock for the stealth pattern. | `.WithCdpPageLoader(cdpUrl)` (BYO) or `.WithCdpPageLoader(CdpLaunchOptions)` (launch managed Chromium) |
| **WebReaper.Playwright** | Microsoft.Playwright-backed transport (ADR-0053). Multi-browser (Chromium default; Firefox / WebKit opt-in). All ten `PageAction` arms supported. Use for modern multi-browser needs; pair with `WebReaper.Cdp` for AOT or stealth. | `.WithPlaywrightPageLoader()` |
| **WebReaper.Stealth.CloakBrowser** | First stealth-backend satellite (ADR-0054). Auto-downloads CloakBrowser on first use; composes on `WebReaper.Cdp`. Disposable via the ADR-0058 engine teardown chain. | `.WithCloakBrowser()` |
| **WebReaper.AI** | LLM extraction, LLM action resolver, LLM brain, LLM self-healing, LLM schema inferrer (ADR-0044 / 0050 / 0051 / 0067). Built on `Microsoft.Extensions.AI`; bring your own `IChatClient`. | `.WithLlmFallback` `.WithLlmSelfHealing` `.WithLlmExtractor` `.WithLlmAgentBrain` `.WithLlmActionResolver` `.WithLlmSchemaInferrer` `.UseAi(client)` |
| **WebReaper.AI.Http** | AOT-safe OpenAI-compatible `IChatClient` (ADR-0084): raw `HttpClient` plus System.Text.Json source-gen, no provider SDK. The bring-your-own client the AOT CLI and the MCP servers use for prompt extraction. | `new OpenAiCompatibleChatClient(baseUrl, model, apiKey)` |
| **WebReaper.Extraction.Attributes** | The `[ScrapeSchema]` / `[ScrapeField]` marker types. Standalone, no runtime cost. | `[ScrapeSchema]` `[ScrapeField("selector")]` |
| **WebReaper.Extraction.Generators** | Roslyn source generator that emits `static Schema` plus reflection-free `static Materialize(JsonObject)` (ADR-0045). `DevelopmentDependency=true`; does not propagate at runtime. | compile-time only |
| **WebReaper.Mcp** | MCP server `Exe` exposing scrape / map / extract / extract_with_prompt / extract_inferred / crawl as MCP tools over **stdio** (ADR-0049). Interop adapter for local MCP clients (Cursor, Claude Desktop). | the package _is_ the executable |
| **WebReaper.Mcp.AspNetCore** | MCP server over **Streamable HTTP** (ADR-0086) for URL-based clients like n8n; same tools as `WebReaper.Mcp`, plus bearer-token auth and a `WEBREAPER_CDP_URL` browser sidecar. Also ships as a Chromium-baked container image. | the package _is_ the executable; or `docker run ghcr.io/pavlovtech/webreaper-mcp-http` |
| **WebReaper.Mongo** | MongoDB result sink and MongoDB-backed config / cookie storage. | `.WriteToMongoDb(...)` `.WithMongoDbConfigStorage(...)` `.WithMongoDbCookieStorage(...)` |
| **WebReaper.Redis** | Redis scheduler, visited-link tracker, result sink, config / cookie storage. | `.WithRedisScheduler(...)` `.TrackVisitedLinksInRedis(...)` `.WriteToRedis(...)` `.WithRedisConfigStorage(...)` `.WithRedisCookieStorage(...)` |
| **WebReaper.AzureServiceBus** | Distributed scheduler over an Azure Service Bus queue. | `.WithAzureServiceBusScheduler(...)` |
| **WebReaper.Cosmos** | Azure Cosmos DB result sink. | `.WriteToCosmosDb(...)` |
| **WebReaper.Sqlite** | Local **durable** scheduler and visited-link tracker on an embedded SQLite store; resume is a query, no position file. Opt-in robust-local tier (no server, unlike Redis). | `.WithSqliteScheduler(...)` `.TrackVisitedLinksInSqlite(...)` |

`WebReaper.Cli` (the AOT single-binary; ADR-0043) is not a NuGet package; it ships as platform binaries on every GitHub release (Native-AOT plus `dotnet tool install` are mutually incompatible on one target). Install via Homebrew or `install.sh`, or build from source.

## Compared to Firecrawl, Crawl4AI, and Crawlee

|  | WebReaper | Firecrawl | Crawl4AI | WebFetch (Claude) |
|---|---|---|---|---|
| **License** | MIT | AGPL-3.0 (plus commercial) | Apache 2.0 | bundled with Claude |
| **Install** | one binary, ~12 MB | Docker + Postgres + Redis (self-host) or hosted | Docker + Python + Playwright | nothing to install |
| **Cost** | free | metered API plus free tier | free | included with Claude |
| **BYO LLM** | any `IChatClient` | no (their model) | yes (LiteLLM) | Claude only |
| **Autonomous agent** | `Agent.RunAsync()` durable, in-process | `/agent` endpoint (cloud only) | code it yourself | not available |
| **Whole-site crawl** | `webreaper crawl` / `.Sweep()`: recursive, on-domain, sitemap-seeded, streams JSON Lines | `crawl` (cloud or self-host) | deep-crawl strategies (code it yourself) | no (single fetch) |
| **Page actions** | 10 declarative arms: `Click`, `Wait`, `Fill`, `Press`, `ScrollToEnd`, `ScrollIntoView`, `WaitForSelector`, `WaitForNetworkIdle`, `EvaluateExpression`, `SemanticAct` (natural-language) | 9 actions: `wait`, `click`, `write`, `press`, `scroll`, `executeJavascript`, plus 3 observation (`screenshot`, `pdf`, `scrape`) | JS hooks; no closed-sum vocabulary | none (single-fetch only) |
| **Bot-protected** | automatic HTTP → browser → stealth climb, per page, host-sticky, self-hosted | cloud yes; self-host degraded (no Fire-engine) | BYO | no |
| **Claude Code skill** | `webreaper init` bundled | community `firecrawl-claude-code-skill` wraps the cloud API | none official | not applicable |

[Crawlee](https://github.com/apify/crawlee) (Apify's Node/Python library) is also worth knowing; it covers similar ground to the WebReaper library API but doesn't ship a binary, a Claude Code skill, or a built-in LLM safety net. Use it if you're already in the Apify ecosystem.

The closest reference is Firecrawl: same AI-native positioning, opposite distribution shape. Firecrawl optimises for the hosted-API flow; WebReaper optimises for the local-binary flow. If you want a managed cloud with someone else's proxies and infra, Firecrawl is the buy. If you want a binary that runs locally with your own LLM key and no metering, WebReaper is the build.

## API overview

The library is a fluent builder over a small set of seams. For the deep seam-by-seam reference (interfaces, main entities, custom sinks), see [`docs/architecture.md`](docs/architecture.md).

### Schema extraction

```csharp
using WebReaper.Builders;

var engine = await ScraperEngineBuilder
    .Crawl("https://www.alexpavlov.dev/blog")
    .Extract(new()
    {
        new("title", ".text-3xl.font-bold"),
        new("text", ".max-w-max.prose.prose-dark")
    })
    .Follow("a.text-gray-900.transition")
    .WriteToJsonFile("output.json")
    .PageCrawlLimit(10)
    .WithParallelismDegree(30)
    .LogToConsole()
    .BuildAsync();

await engine.RunAsync();
```

Each `new("field", "css-selector")` is a leaf; nest schemas for objects, set `IsList = true` for arrays, set `Attr = "href"` to read an HTML attribute instead of inner text.

### Collect records in-process

Get the scraped records back in your own process, no file and no custom sink. Pass `Subscribe` (ADR-0038) the `Add` of a thread-safe collection, then read them after the run:

```csharp
using System.Collections.Concurrent;
using WebReaper.Builders;
using WebReaper.Sinks.Models;

var records = new ConcurrentBag<ParsedData>();

var engine = await ScraperEngineBuilder
    .Crawl("https://news.ycombinator.com")
    .AsMarkdown()
    .Subscribe(records.Add)            // called concurrently; collect into a thread-safe type
    .BuildAsync();

await engine.RunAsync();

foreach (var record in records)
    Console.WriteLine($"{record.Url}: {record.Data["markdown"]?.GetValue<string>()?.Length ?? 0} chars");
```

The Crawl driver fans out to sinks concurrently, so a `ConcurrentBag` is the right collector, not a bare `List`. For structured fields, read your schema keys out of `record.Data` instead of `"markdown"`.

### Parsing dynamic pages (SPA)

For JS-rendered pages, swap `Crawl` for `CrawlWithBrowser` and register a browser transport. Two satellites are available.

**[`WebReaper.Playwright`](https://www.nuget.org/packages/WebReaper.Playwright)** is the modern default (ADR-0053): multi-browser, all ten `PageAction` arms.

```csharp
using WebReaper.Builders;
using WebReaper.Playwright;

await ScraperEngineBuilder
    .CrawlWithBrowser("https://example.com")
    .Extract(new() { new("title", "h1") })
    .WithPlaywrightPageLoader()
    .BuildAsync();
```

**[`WebReaper.Cdp`](https://www.nuget.org/packages/WebReaper.Cdp)** is the AOT-clean alternative (ADR-0052): raw CDP over System.Net.WebSockets, AOT-publishable. Use for AOT consumers or as the base for stealth backends.

```csharp
using WebReaper.Cdp;

.WithCdpPageLoader(new CdpLaunchOptions { Headless = true })
// or .WithCdpPageLoader(cdpUrl: "http://localhost:9222") for BYO browser
```

For bot-protected sites, layer **[`WebReaper.Stealth.CloakBrowser`](https://www.nuget.org/packages/WebReaper.Stealth.CloakBrowser)** (ADR-0054) on top of `WebReaper.Cdp`:

```csharp
using WebReaper.Stealth.CloakBrowser;

.WithCloakBrowser()    // auto-downloads CloakBrowser on first use, ~220 MB
```

For visible-browser debugging, add `.HeadlessMode(false)`.

### Running JavaScript and page actions

Drive the page as it loads. Pass an actions lambda.

```csharp
using WebReaper.Builders;
using WebReaper.Playwright;
using WebReaper.Domain.PageActions;

await ScraperEngineBuilder
    .CrawlWithBrowser("https://www.reddit.com/r/dotnet/", actions => actions
        .ScrollToEnd()
        .Build())
    .Extract(new() { new("title", "h1") })
    .WithPlaywrightPageLoader()
    .BuildAsync();
```

`PageActionBuilder` exposes `Click`, `Wait`, `ScrollToEnd`, `ScrollIntoView`, `WaitForSelector`, `WaitForNetworkIdle`, `EvaluateExpression`, `Fill`, `Press`, `SemanticAct`, `Repeat` / `RepeatWithDelay`, and `Build()`. `Fill(selector, value)` / `Press(key)` / `ScrollIntoView(selector)` (ADR-0074) carry an implicit 30 s auto-wait and use the React-friendly native-setter trick on the CDP transport, so controlled components in React / Vue / Svelte observe the change. `SemanticAct` (ADR-0050) accepts a natural-language intent and resolves it via the registered `IActionResolver` (see [AI features §5](#5-semantic-page-actions)).

### Persist progress locally

Survive `kill -9` and resume across restarts. Two adapters: file-backed (zero deps, in core) and SQLite-backed (durable, opt-in via `WebReaper.Sqlite`).

```csharp
using WebReaper.Builders;
using WebReaper.Sqlite;

await ScraperEngineBuilder
    .Crawl("https://example.com")
    .Extract(new() { new("name", "h1") })
    .Follow(".forumlink>a")
    .Paginate("a.torTopic", ".pg")
    .WriteToJsonFile("result.json")
    .WithSqliteScheduler("crawl/state.db")        // resume is a query, not a position file
    .TrackVisitedLinksInSqlite("crawl/state.db")  // the table is the set
    .BuildAsync();
```

Pass `dataCleanupOnStart: true` to any sink, tracker, or scheduler method to wipe its store at start (note: `WriteToJsonFile` defaults this to `true`; the others default to `false`).

### Authorization

If the site needs cookies, call `SetCookies` and fill the container. You perform the login yourself.

```csharp
using System.Net;
using WebReaper.Builders;

await ScraperEngineBuilder
    .Crawl("https://example.com/protected")
    .Extract(new() { new("name", "h1") })
    .SetCookies(cookies =>
    {
        cookies.Add(new Cookie("AuthToken", "123"));
    })
    .BuildAsync();
```

### Distributed and serverless

Swap the scheduler, config storage, and link tracker to Redis or Azure Service Bus; multiple workers or serverless functions share one crawl. [`Examples/WebReaper.AzureFuncs`](Examples/WebReaper.AzureFuncs) shows the serverless shape (two functions: `StartScraping` seeds the work, `WebReaperSpider` is the distributed Crawl driver). [`Examples/WebReaper.DistributedScraperWorkerService`](Examples/WebReaper.DistributedScraperWorkerService) shows the worker-service shape.

`DistributedSpiderBuilder.BuildSpider()` returns a bare `ISpider` without a Crawl seed (ADR-0009 / ADR-0025: "two seams, not one bug" split). The worker's config is persisted separately by the start endpoint.

### Storage and scheduler backends

Every backend is a swappable seam. In-memory is the default; file-backed lives in core; the rest come from satellites.

| Seam | Core (in-memory default + file) | Satellite options |
|---|---|---|
| Scheduler | in-memory, `WithTextFileScheduler` | `WithSqliteScheduler`, `WithRedisScheduler`, `WithAzureServiceBusScheduler` |
| Visited-link tracker | in-memory, `TrackVisitedLinksInFile` | `TrackVisitedLinksInSqlite`, `TrackVisitedLinksInRedis` |
| Config storage | in-memory, `WithFileConfigStorage` | `WithMongoDbConfigStorage`, `WithRedisConfigStorage` |
| Cookie storage | in-memory, `WithFileCookieStorage` | `WithMongoDbCookieStorage`, `WithRedisCookieStorage` |
| Agent run store | in-memory, file (ADR-0051) | `WithSqliteAgentRunStore`, `WithRedisAgentRunStore`, `WithMongoAgentRunStore`, `WithCosmosAgentRunStore` |
| Result sink | `WriteToConsole`, `WriteToCsvFile`, `WriteToJsonFile` | `WriteToMongoDb`, `WriteToRedis`, `WriteToCosmosDb` |
| Page loader transport | HTTP (default) | `WithPlaywrightPageLoader`, `WithCdpPageLoader`, `WithCloakBrowser` |

Custom sinks, the full interface index, and the main domain entities live in [`docs/architecture.md`](docs/architecture.md).

## Repository structure

| Project | Description |
|---|---|
| `WebReaper` | The core library (the `WebReaper` NuGet package). |
| `WebReaper.Cdp` | Raw CDP transport satellite (ADR-0052). |
| `WebReaper.Playwright` | Microsoft.Playwright transport satellite (ADR-0053). |
| `WebReaper.Stealth.CloakBrowser` | First stealth-backend satellite (ADR-0054). |
| `WebReaper.AI` | LLM extraction, action resolver, agent brain, self-healing, schema inferrer. |
| `WebReaper.Extraction.Attributes` | `[ScrapeSchema]` / `[ScrapeField]` marker types (ADR-0045). |
| `WebReaper.Extraction.Generators` | Roslyn source generator (ADR-0045). |
| `WebReaper.Mcp` | MCP server satellite (ADR-0049). |
| `WebReaper.Mongo` | MongoDB sink plus config / cookie storage. |
| `WebReaper.Redis` | Redis scheduler, tracker, sink, config / cookie storage. |
| `WebReaper.AzureServiceBus` | Azure Service Bus distributed scheduler. |
| `WebReaper.Cosmos` | Azure Cosmos DB sink. |
| `WebReaper.Sqlite` | Local durable scheduler and visited-link tracker over embedded SQLite. |
| `WebReaper.Cli` | AOT single-binary CLI (ADR-0043). |
| `Examples/WebReaper.ConsoleApplication` | Using WebReaper in a console application. |
| `Examples/WebReaper.AiNativeShowcase` | Runnable demos for every AI feature in this README. |
| `Examples/WebReaper.SchemaInferenceShowcase` | Demos for ADR-0067 / 0068 / 0069 schema inference. |
| `Examples/WebReaper.ScraperWorkerService` | Using WebReaper in a .NET Worker Service. |
| `Examples/WebReaper.DistributedScraperWorkerService` | Distributed crawl across workers sharing crawl state. |
| `Examples/WebReaper.AzureFuncs` | Serverless crawl with Azure Functions plus Azure Service Bus. |
| `Examples/BrownsfashionScraper` | A real-world e-commerce scraper example. |
| `Misc/WebReaper.ProxyProviders` | Example proxy-provider implementations. |

## License

WebReaper is MIT-licensed (ADR-0017). All NuGet packages plus the `WebReaper.Cli` binary ship under the same terms. Use it commercially, embed it in proprietary software, fork it, modify it, redistribute it; the only ask is that you keep the copyright notice.

Prior to the 10.0.0 wave, WebReaper was GPL-3.0-or-later. The relicense is strictly more permissive: every existing user is unaffected; new users who couldn't embed under GPL now can. Historical contributors are credited in [`CONTRIBUTORS.md`](CONTRIBUTORS.md). See [`docs/adr/0017-relicense-gpl-mit.md`](docs/adr/0017-relicense-gpl-mit.md) for the analysis and contributor consent path.

Contributions are welcome under the same MIT terms; sign-off via DCO ([`CONTRIBUTING.md`](CONTRIBUTING.md)).
