# WebReaper: verified facts for all concept sites

Source of truth: repo README.md, docs/architecture.md, docs/AI-ONBOARDING.md, CHANGELOG.md, `webreaper --help`.
Every claim below is verified against those files as of 2026-07-10 (v11.3.x). Do NOT add claims not listed here.

## Identity

- **WebReaper**: AI-native web scraper. Free, MIT-licensed (was GPL before 10.0.0).
- Three surfaces: **AOT single-binary CLI** (~12 MB, no runtime), **.NET library** (`WebReaper` NuGet package, net10.0), **MCP servers** (stdio + Streamable HTTP).
- Built by HighCraft.io. Site: webreaper.ai. Repo: github.com/alex-on-ai/WebReaper. Current version: 11.3.x.
- 15 NuGet packages (1 core + 14 satellites), versioned in lockstep.
- Mascot: hooded reaper with a scythe. Metaphor: reaping/harvesting the web.
- No account, no API key, no credit meter, no hosted dependency. Everything runs locally. Only key ever needed: your own LLM key, only for optional `--prompt` / `--infer` modes.

## Install (exact commands)

```bash
brew install alex-on-ai/webreaper/webreaper                                        # macOS/Linux
curl -fsSL https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/scripts/install.sh | sh   # any POSIX shell
dotnet add package WebReaper                                                       # .NET library
```

- Windows: binaries on GitHub Releases (win-x64 / win-arm64), put webreaper.exe on %PATH%.
- Six RIDs per release: linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64, win-arm64.
- macOS binaries Apple-codesigned and notarized: no Gatekeeper warnings.
- Update: `brew upgrade webreaper`; CLI prints a one-line upgrade hint on stderr when newer release exists (TTY only; disable WEBREAPER_NO_UPDATE_CHECK=1).

## CLI surface (v11.3.x)

Commands: `scrape <url>`, `crawl <url>`, `map <url>`, `init`, `browser install|path|list`, `stealth install|path|list`, `version`, `help`.

```bash
webreaper scrape https://example.com                          # one page → Markdown (stdout)
webreaper scrape https://example.com --output page.md
webreaper map https://example.com --search /blog/ --max-urls 50
webreaper crawl https://example.com > pages.jsonl             # whole site, recursive, JSON Lines
webreaper scrape https://example.com --schema schema.json     # structured JSON via CSS-selector schema
webreaper scrape https://example.com --prompt "title and author" --model gpt-4o-mini --llm-url https://api.openai.com/v1
webreaper crawl https://example.com --infer "product name and price" --model gpt-4o-mini --llm-url https://api.openai.com/v1 --output-dir ./out
webreaper scrape https://example.com --browser                # JS-rendered SPA
webreaper scrape https://example.com --stealth                # start at stealth tier
webreaper init                                                # writes Claude Code skill
```

Flags: `--schema`, `--output`, `--output-dir`, `--max-age <30s|5m|2h|1d>`, `--browser`, `--browser-cdp-url`, `--follow <selector>`, `--stealth`, `--auto-stealth`, `--no-auto-stealth`, `--search`, `--max-urls`, `--allow-offsite`, `--no-sitemap`, `--prompt`, `--infer`, `--model`, `--llm-url`, `--open`, `--yes`.
LLM key env: `WEBREAPER_LLM_API_KEY` / `OPENAI_API_KEY` (never a flag). `--prompt` / `--infer` / `--schema` are mutually exclusive.
Data → stdout, diagnostics → stderr. Blocked-at-top-tier pages are dropped and the run exits non-zero.

## Bot protection (ADR-0083): headline feature

- Automatic per-page climb: **HTTP → browser (Chromium) → stealth (CloakBrowser)**.
- Climbs only when a page actually looks blocked (challenge status 403/429/503, header, or body marker: Cloudflare/DataDome/PerimeterX/Incapsula/Akamai).
- **Host-sticky floor**: first confirmed block on a host lifts that host's floor; rest of crawl starts at the working tier. Pay for the climb once, not per page.
- Challenge pages are **dropped, never returned as data**; run exits non-zero so unattended jobs know.
- `--stealth` starts at stealth; `--auto-stealth` unattended; `--no-auto-stealth` caps at vanilla browser. Stealth backend ~220 MB, downloads only on opt-in.
- Self-hosted, no cloud round-trip.

## Library API (canonical snippets)

```csharp
using WebReaper.Builders;

var engine = await ScraperEngineBuilder
    .Crawl("https://news.ycombinator.com")
    .AsMarkdown()
    .WriteToConsole()
    .BuildAsync();

await engine.RunAsync();
```

Seed terminals (a build MUST start with Crawl/CrawlWithBrowser and pick one): `.Extract(schema)` | `.AsMarkdown()` | `.ExtractInferred(goal?)` | `.ExtractWithPrompt(chatClient, instruction)`.

Schema extraction + crawling:
```csharp
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
```

Source-gen schema (ADR-0045; class must be partial):
```csharp
using WebReaper.Extraction.Attributes;

[ScrapeSchema]
public partial class Article
{
    [ScrapeField("h1")]                                     public string? Title { get; set; }
    [ScrapeField(".views", Type = SchemaFieldType.Integer)] public int Views { get; set; }
    [ScrapeField(".tag", IsList = true)]                    public List<string> Tags { get; set; } = new();
}
// Emits at compile time: static Schema Schema; static Article Materialize(JsonObject)
```

AI safety net (WebReaper.AI satellite, any IChatClient via Microsoft.Extensions.AI):
```csharp
.WithLlmFallback(chatClient)          // LLM fires only when a field returns empty (ADR-0046)
.WithLlmSelfHealing(chatClient)       // repair broken selector once, cache forever (ADR-0047)
.UseAi(chatClient, AiPolicyMode.Inferred)  // no schema: infer once, re-infer on validator failure
```

Autonomous agent (ADR-0051):
```csharp
var result = await LlmAgent.RunAsync(
    "https://example.com",
    goal: "Find the contact email and phone number for the support team.",
    chatClient);
```
AgentEngine: sequential decide → persist → execute loop over closed-sum AgentDecision (Extract | Follow | Act | Stop). Durable resume across restarts (IAgentRunStore: InMemory/File core; Sqlite/Redis/Mongo/Cosmos satellites).

Browser + actions:
```csharp
await ScraperEngineBuilder
    .CrawlWithBrowser("https://example.com", actions => actions
        .ScrollToEnd()
        .Do(PageAction.SemanticAct("click 'sign in'"))   // natural language, LLM-resolved once, cached
        .Do(PageAction.WaitForNetworkIdle())
        .Build())
    .Extract(new() { new("title", "h1") })
    .WithPlaywrightPageLoader()      // or .WithCdpPageLoader(...) / .WithCloakBrowser()
    .BuildAsync();
```
10 PageAction arms: Click, Wait, Fill, Press, ScrollToEnd, ScrollIntoView, WaitForSelector, WaitForNetworkIdle, EvaluateExpression, SemanticAct. Fill/Press use React-friendly native-setter trick.

In-process collection: `.Subscribe(records.Add)` with ConcurrentBag (sinks fan out concurrently).
Cookies: `.SetCookies(c => c.Add(new Cookie("AuthToken", "123")))`.
Durable local resume: `.WithSqliteScheduler("crawl/state.db")` + `.TrackVisitedLinksInSqlite("crawl/state.db")`.
Engine is IAsyncDisposable: `await using var engine = await builder.BuildAsync();`

## Distributed

Swap scheduler + config storage + link tracker to Redis or Azure Service Bus → multiple workers/serverless functions share one crawl. `DistributedSpiderBuilder.BuildSpider()` returns bare ISpider for workers. Examples: WebReaper.AzureFuncs (serverless), WebReaper.DistributedScraperWorkerService.

Seams table (defaults → satellites):
| Seam | Core default | Satellites |
|---|---|---|
| Scheduler | in-memory, file | SQLite, Redis, Azure Service Bus |
| Visited-link tracker | in-memory, file | SQLite, Redis |
| Config storage | in-memory, file | MongoDB, Redis |
| Cookie storage | in-memory, file | MongoDB, Redis |
| Agent run store | in-memory, file | SQLite, Redis, MongoDB, Cosmos |
| Result sink | Console, CSV, JSON-Lines | MongoDB, Redis, Cosmos |
| Page loader | HTTP | Playwright, CDP, CloakBrowser stealth |

Public interfaces (implement to extend): IScheduler, IVisitedLinkTracker, IPageLoader, IPageLoadTransport, IContentExtractor, ISchemaBackend<TNode>, IScraperSink, ICrawlStep, ISpider, IOutstandingWorkLatch, IAgentBrain, IAgentRunStore, IActionResolver, ISelectorRepairer, ISchemaInferrer, ISchemaValidator, IPageProcessor, IRetryPolicy.

Closed sums: CrawlOutcome (Parsed | Followed | Paginated), AgentDecision (Extract | Follow | Act | Stop), AgentDecisionOutcome (6 arms), PageAction (10 arms).

## Agent integrations

- `webreaper init` → writes SKILL.md to `.claude/skills/webreaper/`; next Claude Code session routes scraping intents to the CLI.
- MCP stdio: **WebReaper.Mcp** (Cursor, Claude Desktop, Copilot Studio). MCP HTTP: **WebReaper.Mcp.AspNetCore** (n8n; bearer token). Both expose 6 tools: `scrape`, `map`, `extract`, `extract_with_prompt`, `extract_inferred`, `crawl`.
- `docker run -p 8080:8080 -e WEBREAPER_MCP_TOKEN=your-secret ghcr.io/alex-on-ai/webreaper-mcp-http:latest`
- Works inside any shell-spawning agent harness (LangChain ShellTool, GitHub Actions, scripts).
- Agent onboarding one-pager: docs/AI-ONBOARDING.md (raw URL fetchable).

## Packages (15)

WebReaper (core) · WebReaper.Cdp · WebReaper.Playwright · WebReaper.Stealth.CloakBrowser · WebReaper.AI · WebReaper.AI.Http · WebReaper.Extraction.Attributes · WebReaper.Extraction.Generators · WebReaper.Mcp · WebReaper.Mcp.AspNetCore · WebReaper.Mongo · WebReaper.Redis · WebReaper.AzureServiceBus · WebReaper.Cosmos · WebReaper.Sqlite.
(WebReaper.Cli is not a NuGet package; ships as release binaries.)

## Comparison (from README table: safe claims)

- vs **Firecrawl**: same AI-native positioning, opposite distribution. Firecrawl = hosted API, AGPL-3.0 + commercial license, Docker+Postgres+Redis to self-host, their model only. WebReaper = local binary, MIT, BYO any LLM, free.
- vs **Crawl4AI**: Docker + Python + Playwright install; Apache 2.0; agent = code it yourself.
- vs **WebFetch (Claude built-in)**: single fetch only, no crawl, no bot-protection handling.
- vs **Crawlee**: similar library ground, no binary, no Claude Code skill, no LLM safety net.

## Use cases

1. Build LLM context from blog/docs sites (map + scrape → prompt or vector DB).
2. Monitor competitor pricing/status pages (cron + `.WithChangeTracking()` hash dedup).
3. Autonomous research agent (LlmAgent.RunAsync).
4. Scrape Cloudflare-protected catalogs (auto-climb + --stealth).
5. Clean datasets from semi-structured pages ([ScrapeSchema] + AOT).
6. Embed scraping primitive in your own app (MIT, seams).

## Honesty rules for the concept sites

- Demos that replay output must be labeled as recorded/simulated, not live.
- No pricing/cloud claims (waitlist exists on main site but concepts stay OSS-focused).
- No benchmark numbers that aren't in the README (~12 MB binary, ~220 MB stealth download are OK).
- Version strings shown: 11.3.2 (current released).
- The real hero: `webreaper scrape <url>` → Markdown, zero config.
