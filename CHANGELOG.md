# Changelog

## 11.3.0: MCP progress + schema-free extraction (extract_inferred)

An additive release for the MCP servers. No breaking changes, no API removals.

- **Progress over MCP ([ADR-0085]).** The `scrape`, `extract`, `extract_with_prompt`, `crawl`, and `extract_inferred` tools now forward the escalating loader's climb steps to MCP progress notifications. Interactive MCP clients (Claude Desktop, Cursor) render live per-page and per-climb progress during a long call. A blocking client like n8n does not surface these and just waits for the result, so the long-crawl mitigations there remain the `maxPages` cap and the map-then-scrape pattern.
- **New `extract_inferred` MCP tool ([ADR-0067]).** Extract structured data from a URL with no schema: an LLM infers the schema once (optionally steered by a goal), then WebReaper extracts deterministically. Cheaper and more consistent than `extract_with_prompt` across similarly shaped pages. Same OpenAI-compatible LLM config as `extract_with_prompt` (`WEBREAPER_LLM_*`), with an optional per-call `model` override. Both MCP servers (stdio and HTTP) now expose six tools.

All 15 packages ship at 11.3.0 (lockstep).

[ADR-0085]: docs/adr/0085-climb-progress-observer-seam.md
[ADR-0067]: docs/adr/0067-schema-inferrer-seam.md

## 11.2.0: MCP server over HTTP (n8n and remote clients)

An additive release: a new Streamable HTTP MCP server so URL-based clients (n8n, hosted agents) can reach WebReaper, plus a CDP-sidecar option and a bounded crawl tool that both MCP servers gain. No breaking changes, no API removals.

- **New `WebReaper.Mcp.AspNetCore` satellite ([ADR-0086]).** A Streamable HTTP MCP host (stateless mode), the sibling of the stdio `WebReaper.Mcp` server. The stdio server stays the choice for local process-spawning agents (Cursor, Claude Desktop); this one is for clients that connect to a URL, the most prominent being n8n, whose built-in MCP Client node is URL-only and cannot reach a stdio server. It reuses the same tools, adds bearer-token auth (`WEBREAPER_MCP_TOKEN`, mandatory when bound to a non-loopback interface, so an unauthenticated public scraper is structurally refused), and a `/health` endpoint. A Chromium-baked Docker image, a `docker-compose` example, and an n8n quickstart ship with it (`docs/mcp-http-quickstart.md`).
- **`crawl` tool on both MCP servers ([ADR-0086]).** A bounded whole-site sweep, `crawl(url, maxPages, browser)`, returning one Markdown record per page as JSON Lines. The default cap is 50 pages (hard cap 1000) because MCP has no streaming, and the tool description flags it as a single blocking call with no progress feedback, pointing at `map` then per-URL `scrape` for large sites.
- **`WEBREAPER_CDP_URL` browser sidecar ([ADR-0086]).** A `browser=true` MCP call now connects to an external CDP endpoint (a shared Chromium / browserless pool) when `WEBREAPER_CDP_URL` is set, instead of launching managed Chromium; `WEBREAPER_MCP_MAX_CONCURRENT_BROWSERS` caps concurrent launches. The selection lives in the shared satellite, so the stdio server gains the sidecar option too.
- **Per-call model on `extract_with_prompt` ([ADR-0086]).** The tool gains an optional `model` parameter overriding `WEBREAPER_LLM_MODEL` for that one call; the API key stays environment-only, never a tool argument.

The container image publishes to `ghcr.io/pavlovtech/webreaper-mcp-http` (tagged with the version and `latest`) on each release.

All 15 packages ship at 11.2.0 (lockstep).

[ADR-0086]: docs/adr/0086-mcp-streamable-http-transport.md

## 11.1.2: faster browser rung + a climb-progress seam

A small additive release: browser-mode scrapes return sooner, the escalating loader gains an observability seam, and two transport/installer fixes land. No breaking changes, no API removals.

- **Faster browser rung ([ADR-0083] transport tuning).** The raw-CDP transport (`WebReaper.Cdp`) waited up to 30s for the page load event and polled a JS-challenge interstitial for up to 20s. Both were capture *deadlines*, not targets: the DOM is ready long before the load event settles on an ad-heavy page, and a Cloudflare-style challenge that clears does so in the first few seconds. The load wait is now capped at 12s and the interstitial poll at 8s (1s steps); on timeout the current DOM is captured as before, and a page still showing a challenge is a real block the detector flags. Measured on a real climb: a browser-rung success dropped from ~22s to ~9s, and a blocked rung from ~53s to ~40s. Helps every CDP consumer, including the CLI's `--browser` path; nothing about the result changes, only the dead waiting.
- **Climb-progress observer seam ([ADR-0085]).** `EscalatingPageLoader` now reports each step of a climb (attempt, blocked, climbing, succeeded, exhausted) through a new `IClimbObserver` seam, with `NullClimbObserver` as the zero-overhead default. Wire one with `ScraperEngineBuilder.WithClimbObserver(...)` to surface live per-rung progress (the live scrape playground streams it over SSE); a no-op for everyone who does not.
- **CDP launch-and-connect teardown fix ([ADR-0058]).** `.WithCdpPageLoader(CdpLaunchOptions)` now registers its spawned browser for engine teardown, so the launched Chromium is killed on engine disposal instead of leaking on a long-running host. The CLI was unaffected (it exits per scrape); server consumers that build an engine per request were not.
- **`WebReaper.Stealth.CloakBrowser` installer fix.** The binary auto-download now resolves a real CloakBrowser release (the `chromium-vNNN` tag scheme), verifies the asset against the release `SHA256SUMS`, extracts with the BCL (`System.Formats.Tar`, AOT-clean), and fails fast on an unshipped platform (macOS / linux-arm64 / win-x86 are not published).

All 14 packages ship at 11.1.2 (lockstep).

[ADR-0083]: docs/adr/0083-escalating-page-loader.md
[ADR-0085]: docs/adr/0085-climb-progress-observer-seam.md
[ADR-0058]: docs/adr/0058-engine-teardown-disposal.md

## 11.1.1: AI extraction in the MCP server + discoverability

A small additive release on top of v11.1.0's AI extraction.

- **MCP `extract_with_prompt` tool.** `WebReaper.Mcp` gains a schema-free LLM extraction tool: extract structured data from a URL by a natural-language instruction (no CSS schema), returning JSON Lines. Built on the `WebReaper.AI.Http` OpenAI-compatible client. Configure the endpoint via the `WEBREAPER_LLM_MODEL` and `WEBREAPER_LLM_BASE_URL` environment variables, with the key in `WEBREAPER_LLM_API_KEY` (or `OPENAI_API_KEY`).
- **Discoverability.** The bundled Agent Skill (`webreaper init`) and the README now teach the CLI's `--prompt` / `--infer` / `--output-dir` flags, with a guide to choosing between schema, prompt, infer, and Markdown extraction.

All 14 packages ship at 11.1.1 (lockstep).

## 11.1.0: AI extraction in the CLI

`webreaper scrape` and `webreaper crawl` can now extract structured data with an LLM, on the AOT single binary, with your own key. This is the [ADR-0084](docs/adr/0084-ai-extraction-in-the-aot-cli.md) wave. It is additive: no breaking changes, one new package.

What you get:

- **`--prompt "<instruction>"`** runs schema-free, per-page LLM extraction: the model reads each page and returns JSON for whatever you describe ("all C-level execs with name and title"). Robust on heterogeneous pages; one LLM call per page.
- **`--infer ["<goal>"]`** infers a schema once from the first page, then extracts the rest deterministically: about one LLM call for a whole crawl, the cheap path when pages share a shape.
- **Bring your own model, explicitly.** `--model <id>` plus `--llm-url <url>` point at any OpenAI-compatible endpoint (OpenAI, Ollama, OpenRouter, vLLM, and so on). The API key is read from `WEBREAPER_LLM_API_KEY` or `OPENAI_API_KEY` only, never a command-line flag.
- **`--output-dir [dir]`** writes one file per page (`.md` in Markdown mode, `.json` for schema / prompt / infer; default `./webreaper-out`), `--open` reveals the folder, and a hint reports where output landed. The default stays JSON Lines on stdout.
- **`crawl --prompt` warns before a large per-page run** (one LLM call per page); `--yes` or a non-interactive shell skips the prompt.

New package:

- **`WebReaper.AI.Http`**: an AOT-clean, OpenAI-compatible `IChatClient` (`OpenAiCompatibleChatClient`) built on raw `HttpClient` and System.Text.Json source generation, no provider SDK. The bring-your-own chat client the .NET ecosystem otherwise lacks for Native-AOT. JSON-mode chat completions (all the extraction paths need); tool calling throws rather than silently dropping tools.

Under the hood:

- **`WebReaper.AI` is now Native-AOT clean** and baked into the CLI, so the single-binary AOT publish is preserved. Three reflection-JSON sites moved to source generation, gated by two new bake-the-satellite AOT smoke tests in CI. This reverses the ADR-0009 quarantine for the satellite's own code; the quarantine of concrete provider SDKs still holds (your `IChatClient` makes the call).
- **`PromptContentExtractor`** is the new schema-free `IContentExtractor` strategy behind `--prompt`, reachable in code as the fourth `ICrawlSeed` terminal `.ExtractWithPrompt(chatClient, instruction)`.

All 14 packages ship at 11.1.0 (lockstep), now including `WebReaper.AI.Http`.

## 11.0.0: block-aware escalating page loader

WebReaper now detects bot-check challenges and climbs through stronger transports automatically, per page, instead of silently harvesting challenge pages. This is the [ADR-0083](docs/adr/0083-escalating-page-loader.md) wave (5 slices), and it is a breaking major because it changes the core page-loader contract.

What you get:

- **Automatic browser fallback for blocked sites.** A page that loads fine over HTTP is returned as-is; a page that looks blocked (a challenge-class status, a challenge response header, or a challenge body marker) is re-loaded over the next stronger transport. The ladder is HTTP, then a headless browser, then a stealth browser, and the climb happens inside a single load call, so the crawl driver, scheduler, and visited-link tracker are untouched. Both `scrape` and `crawl` get it.
- **Host-sticky climbing.** Bot protection is near-always site-wide, so once one page on a host is confirmed blocked at a tier, later same-host pages start higher rather than re-pay the failed lower tier. The floor lifts only on a high-confidence block (status or header), never on a weak body marker, so one false-positive page cannot promote a whole legitimate host.
- **Challenge pages are dropped, not emitted.** A page still blocked at the top tier is suppressed (no page-processor run, no sink fan-out) and tallied on the run report, so a blocked scrape yields zero records plus a clear signal, never challenge-page garbage in your store.
- **The library can report an HTTP status and response headers for the first time** (`PageLoadResult`), and a `403` no longer stack-traces out of a run.
- **CLI `--stealth` finally does something.** It starts the climb at the stealth rung (it was parsed but never consulted before). `--browser` starts at the browser rung; with no flag a scrape starts at HTTP and auto-climbs. Stealth-rung inclusion is decided once at startup (`--auto-stealth` / `WEBREAPER_AUTO_STEALTH` / interactive `Y/n`; `--no-auto-stealth` caps the climb at the browser rung).

Breaking changes:

- `IPageLoader.LoadAsync` and `IPageLoadTransport.LoadAsync` return `Task<PageLoadResult>` (body plus `HttpStatus` and `Headers`), not `Task<string>`. Every transport and transport satellite returns `PageLoadResult`.
- A completed response of any status, including 4xx/5xx, is **data**, not an exception. Only a genuine no-response failure (DNS, connection refused, TLS, timeout) throws, now a typed `PageLoadException`. The retry policy therefore retries transport faults only, never an HTTP status (transient `5xx`/`429` are no longer auto-retried on the HTTP path; the correct response to a challenge code is to climb).
- `RunReport.BlockedPageCount` counts residual-blocked pages the run suppressed (dropped), and the CLI exits non-zero when any page stayed blocked.
- `ScraperEngineBuilder.WithLoadTransport` appends an ordered Dynamic rung instead of replacing a single slot (single-call wiring is unchanged).

Supersedes [ADR-0056](docs/adr/0056-cli-bot-check-escalation.md) (the dead CLI bot-check escalation is removed); amends [ADR-0004](docs/adr/0004-one-page-loader-transport-seam.md).

| Area | Change |
|---|---|
| `WebReaper.Core.Loaders` | NEW `PageLoadResult`, `PageLoadException`, `PageLoadTier`, `EscalatingPageLoader`, `HostTierFloor`; `IPageLoader` / `IPageLoadTransport` return `PageLoadResult`. |
| `WebReaper.Core.Blocking` | NEW `IBlockDetector` + `BlockDetector`, `BlockVerdict` / `BlockConfidence`, and the pure `BlockDropPolicy` confidence-split drop rule. |
| `WebReaper.Core` (driver) | The crawl driver runs the detector verdict through the drop policy before the post-extraction pipeline and tallies suppressed pages on `RunReport`. |
| `WebReaper.Cli` | `--browser` / `--stealth` select the entry rung; `--stealth` fixed; the dead `BotCheckDetector` escalation removed; the `EmptyResultAdvisor` zero-record hint kept. |
| satellites | `WebReaper.Cdp` / `WebReaper.Playwright` / proxy variants return `PageLoadResult`. |

Semver: major. The loader / transport return-type change is a breaking change to a core seam and to the transport-satellite contract.

## 10.3.0: self-update notifier

`scrape` / `crawl` / `map` now tell you when a newer release exists:

```
webreaper 10.4.0 is available (you have 10.3.0).
Upgrade: brew upgrade webreaper
(disable this check: WEBREAPER_NO_UPDATE_CHECK=1)
```

[ADR-0082](docs/adr/0082-self-update.md) Part 1. The notifier is deliberately unobtrusive and respects however you installed the CLI:

- **Opt-out, interactive only.** It prints a single line to **stderr** (never stdout, so piped JSON Lines / Markdown stays clean), and only on an interactive terminal: never in a pipe, never under CI, and never when `WEBREAPER_NO_UPDATE_CHECK=1` (or `NO_UPDATE_NOTIFIER`) is set. Agents and scripts see nothing.
- **Read-only, no telemetry.** A single `GET` to GitHub's public `releases/latest`, once per 24h (cached in `~/.webreaper/update-check.json`); no machine ID, usage, or analytics payload, so there is no first-run consent gate (the `gh` / npm model). It never blocks the command, never changes its exit code, and swallows every failure (offline, rate-limited).
- **Channel-aware hint.** Homebrew installs are told `brew upgrade webreaper`, winget / Scoop get their equivalents, and a `curl | sh` install is pointed at the install.sh `--upgrade` one-liner.

A self-replacing `webreaper update` command is deferred (ADR-0082 Part 2): Homebrew and winget already own their upgrade path, so it only benefits `curl | sh` users, and it carries package-manager-desync, Windows, and permissions complexity that the notifier does not.

| File | Change |
|---|---|
| `WebReaper.Cli/UpdateNotifier.cs` | NEW. The notifier: pure gating / version-compare / throttle / hint helpers plus a thin, best-effort orchestration (24h cache, `releases/latest` fetch with a User-Agent and a ~1.5s timeout). AOT-clean (HttpClient + JsonDocument, no reflection). |
| `WebReaper.Cli/Program.cs` | Fire the notifier after a successful `scrape` / `crawl` / `map`. |
| `WebReaper.Cli/Help.cs`, `WebReaper.Cli/skill/SKILL.md`, `README.md` | Document the check and the `WEBREAPER_NO_UPDATE_CHECK` opt-out. |

Semver: additive. The new opt-out stderr line is the only visible behaviour change for interactive users.

## 10.2.0: whole-site crawl (Site sweep)

One command now crawls an entire site:

```bash
webreaper crawl https://example.com > pages.jsonl
```

and the library equivalent:

```csharp
await ScraperEngineBuilder
    .Crawl("https://example.com")
    .AsMarkdown()            // or .Extract(schema)
    .Sweep()
    .StopWhenAllLinksProcessed()
    .WriteToConsole()
    .BuildAsync();
```

Before this, covering a whole site meant `map` piped into a shell loop of `scrape` calls, which silently missed deep pages on sitemap-less sites (the Site mapper is one HTTP pass, not a crawl). [ADR-0081](docs/adr/0081-site-sweep-whole-site-crawl.md) closes that gap with the **Site sweep**: a recursive, on-domain crawl that extracts every page as it follows every link, terminating when the frontier saturates. It is Firecrawl `crawl` parity, built as the small local extension [ADR-0001](docs/adr/0001-crawl-outcome-closed-sum.md) and [ADR-0030](docs/adr/0030-link-path-selector-construction-guards.md) anticipated, not a new engine.

The model: a **Sweep page** is the first **Page category** that BOTH extracts and follows. A recursive `LinkPathSelector.Sweep(...)` selector marks it; the **Crawl step** returns a new fourth **Crawl outcome** arm, `Swept` (this page's `ParsedData` plus its on-domain child Jobs); children **retain** the sweep selector so the traversal perpetuates; the existing **Visited-link tracker** dedups and terminates it, and the **Stop rule** page-limit cutoff bounds it. The whole termination, latch, and Sink fan-out machinery is reused unchanged.

- **On-domain by default, dependency-free.** The sweep follows only links whose host equals the start host, treating a leading `www.` as the apex. `--include-subdomains` widens to a suffix match on the apex host (a documented heuristic, not public-suffix-list-correct; the eTLD+1 dependency was rejected to keep core dependency-light).
- **Sitemap seeding, default-on.** The frontier is seeded from the Site mapper's discovered URLs too, so a site with a sitemap but sparse internal linking is still covered; the two discovery modes union through the same Visited-link tracker. `--no-sitemap` opts out.
- **Bounds.** `--max-pages` (default 1000) maps onto the existing Stop-rule cutoff; `--max-depth` (default unlimited) reads the Job's parent-backlink-chain length.
- **Output is JSON Lines, streamed** to the Sinks as each page is extracted. Markdown `{ title, markdown }` per page by default; `--schema` applies the deterministic Schema fold instead.

The recursive marker round-trips through the selector-chain JSON codec, so durable and distributed crawls resume a Sweep page as a sweep. The distributed-driver example (`Examples/WebReaper.AzureFuncs`) gains the `Swept` case (emit AND follow), and a `CrawlOutcome` arm census test guards every consumer against a future fifth arm.

| File | Change |
|---|---|
| `WebReaper/Domain/Selectors/LinkPathSelector.cs` | New init-only `Recursive` marker and a `Sweep(selector = "a[href]", …)` named factory (the third intent-shape beside `Follow` / `Paginate`). |
| `WebReaper/Core/Crawling/CrawlOutcome.cs` | New `Swept(ParsedData Data, ImmutableArray<Job> Next)` arm + `Sweep(...)` factory; `NextJobs` surfaces the swept children. |
| `WebReaper/Core/Crawling/Concrete/CrawlStep.cs` | The Sweep branch: a recursive head selector extracts the page AND produces on-domain children retaining the selector, with a depth gate. |
| `WebReaper/Core/Crawling/Concrete/SweepDomainFilter.cs` | NEW. The on-domain boundary (host + `www`, apex-suffix with `--include-subdomains`); the leading-dot guard rejects `notexample.com` / `example.com.evil.com`. |
| `WebReaper/Core/Crawling/Concrete/SweepPolicy.cs` | NEW. The crawl-global anchor-host + subdomains + depth policy threaded into the step. |
| `WebReaper/Core/ScraperEngine.cs` | The driver's `Swept` case: run the record through the Post-extraction pipeline (emit) AND enqueue the children (follow). The only arm that does both. |
| `WebReaper/Serialization/Converters/ImmutableQueueJsonConverters.cs` | Round-trip the `recursive` marker (written only when set, so pre-10.2 JSON is unchanged). |
| `WebReaper/Domain/ScraperConfig.cs` | New `IncludeSubdomains` + `MaxDepth` knobs. |
| `WebReaper/Builders/SweepOptions.cs` | NEW. The `.Sweep(options?)` options record. |
| `WebReaper/Builders/{ConfigBuilder,ScraperEngineBuilder,SpiderBuilder}.cs` | `.Sweep(options?)`; `SpiderBuilder` derives the `SweepPolicy` from the config (covers both Crawl drivers); `BuildAsync` seeds the frontier from the Site mapper when sitemap is on. |
| `WebReaper.Cli/Commands/CrawlCommand.cs` | NEW `crawl` verb. Flags `--schema` / `--output` / `--max-pages` / `--max-depth` / `--include-subdomains` / `--no-sitemap`. HTTP-only, AOT-clean. |
| `WebReaper.Cli/{SchemaFile,RecordOutput}.cs` | NEW. Schema-loading + JSON-lines emission extracted from `ScrapeCommand` and shared with `crawl`. |
| `WebReaper.Cli/{Program,Help}.cs`, `WebReaper.Cli/skill/SKILL.md` | Dispatch, help text, and the bundled Agent Skill document the `crawl` verb. |

Semver: additive (a new CLI verb, a new library method, a new selector factory, a new closed-sum arm). No existing arm or method changes shape.

This release also folds in two fixes merged since 10.1.0:

- **CLI accepts bare-host URLs** ([#162](https://github.com/pavlovtech/WebReaper/pull/162)): `webreaper scrape example.com` now defaults a missing scheme to `https://` at the argument boundary, instead of failing with "An invalid request URI was provided".
- **Build-warnings cleanup** ([#161](https://github.com/pavlovtech/WebReaper/pull/161)): the xUnit1031, Node20-artifact, retention-days, and NU5128 warnings are cleared.

## 10.1.0: form-interaction page actions, MCP browser mode, and post-launch refactors

### Three new `PageAction` arms: `Fill`, `Press`, `ScrollIntoView` (ADR-0074)

Closes the form-interaction gap [ADR-0035](docs/adr/0035-pageaction-closed-sum.md) left on the seven-arm closed sum: no text input, no keyboard events, no element-into-view scroll. Today consumers needing those reach for `EvaluateExpression` with a hand-rolled JS payload (silently breaks React / Vue / Svelte controlled components because the framework's `_valueTracker` bypasses changes that don't go through the native property setter) or `SemanticAct` (the LLM resolver has no concrete `Fill`-shape to return). Both are wrong by construction.

[ADR-0074](docs/adr/0074-pageaction-form-interaction-primitives.md) widens the closed sum from seven arms to ten:

| Arm | Shape | Maps to |
|---|---|---|
| `PageAction.Fill(string Selector, string Value)` | Auto-wait 30 s + clear + focus + native-setter trick + `input` / `change` events | `page.FillAsync(sel, val)` (Playwright) / `Input.insertText` + event dispatch via JS (CDP) |
| `PageAction.Press(string Key)` | Playwright-style key strings (`"Enter"`, `"Control+A"`, single chars) on the currently-focused element | `page.Keyboard.PressAsync(key)` (Playwright) / `Input.dispatchKeyEvent` with a static `key-string → CDP-fields` map (CDP) |
| `PageAction.ScrollIntoView(string Selector)` | Auto-wait 30 s + `element.scrollIntoView()` | `page.Locator(sel).ScrollIntoViewIfNeededAsync()` (Playwright) / `Runtime.evaluate` (CDP, reusing ADR-0057's `WaitForSelectorAsync` helper) |

Achieves vocabulary parity with Firecrawl's `write` / `press` / `scroll` actions while keeping the structural differentiators (`WaitForNetworkIdle`, `SemanticAct`) WebReaper carries that Firecrawl does not. The `Sequence(arm1, arm2, …)` composite arm needed for multi-step `SemanticAct` resolution stays deferred to v2 (three open questions, no real caller; see ADR §Considered options (h)); v1 keeps `SemanticAct → single arm`.

The `LlmActionResolver` whitelist extends from four shapes to seven; the brain registry grows 10 → 13 tools and the resolver registry 6 → 9. Fork 8 of ADR-0060 (`ActSemanticAct` absent from the resolver's tool list) is preserved; no SemanticAct loop is representable, structurally.

| File | Change |
|---|---|
| `WebReaper/Domain/PageActions/PageAction.cs` | Three new nested `sealed record` arms (`Fill`, `Press`, `ScrollIntoView`). Class doc updated: "seven arms" → "ten arms"; implicit-30s-auto-wait noted on `Fill` and `ScrollIntoView`. `ScrollIntoView` doc notes it is distinct from `ScrollToEnd`. |
| `WebReaper/Builders/PageActionBuilder.cs` | Three new fluent methods (`Fill`, `Press`, `ScrollIntoView`) with `ArgumentException.ThrowIfNullOrWhiteSpace` validation on selector + key arguments. `Fill` accepts an empty `value` (clears the field). |
| `WebReaper/Serialization/Converters/PageActionJsonConverter.cs` | Three new write/read arm cases. Wire tags `"fill"` / `"press"` / `"scrollIntoView"`. Pre-v10.1 readers throw `JsonException` with the unknown arm tag; closed-sum-default-arm posture preserved. |
| `WebReaper.Cdp/CdpKeyMapper.cs` | NEW file. Pure static deep module mapping Playwright-style key strings to the four CDP `Input.dispatchKeyEvent` fields (`key`, `code`, `windowsVirtualKeyCode`, `modifiers` bitmask). ~80 entries: printable chars, named keys, function keys F1-F12, modifier-prefixed combos. Unknown key throws `ArgumentException`. |
| `WebReaper.Cdp/CdpPageActionDispatcher.cs` | Three new dispatch arms. `Fill` calls `WaitForSelectorAsync` then evaluates a `Runtime.evaluate` payload running the React-friendly native-setter trick + `dispatchEvent(input/change)` (`BuildFillScript` helper). `Press` dispatches via `CdpKeyMapper.Map` + two `Input.dispatchKeyEvent` calls (`keyDown` then `keyUp`). `ScrollIntoView` reuses the same poll helper before `Runtime.evaluate` of `scrollIntoView()`. |
| `WebReaper.Playwright/PlaywrightPageLoadTransport.cs` | Three new dispatch arms, one line each (`page.FillAsync`, `page.Keyboard.PressAsync`, `page.Locator(sel).ScrollIntoViewIfNeededAsync`). |
| `WebReaper.AI/Tools/PageActionTools.cs` | Three new nested static classes following PR #134's arm-local pattern: `PageActionTools.Fill`, `.Press`, `.ScrollIntoView` each with `Name` const + `Descriptor` JSON Schema + `FromArguments`. |
| `WebReaper.AI/Tools/AgentDecisionTools.cs` | `ForBrain()` grows 10 → 13 tools (adds `Press`, `ScrollIntoView`, `Fill`); `ForResolver()` grows 6 → 9 tools (same). |
| `WebReaper.AI/LlmActionResolver.cs` | `ParseActionTool` switch gains one case per new arm. The system prompt does NOT enumerate JSON shapes; the tool list is the schema (ADR-0060), so the new arms surface via their tools, not prompt text (pinned by `System_prompt_no_longer_enumerates_JSON_shapes`). |
| `WebReaper.AI/LlmAgentBrain.cs` | `ParseDecisionTool` gains one case per new arm. |
| `WebReaper.Tests/WebReaper.UnitTests/StjSerializationTests.cs` | Three new arm round-trip tests pinning typed-field equality through the codec. |
| `WebReaper.Tests/WebReaper.UnitTests/PayloadShellTests.cs` | Three new `ScraperConfig` payload-shell round-trip tests; the ScrollIntoView test additionally covers chain-nested `LinkPathSelector.PageActions`. |
| `WebReaper.Tests/WebReaper.AotSmokeTest/Program.cs` | Three new `PageAction` arm round-trip checks added to the closed-sum smoke exercise; smoke pass count grows from 11 → 14. |

### `WebReaper.Mcp` browser mode wired (ADR-0073)

Patch fix to a behaviour gap noted but deferred in [PR #122](https://github.com/pavlovtech/WebReaper/pull/122). The MCP satellite's `scrape` and `extract` tools accept a `browser=true` parameter that flips the seed to `ScraperEngineBuilder.CrawlWithBrowser(url)`, but `WebReaper.Mcp.csproj` did not `ProjectReference` any browser-transport satellite, so the first dynamic page load hit the core's `BrowserNotConfiguredPageLoadTransport` error. The parameter was structurally unwired.

[ADR-0073](docs/adr/0073-mcp-browser-transport-policy.md) records the wiring decision: `WebReaper.Mcp` bakes `WebReaper.Cdp` (ADR-0052), mirroring the CLI's ADR-0055 precedent. `Microsoft.Playwright` and `WebReaper.Stealth.*` stay out of the satellite's dependency graph; consistency across both agent-facing satellites carries the convention.

| File | Change |
|---|---|
| `WebReaper.Mcp/WebReaper.Mcp.csproj` | Added `<ProjectReference Include="..\WebReaper.Cdp\WebReaper.Cdp.csproj" />`. |
| `WebReaper.Mcp/WebReaperTools.cs` | `Scrape` and `Extract` now conditionally call `.WithCdpPageLoader(new CdpLaunchOptions())` when `browser=true`. Switched to `await using` for the engine so the spawned Chromium process tears down on each call (ADR-0058 chain). Tool descriptions updated to mention the auto-spawn behaviour. |
| `WebReaper.Mcp/README.md` | New "Browser mode" section documenting the auto-spawn path and prerequisite (a system Chrome / Chromium / Edge on the MCP host). |

The `map` tool needs no change (sitemap discovery is static, no browser load). The `WebReaper.Mcp` NuGet package gains `WebReaper.Cdp` as a transitive dependency at the v10.1.0 bump; both packages move in lockstep on the WebReaper release cadence.

### `WebReaper.AI` new public type `LlmToolArguments` (ADR-0059 amendment)

The byte-identical `TryGetString` / `TryGetInt` JSON-argument extractors that lived as private static methods in both `LlmActionResolver` and `LlmAgentBrain` move to a shared public static class `WebReaper.AI.Llm.LlmToolArguments`. Sibling to `LlmCall<TResponse>` on the same "one canonical mechanism, not five copies" axis the original ADR defines. Consumer-authored tool-calling `Llm*` adapters reuse the helpers for consistent leniency rules instead of re-implementing.

| Public type added | Notes |
|---|---|
| `WebReaper.AI.Llm.LlmToolArguments` | Static. Two methods: `TryGetString(JsonElement, string) → string?` and `TryGetInt(JsonElement, string) → int?`. Both return `null` for missing properties, JSON-null, or non-matching kinds. `TryGetInt` tolerates string-encoded integers (`"30000"` → `30_000`) but the JSON integer-token-vs-decimal-token boundary is strict (`1` → `1`, `1.0` → `null`); the leniency contract is pinned by `LlmToolArgumentsTests`. |

No behaviour changes: the helpers are byte-identical to the now-deleted private copies; the existing brain and resolver tests continue to pass unchanged.

### `WebReaper.AI` off the preview `Microsoft.Extensions.AI` abstractions

`WebReaper.AI` moves its `Microsoft.Extensions.AI.Abstractions` floor from the preview `9.4.0-preview.1.25207.5` to stable `10.3.0`. No code changes; the satellite compiles and its tests pass against the GA abstractions. `10.3.0` is the highest version where the latest stable `Anthropic.SDK` (5.10.0) stays binary-compatible (`HostedMcpServerTool.AuthorizationToken` changed at `10.4.0`), and `Microsoft.Extensions.AI.OpenAI 10.3.0` targets the same abstractions, so both stock providers coexist on one version. Consumers' minimum `Microsoft.Extensions.AI.Abstractions` rises off preview accordingly.

| File | Change |
|---|---|
| `WebReaper.AI/WebReaper.AI.csproj` | `Microsoft.Extensions.AI.Abstractions` `9.4.0-preview.1.25207.5` → `10.3.0` (stable; this is a dependency floor, not a pin). |

### Architecture-deepening refactors (ADR-0076, ADR-0077, ADR-0078)

Post-launch internal refactors from the 2026-05-29 architecture pass. The only public-surface change is `AgentEngine` gaining `IAsyncDisposable`; one consumer-visible bug fix ships with it.

| Change | Notes |
|---|---|
| **`PostExtractionPipeline` module (ADR-0076)** | The page-processor pipeline, sink fan-out, and their warm-up / disposal move into one `WebReaper/Processing/PostExtractionPipeline.cs` with a fused `ProcessAndEmitAsync`. Both the crawl driver and the agent engine delegate to it instead of duplicating the loop. |
| **Bug fix: durable agent-run records were silently dropped** | `AgentEngine` was neither warmed nor disposed, so durable agent-run sinks and the `IAgentRunStore` were never flushed (records lost on agent runs). `AgentEngine` is now `IAsyncDisposable`, and `Agent.RunAsync` / `Agent.ResumeAsync` use `await using`. |
| **`ClosedSumCodec<T>` JSON mechanism (ADR-0077)** | The three near-identical hand-written flat closed-sum converters (`PageAction`, `AgentDecision`, `AgentDecisionOutcome`) collapse onto one `WebReaper/Serialization/Codecs/ClosedSumCodec.cs` driven by per-arm `(tag, write, build)` descriptors. Wire format stays byte-identical (pinned by characterization tests); AOT-clean. |
| **Derived AI tool registries (ADR-0078)** | The brain (13) and resolver (9) tool registries plus their parse dispatch become derived views of one `PageActionTools.Arms` list, so the offered tool and the parse that decodes it cannot drift. Fork 8 (no `SemanticAct` on the resolver) is preserved structurally. Both browser transports gain a dispatch execution-coverage test (CDP via a fake session, Playwright via an NSubstitute `IPage`). |

### NuGet metadata polish (no code changes)

Every NuGet package now displays a logo and README on its package page; em-dashes removed from `<Description>` / `<PackageReleaseNotes>` across all 13 csprojs. No code changes; no public-surface changes.

#### Per-package metadata fixes

| Package | Change |
|---|---|
| `WebReaper.AI` | Added `<PackageReadmeFile>README.md</PackageReadmeFile>` + new README explaining the LLM-extractor / fallback / self-heal / inferrer / agent-brain / action-resolver / `.UseAi(...)` surface. |
| `WebReaper.Extraction.Attributes` | Added `<PackageIcon>` + `<PackageReadmeFile>` + new README explaining the `[ScrapeSchema]` / `[ScrapeField]` marker attributes. |
| `WebReaper.Extraction.Generators` | Added `<PackageIcon>` + `<PackageReadmeFile>` + new README explaining the Roslyn source generator's emitted `Schema` / `Materialize` surface and v1 scope. |
| `WebReaper.Mcp` | Added `<PackageIcon>` + `<PackageReadmeFile>` (the README was already in the repo, just not packaged). |
| All other satellites | Em-dashes replaced with appropriate punctuation in `<Description>` / `<PackageReleaseNotes>` so the rendered NuGet text reads cleanly. |

#### Repo-wide hygiene

- Em-dashes removed from `scripts/install.sh` header (21 instances), `homebrew/webreaper.rb.template` (9 instances), `docs/architecture.md` (6 instances), and the satellite `README.md` files (14 instances total). Per the project's "no em-dashes" discipline; pattern reads as AI-generated and was flagged during the v10.0.0 launch.
- Historical CHANGELOG entries (`## 10.0.0` and earlier) intentionally untouched.

## 10.0.0 — AI-native funnel + semantic actions + transports wave, on a deepened architecture; MIT relicense (breaking)

The headline release of the year — 30 ADRs (0025–0055, with ADR-0017 the
parallel licence move) — splits into four arcs. The first is the *staged
builder* that closes the last runtime construction trap (ADR-0025). The
second is *architecture deepening* — two review waves (ADR-0026..0031 and
ADR-0032..0039) that turn the in-process Crawl driver into a small,
audit-clean composition over named seams. The third is the *AI-native wave*
(ADR-0040..0051): a no-schema Markdown terminal, a CLI + Agent Skill, an LLM
extractor satellite, a `[ScrapeSchema]` source generator, a deterministic→LLM
extraction router, a self-healing extractor, a change-tracking processor, an
MCP server satellite, semantic page actions, and an autonomous agent driver
— all bolted onto the seams the architecture deepening exposed. The fourth
is the *transports wave* (ADR-0052..0055): the `WebReaper.Puppeteer` satellite
is deleted; in its place ship `WebReaper.Playwright` (Microsoft.Playwright SDK,
modern default), `WebReaper.Cdp` (raw CDP, AOT-clean, bedrock for stealth
backends), `WebReaper.Stealth.CloakBrowser` (first per-backend stealth fork
adapter), and a documented CLI browser/stealth acquisition policy.
**In parallel, the project is relicensed GPL-3.0-or-later → MIT** (ADR-0017)
to remove the funnel's last adoption-friction edge for downstream consumers
and SaaS integrators. *Every* breaking change is in this release; the
post-10.0.0 cadence returns to additive minor releases on the seams
introduced here.

### Transports wave: WebReaper.Puppeteer deleted; Playwright + CDP + stealth (ADR-0052..0055)

The `WebReaper.Puppeteer` satellite — ADR-0009's named successor target since
2026 — is deleted in this release. In its place ship two browser transports
(both addressing the singleton Dynamic-slot of ADR-0004's `IPageLoader`), a
per-backend stealth-Chromium-fork pattern, and a documented CLI policy that
preserves the ADR-0043 AOT guarantee end-to-end.

`WebReaper.Cdp` (ADR-0052) is the bedrock: a raw Chrome DevTools Protocol
transport built on `System.Net.WebSockets` + `System.Text.Json` source-gen —
AOT-clean, no PuppeteerSharp / Microsoft.Playwright dependency. Two builder
overloads (`.WithCdpPageLoader(cdpUrl)` connect-to-existing for BYO browsers,
`.WithCdpPageLoader(CdpLaunchOptions)` launch-and-connect for managed
Chromium). Public `CdpLaunchHelpers` utility (PATH search, free-port spawn,
CDP-connect-validate, teardown) — the shared layer every `WebReaper.Stealth.*`
satellite composes on. All seven `PageAction` arms (ADR-0035) including
`SemanticAct` (ADR-0050).

`WebReaper.Playwright` (ADR-0053) is the modern SDK-shaped transport:
multi-browser (Chromium default; Firefox/WebKit opt-in via `PlaywrightBrowser`
enum); `.WithPlaywrightPageLoader(browser?, opts?)`; all seven `PageAction`
arms (closes the ADR-0004 §"Out of scope" four-arm Puppeteer gap that left
`WaitForSelector` and `EvaluateExpression` throwing at runtime). First-run
browser provisioning via the standard `playwright install` step.

`WebReaper.Stealth.CloakBrowser` (ADR-0054) is the first concrete satellite of
the per-backend stealth-fork pattern. Convention (not interface) — each
fork-specific satellite ships an installer + launcher pair composing on
`CdpLaunchHelpers` and exposing one `WithXBackend()` extension. CloakBrowser
solves invisible bot-checks (Cloudflare Turnstile, reCAPTCHA v3, FingerprintJS)
via C++ source-level fingerprint patches. Install model = `playwright install`
/ `winget` (download from upstream on first use; no redistribution; license
acknowledgment via logger). Composes naturally with `IProxyProvider` for the
hardest sites (DataDome + residential proxies).

`WebReaper.Cli` (ADR-0055) bakes ONLY `WebReaper.Cdp` for browser support —
Microsoft.Playwright and `WebReaper.Stealth.*` are never baked, both
AOT-hostile inside the CLI's single-binary publish. Layered auto-spawn (BYO
`--browser-cdp-url` → system Chrome detection → managed Chromium from
`webreaper browser install`). New subcommands: `webreaper browser install` +
`webreaper stealth install` (interactive picker over the curated
`KnownStealthBackends` static registry; `--yes` for unattended CI).

#### Breaking changes

- **`WebReaper.Puppeteer` satellite deleted.** Consumers using
  `.WithPuppeteerPageLoader()` migrate to `.WithPlaywrightPageLoader()` (one
  `using` swap + one method-name swap). The `WebReaper.Puppeteer.Tests`
  package is also deleted. Examples and integration tests are migrated in
  lockstep. No `[Obsolete]` deprecation window — clean cut, matches the
  ADR-0009 precedent exactly.
- **`BrowserNotConfiguredPageLoadTransport` error message updated** to name
  both new satellites (`WebReaper.Playwright` and `WebReaper.Cdp`) instead of
  the deleted `WebReaper.Puppeteer`. Consumers running on Dynamic pages
  without a registered browser transport see the new message.
- **CLAUDE.md transport table updated**: `page loaders` row flips from
  `Http + Puppeteer` to `Http + Playwright + Cdp`.

#### Migration

Common case (one-line per file):
```diff
- using WebReaper.Puppeteer;
+ using WebReaper.Playwright;
  …
- .WithPuppeteerPageLoader()
+ .WithPlaywrightPageLoader()
```

For stealth scenarios (Cloudflare-blocked sites, etc.) — new in this release:
```csharp
using WebReaper.Stealth.CloakBrowser;
…
.WithCloakBrowser()
```

For CLI users: `webreaper scrape ... --browser` now auto-spawns a system
Chrome / Chromium / Edge via `WebReaper.Cdp`. Install a managed Chromium with
`webreaper browser install` if none is on PATH. For BYO browsers:
`--browser-cdp-url http://localhost:9222`.


### A scrape begins with a Crawl seed (ADR-0025)

The builder front door no longer has a runtime trap. Start URLs and a schema
were a *runtime* `InvalidOperationException` from `ConfigBuilder.Build()` if you
forgot them, documented only by a `CLAUDE.md` gotcha — the exact
runtime/implicit failure the project's signature move (ADR-0001, ADR-0022)
exists to make structurally impossible. ADR-0025 makes it so: a scrape now
begins with a **Crawl seed**. `ScraperEngineBuilder.Crawl(urls)` /
`.CrawlWithBrowser(urls)` are *static* and return `ICrawlSeed`; its only member,
`.Extract(schema)`, returns the configurable `ScraperEngineBuilder`. That
builder's constructor is `internal`, so the build terminals are unreachable
without a seed and a schema — "build with no start URLs or no schema" has no
representation to construct, not a throw to hit. Rationale, the grilled
alternatives (type-state phantom generics; the multi-param factory; R-narrow),
the trilemma surfaced at implementation and the two-seam resolution:
[`docs/adr/0025-staged-builder-entry.md`](docs/adr/0025-staged-builder-entry.md).

#### Breaking changes

- **The entry point moved.** `new ScraperEngineBuilder()` (public ctor) +
  `.Get(...)` / `.GetWithBrowser(...)` + `.Parse(...)` are replaced by the
  static `ScraperEngineBuilder.Crawl(...)` / `.CrawlWithBrowser(...)` →
  `ICrawlSeed.Extract(schema)` → the builder. `ScraperEngineBuilder`'s
  constructor is `internal` (test-only via `InternalsVisibleTo`); `Get`,
  `GetWithBrowser`, `Parse` are gone.
- **`ConfigBuilder` is `internal`.** Its sole external use — the distributed
  start endpoint's `new ConfigBuilder()….Build()` — is absorbed by the seed's
  gated `ScraperEngineBuilder.Build()` terminal
  (`Crawl(...).Extract(...).Build()` → `ScraperConfig`). The two
  `ConfigBuilder.Build()` throws and the `CLAUDE.md` builder-order gotcha are
  deleted by construction.
- **The distributed-worker reduced shell is its own type.**
  `new ScraperEngineBuilder()….BuildSpider()` becomes
  `new DistributedSpiderBuilder()….BuildSpider()` (ADR-0009). It has a public
  constructor and no `BuildAsync` — so the structural guarantee is absolute and
  the worker stays seedless ("two seams, not one bug"). `BuildSpider()` is
  removed from `ScraperEngineBuilder`; the worker no longer wires the
  driver-owned visited-link tracker (ADR-0022).
- **Zero satellite ripple.** Every `this ScraperEngineBuilder` satellite
  extension is unchanged and works after `.Extract(...)`. Distributed workers
  wire shared adapters by direct construction (public satellite concretes), as
  the canonical `AzureFuncs` example already did.

#### Migration

`new ScraperEngineBuilder().Get(url)….Parse(schema)….BuildAsync()` →
`ScraperEngineBuilder.Crawl(url).Extract(schema)….BuildAsync()` — move the
schema up to `.Extract`, right after the seed; everything else chains unchanged,
in order, after it. `.GetWithBrowser` → `.CrawlWithBrowser`. The distributed
start endpoint's `new ConfigBuilder()….Build()` →
`ScraperEngineBuilder.Crawl(...).Extract(...)….Build()`. The distributed
worker's `new ScraperEngineBuilder()….BuildSpider()` →
`new DistributedSpiderBuilder()….BuildSpider()` (drop the now-unneeded
`.WithLinkTracker(...)` — the driver owns it, ADR-0022). No behavioural change;
the guardrail (whole-solution build, 94 unit + 27 satellite tests, Native-AOT
smoke) is green.

### Architecture deepening — wave 1 (ADR-0026 – ADR-0031)

A wave of internal-architecture deepening landed on `master` after ADR-0025,
from a fresh architecture review. Each change is internal-only or
behaviourally-additive except for the narrow breaking edges called out below;
full design, considered options and rejected alternatives are in each linked
ADR.

- **Retry around the per-Job Spider call is a named seam; Polly leaves core
  (ADR-0026).** The `internal static Infra.Executor` Polly pass-through becomes
  `IRetryPolicy.ExecuteAsync<T>`, a documented seam; the core default
  `FixedAttemptsRetryPolicy` is hand-rolled (four attempts, no delay), so the
  `Polly` package leaves the core dependency graph. A custom policy — e.g. a
  Polly resilience pipeline with exponential backoff — wires in through
  `ScraperEngineBuilder.WithRetryPolicy(...)`. A latent bug is fixed:
  `OperationCanceledException` is no longer retried, so cancellation propagates
  promptly. Additive, internal-only.
  [`docs/adr/0026-retry-policy-seam.md`](docs/adr/0026-retry-policy-seam.md)

- **Shared raw-extraction helper for the AngleSharp backends (ADR-0027).** The
  attribute / inner-HTML / text dispatch that was copy-pasted across the CSS
  and XPath `ISchemaBackend` implementations moves to one internal
  `AngleSharpRawExtractor`; each backend's `ExtractRaw` shrinks to "apply this
  backend's quirks, then delegate." The `ISchemaBackend<TNode>` seam and the
  ADR-0007 CSS `src`→`title` behavioural difference are unchanged.
  Internal-only.
  [`docs/adr/0027-anglesharp-raw-extractor.md`](docs/adr/0027-anglesharp-raw-extractor.md)

- **Schema construction enforces its grammar at the Add site (ADR-0028).**
  `Schema.Add` rejects an empty `Field`, an empty leaf `Selector`, or an empty
  list-container `Selector` at the add call instead of failing later in the
  fold; `Schema.ListOf(field, selector, …children)` is a new named factory for
  the list-of-objects shape. *Narrowly breaking:* a Schema constructed with one
  of those defects now throws `ArgumentException` at construction rather than
  silently dropping a field or aborting the parse.
  [`docs/adr/0028-schema-construction-guards.md`](docs/adr/0028-schema-construction-guards.md)

- **The Schema fold's coercion-failure policy is pinned, with differentiated
  logs (ADR-0029).** The per-leaf swallow-and-log is now the documented
  contract: a coercion failure (`FormatException` / `OverflowException`) is
  logged with a coercion-specific message naming the target type and field,
  distinct from the catch-all "unexpected error extracting field" message — so
  operators can tell *page had bad data* from *selector is wrong*. Behaviour at
  the contract surface is unchanged (the field is left unset; a noisy page
  never aborts the crawl). Internal-only.
  [`docs/adr/0029-coercion-failure-policy.md`](docs/adr/0029-coercion-failure-policy.md)

- **LinkPathSelector enforces its grammar at construction (ADR-0030).** The
  `LinkPathSelector` primary constructor rejects an empty `Selector`, an empty
  (non-null) `PaginationSelector`, and `PageActions` paired with
  `PageType.Static`; `LinkPathSelector.Follow` / `.Paginate` are new named
  factories for the two intent-shapes. The four `ConfigBuilder` selector-chain
  methods are unchanged in signature. *Narrowly breaking:* a `LinkPathSelector`
  constructed with one of those defects now throws at construction instead of
  failing late at the crawl.
  [`docs/adr/0030-link-path-selector-construction-guards.md`](docs/adr/0030-link-path-selector-construction-guards.md)

- **ParsedData's construction owns the URL-merge (ADR-0031).** The page URL is
  folded into `ParsedData.Data` under `"url"` at construction, so every sink
  writes `Data` as-is and none re-merges the URL — console output now includes
  the URL, which it previously omitted. The Crawl driver hands each sink its
  own deep-clone of `Data`, so concurrent sinks never share a `JsonObject`.
  `ParsedData`'s public shape is unchanged; *one narrow edge:* constructing a
  `ParsedData` mutates the passed `JsonObject` to fold the URL in.
  [`docs/adr/0031-parseddata-url-merge.md`](docs/adr/0031-parseddata-url-merge.md)

### Architecture deepening — wave 2 (ADR-0032 – ADR-0039)

A second review wave following wave-1. The Crawl driver becomes a small
composition over named seams; several latent footguns in durable adapters
fall out by construction; and the extraction surface is renamed honestly,
opening the seat the AI-native wave drops into.

- **The Crawl driver's stop rule becomes a module; the latch's credit protocol collapses to one atomic op (ADR-0032).** *"Is the Crawl over, and why?"* lands in a new internal `StopRule` that composes the **Outstanding-work latch** and the page limit behind one verdict; `IOutstandingWorkLatch` loses `AddAsync` and `SignalProcessedAsync` takes a child count, so the two-call credit-ordering footgun is gone — the Redis latch does one round-trip, not two. *Breaking:* a Tier-1 public seam re-signatures; `RedisOutstandingWorkLatch` is updated, custom latch adapters must adopt the new shape.
  [`docs/adr/0032-stop-rule-module.md`](docs/adr/0032-stop-rule-module.md)

- **Async adapter warm-up becomes an opt-in capability the Crawl driver drives (ADR-0033).** A new public `IAsyncInitializable` (one idempotent `InitializeAsync()`) replaces the ad-hoc `Task Initialization` property; the ten durable adapters get pure constructors backed by `Lazy<Task>`, and the driver warms scheduler + tracker + every sink uniformly before the loop — sinks are warmed where they used to self-guard on first emit. *Breaking:* `IScheduler` and `IVisitedLinkTracker` lose `Initialization`; durable adapters must implement `IAsyncInitializable`. `IScraperSink` is unchanged.
  [`docs/adr/0033-async-warmup-seam.md`](docs/adr/0033-async-warmup-seam.md)

- **The Spider takes its run-scoped inputs at construction; `IScraperConfigStorage` leaves the shell (ADR-0034).** `Spider`'s constructor becomes `(ICrawlStep, IPageLoader, bool headless, Schema? parsingScheme)` and the per-Job `GetConfigAsync()` round-trip is gone — config storage is purely the Crawl driver's concern now. *Breaking:* `DistributedSpiderBuilder` loses `WithConfigStorage` / `WithFileConfigStorage`; `BuildSpider()` gains a required `ScraperConfig` parameter, making "build a worker with no config" a compile error. `ScraperEngineBuilder.WithConfigStorage` is unchanged.
  [`docs/adr/0034-spider-config-at-construction.md`](docs/adr/0034-spider-config-at-construction.md)

- **`PageAction` becomes a closed sum of typed arms (ADR-0035).** `PageAction` is now an abstract record with six sealed-record arms (`Click`, `Wait`, `ScrollToEnd`, `EvaluateExpression`, `WaitForSelector`, `WaitForNetworkIdle`), each carrying typed fields — the `PageActionType` enum, the `object[] Parameters`, the ~75-line kind-tagging codec and the transport's runtime casts are all gone. `EvaluateExpression` and `WaitForSelector` (publicly advertised but silently unwired) now actually run. *Breaking:* `PageAction` re-shaped, `PageActionType` removed, wire format changes; `PageActionBuilder`'s public signatures are unchanged.
  [`docs/adr/0035-pageaction-closed-sum.md`](docs/adr/0035-pageaction-closed-sum.md)

- **Link extraction collapses to a concrete function; `ILinkParser` is removed (ADR-0036).** The one-adapter, one-caller `ILinkParser` seam becomes a single `internal static LinkExtractor.GetLinksAsync` called directly by `CrawlStep`. A latent mid-crawl crash on `<a>` elements without `href` is fixed in the rewrite — hrefless anchors are skipped, not `ArgumentNullException`. *Breaking:* `ILinkParser` and `LinkParserByCssSelector` removed; no fluent-path migration exists (there was never a `WithLinkParser`).
  [`docs/adr/0036-link-extraction-not-a-seam.md`](docs/adr/0036-link-extraction-not-a-seam.md)

- **Stop ceases the Crawl driver's consumption; `IScheduler.Complete()` is removed (ADR-0037).** Termination is now a consumer-side cancel of `GetAllAsync`'s token — uniform across every scheduler. Previously `Complete()` was a no-op on `FileScheduler` / `RedisScheduler` / `SqliteScheduler` / `AzureServiceBusScheduler`, so a durable scheduler with `StopWhenAllLinksProcessed()` ran forever; that footgun is gone. `GetAllAsync`'s token contract is now stated explicitly. *Breaking:* `IScheduler.Complete()` removed; in-flight Jobs abort on a cutoff instead of draining.
  [`docs/adr/0037-stop-ceases-consumption.md`](docs/adr/0037-stop-ceases-consumption.md)

- **The post-extraction surface becomes two seams: a page-processor pipeline and the Sink (ADR-0038).** A new `IPageProcessor` / `PageContext` / `PageVerdict` (`Kept | Dropped`) surface in `WebReaper.Processing` runs ordered over each extracted page before the sink fan-out — enrich, observe, filter, or replace the record, with the raw HTML and `Schema` in hand. `Subscribe` keeps its signature but is now sugar over an internal `DelegateSink` (closing the ADR-0031 shared-record leak in passing). *Breaking:* `ScraperEngineBuilder.PostProcess` and the public `Metadata` type are removed; move a `PostProcess` callback to `.Process(...)`.
  [`docs/adr/0038-page-processor-seam.md`](docs/adr/0038-page-processor-seam.md)

- **`IJsonContentParser` becomes `IContentExtractor`; the three `*ContentParser` shells collapse onto `SchemaFold` (ADR-0039).** The seam is renamed honestly (the "Json" qualifier was a 6.0.0 fossil), `SchemaContentParser<TNode>` becomes the public `SchemaFold<TNode>`, and the pass-through `AngleSharpContentParser` / `XPathContentParser` / `JsonContentParser` shells are deleted — `SpiderBuilder` constructs the fold directly, the same way ADR-0002's custom-backend extension path does. *Breaking:* `IJsonContentParser` → `IContentExtractor`, `ParseToJsonAsync` → `ExtractAsync`, `WithContentParser` → `WithContentExtractor`; `WithJsonContentParser` / `WithXPathContentParser` keep their names.
  [`docs/adr/0039-content-extractor-seam.md`](docs/adr/0039-content-extractor-seam.md)

### AI-native wave (ADR-0040 – ADR-0049)

The strategic move of the release. The architecture deepening exposed clean
seams for *content extraction* (ADR-0039), *post-extraction processing*
(ADR-0038), and *page loading* (ADR-0004). This wave drops AI-native
features onto those seams: a no-schema Markdown terminal, a CLI, an LLM
extractor satellite, a Roslyn `[ScrapeSchema]` source generator
(Pydantic-parity Python cannot match), a deterministic→LLM router, a
self-healing extractor, change-tracking, and MCP interop. Core stays
dependency-light and AOT-clean; every heavy dependency stays quarantined in
its satellite per ADR-0009.

- **`.AsMarkdown()` — a second `ICrawlSeed` terminal returning LLM-ready Markdown (ADR-0040).** `ICrawlSeed` gains `AsMarkdown()` alongside `Extract(Schema)`; a new `MarkdownContentExtractor` (AngleSharp-driven, AOT-clean, zero new transitive deps) cleans the DOM via a tag-based Readability heuristic and emits `{ title, markdown }`. The schema-required gate becomes a strategy-choice lattice — ADR-0025's structural promise is stated correctly: *a Crawl declares its extraction strategy before `BuildAsync`*. *Breaking:* `ICrawlSeed` gains one method; `IContentExtractor`'s doc widens (the schema requirement is strategy-local).
  [`docs/adr/0040-markdown-extraction-seed-terminal.md`](docs/adr/0040-markdown-extraction-seed-terminal.md)

- **`IPageCache` — a cache-read seam on the page loader, with the firecrawl-shaped `WithMaxAge(TimeSpan)` one-liner (ADR-0041).** A new public `IPageCache` (`TryReadAsync` / `WriteAsync`, keyed on `(url, pageType)`) sits beside `PageLoader` as a cache-aside collaborator; `InMemoryPageCache(TimeSpan maxAge)` ships as the firecrawl-shaped TTL adapter, `NullPageCache` is the no-op default. Enables iterative crawl development without re-fetching and gives the router (ADR-0046) and self-heal (ADR-0047) free re-reads. Additive — no Tier-1 break.
  [`docs/adr/0041-page-cache-seam.md`](docs/adr/0041-page-cache-seam.md)

- **`ISiteMapper` — URL discovery via `sitemap.xml` ∪ root-page links (ADR-0042).** A new public `ISiteMapper` + default `SiteMapper` adapter parses `robots.txt` for `Sitemap:` lines, recurses one level of sitemap-indexes, extracts root-page `<a href>`s, and returns a deduped ordered URL list — without spending the Crawl/visited-link/page-processor pipeline on a one-HTTP-request operation. `ScraperEngineBuilder.MapAsync(url, options?)` is the one-liner; `MapOptions` exposes `MaxUrls` / `Search` / `AllowOffsite` knobs. Additive.
  [`docs/adr/0042-site-mapper.md`](docs/adr/0042-site-mapper.md)

- **`WebReaper.Cli` — the AOT single-binary primitive agent surface, plus a bundled Agent Skill (ADR-0043).** A new AOT-clean executable (`PublishAot=true`, zero NuGet deps beyond the BCL, hand-rolled ~120-line arg parser) ships `webreaper scrape <url>` (defaults to Markdown, `--schema <path>` switches to typed JSON), `webreaper map <url>`, `webreaper init` (writes an embedded `SKILL.md` into `.claude/skills/webreaper/`), and `webreaper version`. The CLI is the primitive; Skill and MCP are adapters over it. Additive.
  [`docs/adr/0043-cli-and-agent-skill.md`](docs/adr/0043-cli-and-agent-skill.md)

- **`WebReaper.AI` — an LLM-backed `IContentExtractor` satellite bound to `Microsoft.Extensions.AI.Abstractions` (ADR-0044).** New satellite shipping `LlmContentExtractor` (Markdown pre-clean by default, deterministic `Temperature = 0`, `MaxTokens = 4096`), a `Schema` → JSON Schema bridge, and `WithLlmExtractor(IChatClient, LlmExtractorOptions?)`. BYO model — the consumer brings their own `IChatClient`; core stays dependency-light and AOT-clean per ADR-0009 quarantine. Additive.
  [`docs/adr/0044-llm-extractor-satellite.md`](docs/adr/0044-llm-extractor-satellite.md)

- **`[ScrapeSchema]` — a Roslyn source generator emitting `Schema` from attributed POCOs (ADR-0045).** Two new packages — `WebReaper.Extraction.Attributes` (the `[ScrapeSchema]` / `[ScrapeField]` markers + `SchemaFieldType`) and `WebReaper.Extraction.Generators` (an `IIncrementalGenerator`). A `partial` POCO with attributed properties gets a compile-time `static Schema Schema` and a reflection-free `static Materialize(JsonObject)`, both AOT-clean. v1 ships the common case (single-level, primitive fields, `List<T>` of primitives); nested `[ScrapeSchema]` POCOs are explicitly deferred. Additive.
  [`docs/adr/0045-scrape-schema-source-generator.md`](docs/adr/0045-scrape-schema-source-generator.md)

- **`ExtractionRouter` — deterministic-first → fallback composition on the `IContentExtractor` seam (ADR-0046).** A new public `ExtractionRouter(primary, fallback, isValid?, logger?)` is itself an `IContentExtractor` — no seam-of-a-seam. Runs the deterministic fold, validates via `SchemaSatisfiedValidator` (a required leaf is missing iff absent or string-empty / list-empty), falls back to e.g. the LLM extractor only when the cheap path fails. `ScraperEngineBuilder.WithFallbackExtractor` is the sugar; `WebReaper.AI` ships `WithLlmFallback`. Additive.
  [`docs/adr/0046-extraction-router.md`](docs/adr/0046-extraction-router.md)

- **`SelfHealingContentExtractor` — LLM proposes selectors, the fold validates, the schema-cache persists the fix (ADR-0047).** New public `ISelectorRepairer` seam plus a `SelfHealingContentExtractor` wrapper: on a deterministic-fold failure, the repairer proposes new selectors, the fold re-runs with the patched `Schema`, and if it validates, the patch is cached (reference-identity, per-crawl in-memory) so every subsequent page runs deterministic again — no recurring LLM cost. `WebReaper.AI` ships `LlmSelectorRepairer` + `WithLlmSelfHealing`. Additive.
  [`docs/adr/0047-self-healing-selectors.md`](docs/adr/0047-self-healing-selectors.md)

- **`ChangeTrackingProcessor` — snapshot Markdown per URL, emit `change_status` on the page-processor pipeline (ADR-0048).** A new `IPageProcessor` (ADR-0038) hashes each page's Markdown extraction (SHA-256, robust to template noise), looks up the prior hash in a new `IChangeStore` seam (`InMemoryChangeStore` default), and annotates the record with `change_status` (`new` / `same` / `changed`) plus `previous_hash`. `ScraperEngineBuilder.WithChangeTracking(IChangeStore? = null)` is the sugar. Additive; `removed` detection and diff text are deferred.
  [`docs/adr/0048-change-tracking-processor.md`](docs/adr/0048-change-tracking-processor.md)

- **`WebReaper.Mcp` — MCP server satellite exposing scrape/map/extract as MCP tools (ADR-0049).** New Exe satellite over the `ModelContextProtocol` C# SDK with stdio transport, exposing three `[McpServerTool]` methods that wrap the existing library API — for MCP-only clients (Cursor, ChatGPT Desktop, Copilot Studio) that can't reach the CLI. Thin facade; pre-1.0 SDK churn quarantined per ADR-0009. Additive.
  [`docs/adr/0049-mcp-server-satellite.md`](docs/adr/0049-mcp-server-satellite.md)

- **`PageAction.SemanticAct(intent)` — natural-language page actions; LLM resolves once, deterministic thereafter (ADR-0050).** A seventh closed-sum `PageAction` arm carrying an intent string ("click sign in") instead of a CSS selector. New public `IActionResolver` seam + `ScraperEngineBuilder.WithActionResolver(...)`; the `WebReaper.AI` satellite ships `LlmActionResolver` + `WithLlmActionResolver(chatClient)`. The Puppeteer transport resolves the intent on the first dynamic page, dispatches the concrete arm, and caches the resolution per crawl by intent string — every subsequent same-intent page dispatches the cached arm with no LLM call. The cache lives in core (`SemanticActCoordinator`), unit-testable without an `IPage`. Same proposer-validator pattern as the extraction router (ADR-0046) and self-healing extractor (ADR-0047), generalised from extraction to actions — self-heal stops being one feature and becomes a *project-level pattern*. **Narrow breaking edge:** `ScraperEngineBuilder.WithLoadTransport`'s factory delegate widens from 3 to 4 arguments (the fourth is `IActionResolver`); the in-tree `WebReaper.Puppeteer` satellite is updated in lockstep. A `SemanticAct` in the config without a registered resolver logs a warning at `BuildAsync` and throws `SemanticActResolutionException` on the first dispatch.
  [`docs/adr/0050-semantic-page-actions.md`](docs/adr/0050-semantic-page-actions.md)

- **`AgentEngine` + `IAgentBrain` + durable `IAgentRunStore` — autonomous "give me X about this site" loop (ADR-0051).** The fourth proposer-validator dock, applied to *page selection* (after extraction-routing ADR-0046, extraction-self-heal ADR-0047, and semantic actions ADR-0050). A new sibling driver to `ScraperEngine`: `AgentEngine` runs a sequential decide→persist→execute loop over a closed-sum `AgentDecision` (`Extract` / `Follow` / `Act` / `Stop`); the brain picks each step from the bounded `AgentState` view; the engine validates, persists, and dispatches. New public types in core: `AgentDecision`, `AgentState`, `AgentResult`, `AgentRunSnapshot`, `IAgentBrain`, `IAgentRunStore`, `AgentEngine`, `AgentEngineBuilder`, `AgentEngineOptions`, `Agent.RunAsync` / `Agent.ResumeAsync` static sugar. The `WebReaper.AI` satellite ships `LlmAgentBrain` + `WithLlmBrain(chatClient)` + the firecrawl-shaped one-liner `LlmAgent.RunAsync(url, goal, chatClient)`. **Durable agent run state ships in v10** (HITL flip from the original "v2 deferral" verdict — architectural-consistency argument: every other seam is InMemory + durable adapters since 7.x): `InMemoryAgentRunStore` default + `FileAgentRunStore` in core; `RedisAgentRunStore` / `MongoAgentRunStore` / `SqliteAgentRunStore` / `CosmosAgentRunStore` satellites + their `WithXxxAgentRunStore(...)` extensions on `AgentEngineBuilder` in lockstep (AzureServiceBus skipped — queue-shaped, doesn't fit a snapshot store). Persist-before-execute, at-least-once on sink effects, exactly-once on brain decisions — resumable agent runs require idempotent sinks (the ADR-0048 change-tracking processor composes cleanly). The two-seam builder pattern (ADR-0009 / ADR-0025) gets its third instance — `AgentEngineBuilder` beside `ScraperEngineBuilder` and `DistributedSpiderBuilder`. AOT-clean by design; LLM brain quarantined in `WebReaper.AI` per ADR-0009. Hand-written `AgentRunSnapshotCodec` (AOT-safe Utf8JsonWriter / JsonObject.WriteTo) + new public `WebReaperAgentJson` serialization surface.
  [`docs/adr/0051-agent-crawl-driver.md`](docs/adr/0051-agent-crawl-driver.md)

### Licence (ADR-0017)

- **WebReaper relicenses GPL-3.0-or-later → MIT.** Every NuGet package in the
  release ships `<PackageLicenseExpression>MIT</PackageLicenseExpression>`
  (core + every satellite, including the four new AI-native satellites). The
  funnel's last legal-adoption-friction edge for downstream consumers and
  SaaS integrators is gone. Full gates, contributor analysis, and history
  rewrite (Deloitte → personal email on 41 commits) recorded in
  [`docs/adr/0017-relicense-gpl-mit.md`](docs/adr/0017-relicense-gpl-mit.md);
  the rewrite's backup branches `origin/pre-email-rewrite-master` and
  `origin/pre-email-rewrite-ai-native-wave` are deletable once the owner is
  satisfied (~30 days).

### `WebReaper.SchemaInferenceShowcase` example project

Funnel-side companion to [`WebReaper.AiNativeShowcase`](Examples/WebReaper.AiNativeShowcase/)
(PR #101, which covered the original AI-native wave ADR-0040..0049).
Closes the documentation gap for the v10.0.0 schema-inference dock —
three sub-commands, each the minimal viable demo of one new public API:

- **`alacarte`** (ADR-0067) — à la carte registration via
  `.ExtractInferred(goal).WithLlmSchemaInferrer(client)`.
- **`useai`** (ADR-0068) — one-line policy via `.UseAi(client,
  new AiOptions(Policy: AiPolicyMode.Inferred))`.
- **`reinfer`** (ADR-0069) — validator-driven re-inference,
  demonstrated with scripted stubs across three configurations
  (default opt-out auto-heal, strict opt-out, cost cap).

All three sub-commands use a deterministic in-process `StubChatClient`
so the example runs offline; the production swap is any
`Microsoft.Extensions.AI.IChatClient` adapter (OpenAI / Anthropic /
Ollama / Azure AI / …). `alacarte` and `useai` actually crawl
`example.com` end-to-end and print the extracted record.

Also fixes one cref warning on `LearnedSchemaContentExtractor.cs`
introduced by the ADR-0069 implementation (the satellite-side
`LlmSchemaInferrer` type was referenced via `<see cref=>` from core
where it cannot resolve; replaced with `<c>`).

### AI-native completion wave (ADR-0068 + ADR-0069)

Two paired ADRs closing the named v1 deferrals on ADR-0067 (the schema
inference ADR — the third v10.0.0 pre-tag AI-native slice). Together
the pair makes the schema-inference dock genuinely *"first page pays
the LLM, every subsequent page runs the fold — until the fold says
otherwise."* Rides v10.0.0 — the major break (Puppeteer deletion,
ADR-0053) still owns the version; this slice lands additively.

- **ADR-0068 — `AiPolicyMode.Inferred` arm + `.UseAi(...)` auto-wiring
  of the schema inferrer.** Closes ADR-0067 Fork 7 (the v1 explicit-
  wiring deferral). New 5th arm on the closed-sum enum wires
  `WithLlmSchemaInferrer + WithLlmActionResolver` — the inferrer-aware
  version of the firecrawl-shaped triple. Mutually exclusive with
  `Recommended` / `LlmPrimary` / `ExtractionOnly` (those register an
  `IContentExtractor` that would shadow the
  `LearnedSchemaContentExtractor` wrapper). `AiOptions` grows the
  `Inferrer: LlmSchemaInferrerOptions?` per-role field + the
  `ResolveInferrerOptions()` synthesis helper; synthesised inferrer
  options inherit the global `CachePolicy` (typically `Hinted`),
  overriding the satellite à-la-carte `Default`. On the **agent
  builder** `.UseAi(Inferred)` throws `ArgumentOutOfRangeException`
  with an actionable message — the brain proposes its own schemas per
  `AgentDecision.Extract(schema)`; a separate inferrer is structurally
  redundant. Consumer one-liner:

  ```csharp
  var engine = await ScraperEngineBuilder
      .Crawl("https://shop.com/products")
      .ExtractInferred(goal: "product details")
      .UseAi(chatClient, new AiOptions(Policy: AiPolicyMode.Inferred))
      .WriteToConsole()
      .BuildAsync();
  ```

  v1 bounded scope (named in the ADR): no self-heal composition with
  the inferrer (Fork 3 — layering correctness with ADR-0069), no
  smart-`Recommended` auto-detection of the seed terminal (Fork 1 —
  closed-sum discipline), no `WireInferrer: true` flag on `AiOptions`
  (Fork 1 — splits the surface), no agent-side `Inferred` (Fork 5 —
  throw, not no-op), no `Markdown` strategy bundled into the enum.

- **ADR-0069 — Validator-driven re-inference for
  `LearnedSchemaContentExtractor`.** Closes ADR-0067 Fork 9 (the v1
  trust-the-cache deferral). The wrapper consults the builder-
  registered `ISchemaValidator` (ADR-0062 — default
  `SchemaSatisfiedValidator`) on every inner-extractor output; N
  consecutive invalid verdicts drop the cached inferred schema and
  trigger re-inference on the next call. The wrapper becomes the
  fourth consumer site of the validator seam (alongside
  `ExtractionRouter`, `SelfHealingContentExtractor`, and the
  `AgentEngine`). New optional constructor args (`validator`,
  `reInferAfterFailures`, `maxReInferencesPerInstance`); new
  `ReInferencesUsed` public property exposes the count for
  diagnostics; new
  `ScraperEngineBuilder.WithSchemaInferenceTriggers(int, int)` method
  for explicit override. The satellite's `WithLlmSchemaInferrer`
  threads `LlmSchemaInferrerOptions.ReInferAfterFailures` /
  `MaxReInferencesPerInstance` through automatically. **Behavioural
  delta:** default `LlmSchemaInferrerOptions.ReInferAfterFailures` is
  `3` — a wrong first-page inference now auto-heals after three
  consecutive empty extractions instead of silently producing empty
  records for the rest of the crawl. Consumers wanting strict
  ADR-0067 trust-the-cache set
  `LlmSchemaInferrerOptions(ReInferAfterFailures: 0)`. Cost cap
  defaults to `int.MaxValue` (unbounded; consumer's cost guardrail).

  Consumer-facing surfaces:

  ```csharp
  // Opt-out — preserve v10.0.0 ADR-0067 trust-the-cache behaviour:
  .WithLlmSchemaInferrer(chatClient,
      new LlmSchemaInferrerOptions(ReInferAfterFailures: 0))

  // Cap re-inferences for unattended / cron runs:
  .WithLlmSchemaInferrer(chatClient,
      new LlmSchemaInferrerOptions(
          ReInferAfterFailures: 3,
          MaxReInferencesPerInstance: 2))

  // Custom inferrer + custom validator + explicit triggers:
  .ExtractInferred(goal)
  .WithSchemaInferrer(new HeuristicInferrer())
  .WithSchemaValidator(new MyValidator())
  .WithSchemaInferenceTriggers(reInferAfterFailures: 5)
  ```

  v1 bounded scope (named in the ADR): no per-host re-inference
  triggers (multi-host is an ADR-0067 v2 question entirely), no
  per-field counters, no self-heal composition (Fork 7 — same
  failure-mode entanglement argument as ADR-0068 Fork 3), no
  reason-embedded re-inference goals (Fork 6 — speculative), no
  persistent re-inference history across runs, no per-page-count
  or per-time-window cap (absolute total cap only).

`WebReaper.AI.Tests` joined the `InternalsVisibleTo` list on
`WebReaper.csproj` — the satellite tests need the schema-inferrer
test seams (`SchemaInferrerForTests`,
`SchemaInferenceTriggersForTests`, `InferenceMarkerForTests`) on
`ScraperEngineBuilder` to verify the ADR-0068 + 0069 wiring +
threading. Same shape as every other satellite test assembly on the
list.

Tests added across the wave (27 new):

- **`LearnedSchemaReInferenceTests`** (core, 10 tests) — opt-out
  preserves v1; threshold-3 drops cache after 3rd consecutive
  failure; one success between failures resets the counter; cost cap
  honoured; default validator integration (empty string invalid,
  integer 0 valid); 16-parallel-worker race under cap; goal threads
  through to re-inferences; constructor negative-arg rejection;
  `ReInferencesUsed` property tracks count with commit-to-spend
  semantic.
- **`UseAiInferredTests`** (satellite, 10 tests) — scraper builds
  with `Inferred` after `ExtractInferred`; registered inferrer is
  `LlmSchemaInferrer` (not `NullSchemaInferrer`); silently ignored
  when not paired with `ExtractInferred`; the wrong-mode pairing
  (`ExtractInferred + Recommended`) throws the ADR-0067 build-time
  message; per-role `Inferrer` override threads `CachePolicy.Hinted`
  to the descriptor; synthesised inferrer inherits global `Hinted`
  by default; mutually-exclusive with `LlmFallback` (no shadowed
  registration); agent throws actionably; existing `Recommended` on
  agent unchanged; enum has 5 arms total.
- **`LlmSchemaInferrerReInferenceOptionsTests`** (satellite, 7
  tests) — defaults (`ReInferAfterFailures = 3`,
  `MaxReInferencesPerInstance = int.MaxValue`);
  `WithLlmSchemaInferrer` threads defaults through; custom options
  threaded; opt-out via 0; direct builder call overrides satellite
  defaults; negative rejection.

Docs:

- **CONTEXT.md** — section header `ADR-0040..0067` →
  `ADR-0040..0069`; **AI policy mode** entry grew the 5th arm
  `Inferred` description; **Schema validator** Relationships line
  grew the fourth consumer-site mention (the Learned-schema content
  extractor).
- **CLAUDE.md** — section header `ADR-0040..0067` →
  `ADR-0040..0069`; two new gotcha bullets on ADR-0068 (the
  `Inferred` arm + mutually-exclusive constraint + agent throw +
  per-role `Inferrer` override + `CachePolicy` inheritance flip) and
  ADR-0069 (the default behavioural delta + cost cap + the four
  consumer sites of the validator seam).

Guardrails (post-wave):

- `dotnet build WebReaper.sln`: **0 errors**.
- `WebReaper.UnitTests`: **409 / 409** pass (10 new).
- `WebReaper.AI.Tests`: **240 / 240** pass (17 new).
- `WebReaper.Cdp.Tests` (16/16), `WebReaper.Cli.Tests` (57/57),
  `WebReaper.Extraction.Generators.Tests` (7/7) — all green, no
  regressions.
- `dotnet publish WebReaper.AotSmokeTest -c Release`: native code
  generated for `osx-arm64`, no IL-trim warnings (the AI-side
  changes are satellite-only and quarantined per ADR-0009; the
  core-side changes — wrapper constructor extension, builder method,
  validator threading — are reflection-free additive).

### Schema inference (ADR-0067)

The third and final ADR of the v10.0.0 pre-tag AI-native slice. Closes the
firecrawl *"extract structured data without a hand-authored schema"* parity
gap and completes the project-level proposer-validator pattern on the
extraction surface — five docks now: ADR-0046 routing, ADR-0047 selector
repair, ADR-0050 action resolution, ADR-0051 page selection, **ADR-0067
schema generation**. Rides v10.0.0 — the major break (Puppeteer deletion,
ADR-0053) still owns the version; this lands additively.

- **`ISchemaInferrer` seam** in `WebReaper/Core/Parser/Abstract/` —
  given a page's content and an optional natural-language goal
  (`"product details"` / `"job listings"` / …), returns a `Schema`
  the deterministic fold can apply. Sibling to the four existing
  AI-adjacent core seams (`IContentExtractor`, `ISelectorRepairer`,
  `IActionResolver`, `ISchemaValidator`); consumer-authored
  deterministic inferrers (heuristic / cached / per-tenant) implement
  the interface without taking an AI dep.
- **`NullSchemaInferrer` sentinel** + `BuildAsync` enforcement.
  Default registration is the sentinel; `BuildAsync` detects it via
  reference identity when `.ExtractInferred(...)` was called and throws
  `InvalidOperationException` with an actionable message (same pattern
  as `AgentEngineBuilder` on `NullAgentBrain`). Throwing in the
  sentinel's `InferAsync` is the defence-in-depth path for code that
  constructs the wrapper directly with the sentinel.
- **`LearnedSchemaContentExtractor` core wrapper** — implements
  `IContentExtractor` + `IAsyncDisposable`. First `ExtractAsync` call
  invokes the inferrer (once), caches the result, and delegates every
  subsequent call to the inner extractor (default `SchemaFold`) with the
  cached schema. `SemaphoreSlim`-guarded double-checked locking handles
  the `Parallel.ForEachAsync` race; per-instance cache (fresh engine =
  fresh inference; resumable runs on the same engine reuse).
  `InferredSchema` property exposes the cached schema for diagnostics
  / source-gen-emit (v2 deferral).
- **`ICrawlSeed.ExtractInferred(string? goal = null)`** — the third
  strategy terminal, sibling to `.Extract(schema)` and `.AsMarkdown()`.
  Marks the builder; `BuildAsync` resolves the marker by wrapping the
  registered (or default) content extractor with
  `LearnedSchemaContentExtractor` and registering the wrapper as an
  ADR-0058 teardown hook so the `SemaphoreSlim` disposes cleanly.
- **`ScraperEngineBuilder.WithSchemaInferrer(ISchemaInferrer)`** — the
  registration method, sibling to `WithSchemaValidator` (ADR-0062).
- **`LlmSchemaInferrer` satellite adapter** in `WebReaper.AI` — the
  fifth `Llm*` adapter, sharing the ADR-0059 `LlmCall<TResponse>`
  mechanism (one descriptor + one mechanism call; same shape as the
  four existing adapters). Asks the LLM for a flat `field → CSS
  selector` map (Fork 5: single-level only in v1, matches ADR-0045
  source-gen constraint); accepts both the wrapped `{"fields": {...}}`
  shape and the bare field-map shape without a parse retry. Markdown
  pre-clean by default (ADR-0063 primitive — `HtmlToMarkdown.Convert`);
  32 000-char content cap; 1024-token response cap (small JSON
  object). Default `CachePolicy.Default` — single-page inference
  doesn't amortise the ADR-0065 cache-write premium. Telemetry
  attribution via `nameof(LlmSchemaInferrer)`.
- **`LlmSchemaInferrerOptions`** — per-role options record (Model,
  UseMarkdownPreClean, MaxContentChars, MaxResponseTokens,
  Temperature, SystemPrompt, CachePolicy?).
- **`WithLlmSchemaInferrer(IChatClient, options?)`** — the satellite
  one-liner that wires the LLM-backed inferrer through the core seam
  with the shared per-builder `LlmCallTelemetry` handle (ADR-0066).

Consumer-facing one-liner:

```csharp
var engine = await ScraperEngineBuilder
    .Crawl("https://shop.com/products")
    .ExtractInferred(goal: "product details")
    .WithLlmSchemaInferrer(chatClient)
    .WriteToConsole()
    .BuildAsync();
```

First page pays the LLM once; every subsequent page runs the deterministic
fold against the cached schema. The cheapest dock of the proposer-validator
pattern — one LLM call per crawl.

V1 deferrals (named in the ADR): no `.UseAi(...)` auto-wiring of the
inferrer (Fork 7 — conflicts with `WithLlmFallback` semantics; v2 may add
an `AiPolicyMode.Inferred` arm); no schema persistence across runs
(overlaps source-gen-emit story); no source-gen emit of the inferred
schema (log + `InferredSchema` property is the v1 path — consumer pastes
into `[ScrapeSchema]` when ready to lock); no nested schemas; no
structured goal type; no validator-driven re-inference.

Tests added:

- **`LearnedSchemaContentExtractorTests`** (core, 13 tests) — first-call
  invokes / subsequent reuses; `InferredSchema` property lifecycle;
  inner extractor receives the inferred schema (not the passed-in
  argument); 16-parallel-worker semaphore guard pins one inference;
  goal threading; null-goal threading; `NullSchemaInferrer` sentinel
  throw with the actionable message; null-returning-inferrer throw;
  inferrer-throw leaves cache unset; `DisposeAsync` idempotent;
  constructor null-rejection.
- **`ExtractInferredSeedTerminalTests`** (core, 10 tests) — terminal
  returns builder; marker capture (set, goal, null-goal); other
  terminals don't set the marker; `BuildAsync` throws on missing
  inferrer with all four actionable substrings; `BuildAsync` succeeds
  with registered inferrer; `WithSchemaInferrer` records the registered
  instance; default is `NullSchemaInferrer.Instance`;
  `WithSchemaInferrer` rejects null; ignored when a different terminal
  was chosen.
- **`LlmSchemaInferrerTests`** (satellite, 18 tests) — wrapped + bare +
  nested-selector shapes; code-fence stripping; throw on empty fields;
  throw on non-object response; goal threading + null-goal omission;
  Markdown pre-clean default + `UseMarkdownPreClean=false` opt-out;
  `MaxContentChars` truncation; custom system prompt override; telemetry
  attribution + null-telemetry tolerance; options flow into
  `ChatOptions`; `CachePolicy.Default` default (no `cache_control`
  hint); `CachePolicy.Hinted` adds the hint; constructor + document
  null-rejection; user prompt scaffolding contains the requested shape.
- **`WithLlmSchemaInferrerTests`** (satellite, 5 tests) — `BuildAsync`
  succeeds after `ExtractInferred + WithLlmSchemaInferrer`; silently
  ignored when not paired with `ExtractInferred`; null-builder + null-
  chat-client rejection; telemetry hook materialised when sharing the
  builder with another `WithLlm*` extension.

Docs:

- `CONTEXT.md` — section header `ADR-0040..0066` → `ADR-0040..0067`;
  two new entries (**Schema inferrer**, **Learned-schema content
  extractor**); relationship line updated from "four proposer-validator
  docks" to "five docks"; new relationship line for the third
  `ICrawlSeed` terminal.
- `CLAUDE.md` — section header `ADR-0040..0066` → `ADR-0040..0067`;
  build-path summary mentions the three terminals (was two); one new
  gotcha bullet on `.ExtractInferred(...)`.

Guardrails (post-slice):

- `dotnet build WebReaper.sln`: **0 errors**.
- `WebReaper.UnitTests`: **399 / 399** pass (23 new).
- `WebReaper.AI.Tests`: **223 / 223** pass (25 new).
- `dotnet publish WebReaper.AotSmokeTest -c Release`: native code
  generated for `osx-arm64`, no IL-trim warnings.

### v10.x transports cleanup wave (ADR-0056..0058)

The deliberate post-Transports-wave follow-ups named in ADRs 0052..0055 — the
cleanup queue that the original wave shipped with documented gaps. Three new
ADRs land additively; v10.0.0's major break (Puppeteer deletion, ADR-0053)
still owns the version, so this wave rides minor:

- **ADR-0056 — Hybrid C bot-check escalation in `webreaper scrape`.** Pins the
  concrete detector heuristic + Y/n prompt + inline install + single-retry
  composition that ADR-0055 §Hybrid C named but did not detail. Conservative
  detector (`BotCheckDetector`) is a pure function over (httpStatus,
  renderedHtml, recordCount); challenge markers cover Cloudflare / DataDome /
  PerimeterX / Incapsula / Akamai. Subprocess install for substitutability —
  the same `webreaper stealth install cloakbrowser --yes` the user could
  type. New flags: `--stealth` (skip vanilla attempt), `--auto-stealth` (CI;
  also `WEBREAPER_AUTO_STEALTH=1`), `--no-auto-stealth` (escape hatch).
- **ADR-0057 — CDP Network-idle event tracking.** `PageAction.WaitForNetworkIdle`
  on the CDP transport now tracks real `Network.requestWillBeSent` /
  `loadingFinished` / `loadingFailed` events with a 500 ms debounce + 30 s
  total timeout. Retires the v10.0.0 `Task.Delay(500)` placeholder; matches
  Puppeteer's `networkidle0` shape. AOT-clean (`ConcurrentDictionary` +
  `TaskCompletionSource`, no reflection).
- **ADR-0058 — Engine-teardown disposal chain.** `ScraperEngine` becomes
  `IAsyncDisposable` (dual of ADR-0033's `IAsyncInitializable`). Disposal
  walks adapters in reverse warm-up order, then builder-registered hooks in
  LIFO. `ScraperEngineBuilder.OnTeardown(IAsyncDisposable)` is the public
  hook satellites use for builder-time-spawned resources. **Closes the named
  CLAUDE.md gotcha:** `WithCloakBrowser()` no longer leaks the 220 MB stealth
  subprocess until host exit; `await using var engine = await
  builder.BuildAsync()` is the recommended pattern.

Also in this wave:

- **CDP transport unit-test infrastructure.** New `WebReaper.Cdp.Tests`
  project; `ICdpSession` interface extracted (internal) so
  `CdpPageActionDispatcher` (extracted from `CdpPageLoadTransport`) is
  testable against a `FakeCdpSession`. 16 tests cover the seven
  `PageAction` arms + the `NetworkActivity` tracker. Handoff item #4.
- **CLI test coverage of `BrowserCommand` + `KnownStealthBackends`** —
  pin shape so a future PR adding a stealth backend can't ship a partial
  row. Handoff item #7.
- **CI smoke widened.** The AOT-published CLI is now exercised against
  `version` + `help` + `browser list` + `stealth list` — every new
  command's bundled-graph path survives the publish. Handoff item #6.
- **Gated CloakBrowser end-to-end integration test.** `CloakBrowserSmokeTests`
  runs the real installer + launcher + a scrape + engine disposal when
  `WEBREAPER_STEALTH_SMOKE=1`. Vacuously passes when unset (CI stays
  hermetic). Handoff item #5.

### AI-native deepening campaign (ADR-0059..0064)

Post-AI-native-wave architecture deepening surfaced by the post-wave review.
Six interlocking ADRs that take the friction (four LLM adapters
reimplementing the same mechanism; brain + resolver hand-parsing JSON
discriminators; agent brain blind to its own decisions' outcomes; validator
hardcoded as a static; Markdown adapter shadowing the primitive; five
`WithLlm*` calls per builder) and ship the deep-modules-and-real-seams
version of the satellite. **All six ride v10.x — the major break is the
v10.0.0 Puppeteer-deletion arc above.**

- **ADR-0059 — `LlmCall<TResponse>` mechanism module.** The four
  `WebReaper.AI` LLM adapters (`LlmContentExtractor`, `LlmAgentBrain`,
  `LlmActionResolver`, `LlmSelectorRepairer`) share one mechanism module
  under `WebReaper.AI/Llm/`. Owns: prompt marshalling, `IChatClient`
  transport, code-fence stripping, the bounded one-shot parse-retry, tool-
  call dispatch (the 0060 seam), `ChatResponse.Usage` capture. Each adapter
  shrinks to ~40-60 lines of policy (system prompt + user-message builder +
  parser + optional tool list). Consumer-authored AI adapters reuse the
  same module for consistency.
- **ADR-0060 — Tool-calling on `LlmAgentBrain` + `LlmActionResolver`.**
  Microsoft.Extensions.AI's `AIFunction` + `ChatOptions.Tools`. The brain's
  10-tool registry IS the `AgentDecision` closed sum (Extract / Follow /
  Stop + 7 flat `Act*` arms); the resolver's 6-tool registry IS the
  concrete `PageAction` arms (no `ActSemanticAct` ever — structurally
  prevents the resolver from looping). JSON-mode parsing for these two
  adapters is **gone**; chat clients without tool-calling are unsupported.
  Hand-rolled JSON Schemas (`HandRolledAIFunction`) keep AOT clean.
- **ADR-0061 — `AgentDecisionOutcome` on `AgentState.LastOutcome`.** Closes
  the brain's feedback gap. New six-arm closed sum (`None` / `Extracted` /
  `Followed` / `ActDispatched` / `Failed` / `Stopped`). Engine populates
  from the prior step's execution. **Behaviour change:** page-load failures
  stop being terminal — they surface as `Failed("load: …")`; the loop
  continues; the brain re-decides. `AgentRunSnapshot` v2 (the new field) is
  backward-compatible on read — pre-0061 snapshots deserialise with
  `LastOutcome = None`.
- **ADR-0062 — `ISchemaValidator` seam.** Promotes
  `SchemaSatisfiedValidator` from a static helper to a public seam.
  `ExtractionRouter` + `SelfHealingContentExtractor` consume it; the agent
  driver consults it after every Extract decision and surfaces failed
  verdicts as `Failed("validation: <reason>")` in `LastOutcome`.
  `ISelectorRepairer.RepairAsync` widens with `string? failureReason` so
  the repairer's prompt sees what the validator flagged. The
  proposer-validator pattern's missing half ships as a real seam.
  **Breaking:** `ExtractionRouter`'s constructor `Func<JsonObject, Schema?,
  bool>?` parameter is replaced by `ISchemaValidator?`; `.WithFallbackExtractor`
  loses its `Func` parameter (sugar callers unaffected; consumers who
  passed a custom predicate migrate to `.WithSchemaValidator(...) +
  .WithFallbackExtractor(fallback)`).
- **ADR-0063 — `HtmlToMarkdown` primitive.** Public pure-function in
  `WebReaper.Core.Markdown`. `MarkdownContentExtractor` becomes a thin
  shell (the `AsMarkdown()` seed terminal still works); `LlmContentExtractor`
  pre-clean, `LlmSelectorRepairer` pre-clean, `AgentEngine`'s
  `CurrentPageMarkdown`, and `ChangeTrackingProcessor`'s hash call the
  primitive directly. Resolves the "Markdown extractor wraps Markdown
  extractor" awkwardness.
- **ADR-0064 — `.UseAi(client, opts?)` aggregator.** One-line AI
  enablement on both `ScraperEngineBuilder` and `AgentEngineBuilder`.
  `AiPolicyMode.Recommended` (default) wires the firecrawl-shaped triple:
  deterministic primary + LLM fallback + LLM selector repair + LLM action
  resolver. `LlmPrimary` swaps the deterministic primary for LLM.
  `ExtractionOnly` drops the resolver. `None` is the escape hatch.
  Per-role options inherit global `Model` / `Temperature` /
  `MaxResponseTokens`; à la carte `WithLlm*` methods remain.
  `AgentEngineBuilder` grows `.WithFallbackExtractor`, `.WithSelfHealing`,
  and `.WithSchemaValidator` (siblings of the scraper-side equivalents) so
  `Recommended` wires symmetrically across both builders — the agent path
  matches the scraper path now.

#### Breaking changes (v10.x only — the v10.0.0 major already owns the version)

- **`ExtractionRouter` constructor**: `Func<JsonObject, Schema?, bool>?
  isValid` parameter replaced by `ISchemaValidator? validator`. Existing
  callers who passed a custom predicate must migrate to
  `.WithSchemaValidator(validator)` + `.WithFallbackExtractor(fallback)`.
- **`SelfHealingContentExtractor` constructor**: gains optional
  `ISchemaValidator?` parameter (additive — nullary default preserves
  behaviour).
- **`ISelectorRepairer.RepairAsync`**: widens with `string? failureReason =
  null` (additive — pre-0062 callers + implementations remain valid).
- **`LlmAgentBrain` + `LlmActionResolver`**: require chat clients whose
  provider supports tool-calling (function calling). Chat clients without
  tool support throw `LlmCallException` on first invocation with an
  actionable message. JSON-mode discrimination is gone for these two.
- **`AgentState`**: gains `LastOutcome` field (additive — default `None`).
- **`AgentRunSnapshot`**: gains `LastOutcome` field (additive — older
  snapshots deserialise with `None`; the codec writes `lastOutcome` only
  when non-default, keeping v1 readers byte-compatible for the common
  case).

#### Validation

- `dotnet build WebReaper.sln`: **0 errors**, 4 pre-existing warnings.
- `WebReaper.UnitTests`: **376 / 376** pass.
- `WebReaper.AI.Tests`: **136 / 136** pass.
- `WebReaper.Cdp.Tests` / `WebReaper.Cli.Tests` / `WebReaper.Sqlite.Tests` /
  `WebReaper.Extraction.Generators.Tests` / `WebReaper.AzureServiceBus.Tests`:
  all green.
- `dotnet publish WebReaper.AotSmokeTest -c Release`: native code generated
  for `osx-arm64`, no IL-trim warnings.

### Cost-optimisation slice (ADR-0065..0066)

The deliberate pre-tag cost-optimisation pass — two paired ADRs that turn
the v10 LLM surface from "works" into "cheap and observable." Both ride
v10.0.0 — the major break (Puppeteer deletion, ADR-0053) still owns the
version; this slice lands additively.

- **ADR-0065 — `LlmCall<TResponse>` system-prompt caching + cached-token
  capture.** `CachePolicy { Default, Hinted }` enum on
  `LlmCallDescriptor.SystemPromptCache`; the mechanism writes the
  Anthropic-standard `cache_control: ephemeral` hint on the system
  `ChatMessage.AdditionalProperties` (M.E.AI 9.4 surface — verified
  against the NuGet cache XML docs) when `Hinted`. Default in
  `AiOptions.CachePolicy` is `Hinted` — the AI-native cheap-default
  ethos: Anthropic users get ~5–10× cheaper system prompts; OpenAI users
  see no change (auto-cache continues regardless); Gemini / local-model
  users see the hint ignored without error. `LlmCallResult` expands from
  4 → 7 positional fields with the cached-vs-uncached split
  (`InputTokens` / `OutputTokens` / `CachedInputTokens` / `TotalTokens`);
  the mechanism's `ReadUsage` helper scans
  `UsageDetails.AdditionalCounts` for the known provider keys
  (`cached_input_tokens` / `cache_read_input_tokens` /
  `InputTokenCount.Cached` / `prompt_tokens_details.cached_tokens`).
  Per-role `LlmExtractorOptions.CachePolicy` /
  `LlmActionResolverOptions.CachePolicy` /
  `LlmAgentBrainOptions.CachePolicy` are nullable (null = inherit from
  `AiOptions.CachePolicy` via the `Resolve*` helpers); à la carte
  adapter construction defaults to `CachePolicy.Default`.
- **ADR-0066 — Engine cost telemetry + `MaxBudgetTokens` enforcement.**
  New `ILlmCallTelemetry` seam + `LlmCallTelemetry` thread-safe
  accumulator (`Interlocked` on aggregates + `ConcurrentDictionary`
  per-adapter; has-value sentinels distinguish "no call surfaced a
  value" from "some calls reported 0"). `LlmCallUsage` per-call record;
  `LlmTelemetrySnapshot` + `LlmAdapterStats` immutable read records
  (per-adapter attribution keyed by ADR-0059's `descriptor.Name`).
  `LlmCall` ctor gains an optional `ILlmCallTelemetry?`; Stopwatch
  wraps `InvokeAsync`; usage reported on every success / failure-after-
  retry exit point before return / throw. The four `Llm*` adapters
  thread the telemetry through their ctors.
  `BuilderTelemetryExtensions` (satellite-internal) —
  `ConditionalWeakTable` per-builder maps to the typed accumulator;
  `WithLlm*` / `.UseAi(...)` retrieve via `GetOrCreateLlmTelemetry`,
  guaranteeing one accumulator per builder shared across all
  registrations. Core gains `WebReaper.Domain.Telemetry` namespace with
  two records: `RunReport(object? Llm, TimeSpan Duration)` — returned
  by `ScraperEngine.RunAsync` (the engine's return type widened
  `Task → Task<RunReport>`; discard semantics keeps
  `await engine.RunAsync(ct)` working — Examples all
  unaffected) and exposed via `AgentResult.Report` (positional shape
  evolves 6 → 7); and `RunTelemetryHooks(Func<object?> Snapshot,
  Action Reset, Func<long?>? TotalLlmTokens = null)` — the
  satellite-clean callback channel into the engine ctors. `RunReport.Llm`
  is `object?` to keep the ADR-0009 satellite quarantine — consumers
  cast to `WebReaper.AI.Llm.LlmTelemetrySnapshot` when the AI satellite
  is in use. Both builders gain a public `TelemetryHooks` property (the
  satellite hook — the ADR-0058 `OnTeardown` pattern).
  `AgentEngineOptions.MaxBudgetTokens` is finally ENFORCED inside the
  agent loop via the hooks' `TotalLlmTokens` getter (was documented-but-
  inert since ADR-0051; grep-confirmed); widened `int? → long?` (token
  counts use `long` headroom). Termination precedence per ADR-0051
  fork 6: `Stop > MaxSteps > MaxBudgetTokens > cancellation`.

Tests added across the slice:

- **`CachePolicyTests`** — cache hint encoding (Default vs Hinted, retry
  path, tool-call mode), descriptor default, split-usage capture
  (Anthropic / OpenAI / generic key recognition via Theory),
  null-AdditionalCounts fallback, TotalTokenCount fallback, retry
  accumulation across both calls.
- **`AiOptionsCachingTests`** — global default `Hinted`; per-role
  nullable `CachePolicy?` default null; `Resolve*` inheritance from
  global when per-role null; explicit per-role override wins; `with`
  clause preserves other per-role fields; à la carte semantics.
- **`LlmCallTelemetryTests`** — empty snapshot, single Record, multi-
  Record sum, per-adapter split, null-token sentinels (no value vs zero),
  ParseRetries + TotalDuration sums, Reset clears + fresh accumulation,
  parallel safety (50 tasks × 100 records), Snapshot immutability.
- **`LlmCallTelemetryWiringTests`** — each of three adapters reports
  under its descriptor name; null-telemetry doesn't throw; two adapters
  sharing one telemetry aggregate globally + split per-adapter.
- **`UseAiTelemetryTests`** — `WithLlmExtractor` / `WithLlmFallback` /
  `WithLlmBrain` / `.UseAi` each set builder `TelemetryHooks`; repeated
  `WithLlm` calls share the same accumulator; `TelemetryHooks.Snapshot`
  returns `LlmTelemetrySnapshot` when cast; `None` policy on scraper
  wires nothing.

Guardrails (post-slice):

- `dotnet build WebReaper.sln`: **0 errors**.
- `WebReaper.UnitTests`: **376 / 376** pass.
- `WebReaper.AI.Tests`: **198 / 198** pass (62 new across the slice).
- `dotnet publish WebReaper.AotSmokeTest -c Release`: native code
  generated for `osx-arm64`, no IL-trim warnings.

## 9.0.0 — The public surface is the documented contract (breaking)

The core public API is now exactly the contract — no wider, no narrower —
documented to the bar the codebase already set, and enforced. ADR-0023 drew
the line with the deletion test (named by a documented consumer / inherited by
a satellite / part of the taught fluent API ⇒ public; reached only through a
builder method and named by nobody ⇒ implementation). The ~552-warning CS1591
backlog the satellite csprojs deliberately kept *visible* is closed: every
Tier-1 type carries real intent-revealing XML doc, every Tier-2 implementation
type is now `internal`, and `<WarningsAsErrors>CS1591</WarningsAsErrors>` makes
the documented surface non-regressing (a new undocumented public member fails
the build). Rationale, the deletion-test line, the rejected alternatives (the
shallow factory; document-everything; the non-breaking 8.1.0/9.0.0 split) and
the staged burndown:
[`docs/adr/0023-core-doc-contract.md`](docs/adr/0023-core-doc-contract.md).

The satellite csprojs' "core keeps CS1591 visible — a live doc backlog"
rationale is updated in place to point here: core CS1591 is now a
contract-enforced zero, not an open backlog.

### Breaking changes

- **The Tier-2 implementation adapters are now `internal`.** The concrete
  types reached only via the fluent builder (or core-internally) are no longer
  public: the `File*` / `InMemory*` storage·scheduler·tracker·blob leaves
  (`FileScheduler`, `FileScraperConfigStorage`, `FileCookieStorage`,
  `FileVisitedLinkedTracker`, `FileBlobStore`, `InMemoryBlobStore`,
  `InMemoryCookieStorage`), the sinks (`ConsoleSink`, `CsvFileSink`,
  `JsonLinesFileSink`, `BufferedFileSink`, `CsvFormat`, `JsonLinesFormat`),
  the parsers/loaders (`AngleSharpContentParser`, `JsonContentParser`,
  `XPathContentParser`, `LinkParserByCssSelector`, `PageLoader`,
  `HttpPageLoadTransport`, `BrowserNotConfiguredPageLoadTransport`), the
  crawl internals (`Spider`, `CrawlStep`, `InMemoryOutstandingWorkLatch`),
  `ValidatedProxyProvider`, `Executor`, `ColorConsoleLogger`, and the
  `LogMethodDuration` / `LogInvocationCount` helpers `Timer` / `Counter`.
- **`ScraperEngine`'s constructor is `internal`.** The class stays public (you
  hold it and call `RunAsync`); only `new ScraperEngine(...)` is gone —
  `ScraperEngineBuilder.BuildAsync()` is the construction contract (the
  `internal SpiderBuilder` / `BuildSpider()` precedent).
- **No shipped package is affected.** A repo-wide sweep confirmed no
  satellite-prod / Example / Misc code names a Tier-2 type; satellites bind
  the Tier-1 interfaces and inherit the Tier-1 payload-shell bases
  (`CookieStore` / `ScraperConfigStore`). `[InternalsVisibleTo]` targets the
  test assemblies only — never a NuGet package.
- **Kept public on purpose:** the fluent builders, every `*/Abstract` seam
  interface, the `Domain` model, `WebReaperJson`, `LoggerExtensions`,
  `ScraperEngine` (the type), the in-memory default adapters the
  distributed-worker pattern wires by hand, the `CookieStore` /
  `ScraperConfigStore` satellite-inheritance bases, `StaticProxySource` /
  `HttpProxyValidator` (the only built-in `WithValidatedProxies` inputs), and
  **`SchemaContentParser<TNode>`** — the ADR-0002 custom-backend reuse vehicle.

### Migration

A fluent-API consumer (`new ScraperEngineBuilder()…BuildAsync()` /
`.RunAsync()`), a custom seam implementer (`IScraperSink`, `IScheduler`,
`ISchemaBackend<TNode>` + `SchemaContentParser<TNode>`, …), and the
distributed-worker pattern all need **no changes** — every type they touch
stayed public. The only break is code that constructed a core *implementation*
adapter by name (e.g. `new ConsoleSink()`, `new FileScheduler(...)`): switch
to the builder method that wired it (`.WriteToConsole()`,
`.WithTextFileScheduler(...)`) or to the interface. No behavioural change —
this is visibility + documentation only (zero IL delta in the documented
paths; `WebReaper.AotSmokeTest` still publishes Native-AOT zero-warning).

## 8.0.0 — Crawl driver + Outstanding-work latch; the per-Job shell is a value, not a thrower (breaking)

The per-Job `Spider` shell stops leaking its result through side channels and
stops throwing to terminate. `ISpider.CrawlAsync` now returns a closed
`JobReport` (the ADR-0001 `CrawlOutcome` + the loaded document); the shell is
reduced to load → Crawl step → report. The **Crawl driver** — in-process
`ScraperEngine` or the distributed worker — owns what the shell used to: the
visited-link tracker, the crawl-limit stop, sink fan-out, and the
`PostProcessor` / `ScrapedData` callbacks. The crawl limit is now a value the
driver checks (`Scheduler.Complete()`), no longer a `PageCrawlLimitException`
thrown through the fault-retry policy. Rationale, the rejected alternatives
(durable-workflow coordinator, emergent queue-drain, exact distributed limit)
and the staged design:
[`docs/adr/0022-crawl-driver-and-outstanding-work-latch.md`](docs/adr/0022-crawl-driver-and-outstanding-work-latch.md)
and [`research/distributed-crawl-termination.md`](research/distributed-crawl-termination.md).

**Release packaging (lockstep).** This is a core-major release wave: core and
all six satellites (`Cosmos`, `Mongo`, `Redis`, `AzureServiceBus`, `Puppeteer`,
`Sqlite`) are republished at `8.0.0` together, so a consumer never sees a
`WebReaper 8.0.0` + satellite-`7.x` skew and every satellite declares a
`WebReaper >= 8.0.0` dependency. Only `WebReaper.Redis` changed functionally
(the distributed Outstanding-work latch + the atomic-`SADD` `TryAdd`); the
other satellites are unchanged, rebuilt against core 8.0.0 for graph
coherence (`docs/RELEASE-RUNBOOK.md` lockstep selection).

The visited-link tracker becomes the single **idempotency authority**:
`IVisitedLinkTracker.TryAddVisitedLinkAsync` is an atomic test-and-set
(default-interface-method; `InMemoryVisitedLinkTracker` is a lock-free CAS,
`RedisVisitedLinkTracker` an atomic `SADD`). Termination detection is one
`IOutstandingWorkLatch` seam — a unit-credit counter that trips exactly once
when all work has drained, with an in-memory `Interlocked` adapter and a
distributed Redis adapter (atomic `INCRBY`/`DECRBY` + a `SET NX` one-shot
fence). `Examples/WebReaper.AzureFuncs` is now a real distributed Crawl
driver: it never throws to terminate, so the queue is no longer poisoned at
the crawl-limit boundary.

Closed by construction: the retry-amplified limit exception, the racy
discovery dedup, and the distributed poison message. The fluent builder API
is unchanged — `.PageCrawlLimit(...)`, `.Subscribe(...)`, `.PostProcess(...)`
and `.StopWhenAllLinksProcessed()` keep their signatures and behaviour; only
their internal wiring moved to the driver.

**Build hygiene (non-breaking).** The obsolete, *inert* `ServicePointManager`
calls in the HTTP transport were removed — `ServicePointManager` does not
affect `HttpClient`/`SocketsHttpHandler`, and the connection limit and
cert-bypass already live on the per-request `SocketsHttpHandler`, so there is
no behavioural change. The broken/ambiguous XML-doc crefs and bad paramrefs
that shipped in the package's IntelliSense XML were fixed. The deliberately
visible core CS1591 doc backlog is intentionally left as-is (it is a tracked
signal, not noise — see the satellite csproj rationale).

### Breaking changes

- **`ISpider.CrawlAsync` returns `Task<JobReport>`**, not `Task<List<Job>>`.
  A direct `ISpider` consumer (the `BuildSpider()` distributed-worker pattern)
  reads `report.Outcome.NextJobs` for child jobs and matches
  `report.Outcome is CrawlOutcome.Parsed` for a parsed page; sink fan-out and
  visited-link tracking are now the caller's (driver's) job.
- **`ScrapedData` / `PostProcessor` moved off the concrete `Spider`** to the
  Crawl driver. The public `ScraperEngineBuilder.Subscribe(...)` /
  `.PostProcess(...)` surface is unchanged and now wires onto the driver.
- **`PageCrawlLimitException` is removed.** The crawl limit is a value-driven
  stop, never an exception; remove any `catch (PageCrawlLimitException)`.
- **`Spider`'s constructor changed** (reduced to `ICrawlStep`, `IPageLoader`,
  `IScraperConfigStorage`); `ScraperEngine`'s constructor gained the
  visited-link tracker, sinks, optional callbacks and an optional
  `IOutstandingWorkLatch` (in-memory default). Both are normally built via
  `ScraperEngineBuilder` — no change for fluent-API consumers.
- **No compat shell.** A `List<Job>`-returning forwarder would reinstate the
  side channels this change removes (the ADR-0009 precedent); the break is
  announced here and in ADR-0022, not silent.
- **Builder argument validation is now fail-fast.** Public builder entry
  points that take a free-form string reject `null`/empty/whitespace *at the
  call that introduced it* instead of failing much later (at parse time, at
  first file I/O, or — for start URLs — as an engine that quietly crawled
  nothing): `ConfigBuilder.Build()` now requires a non-empty start-URL set and
  its message is plural; `Follow`/`FollowWithBrowser`/`Paginate`/
  `PaginateWithBrowser` reject a blank selector; the file-backed
  `ScraperEngineBuilder` methods (`WriteToCsvFile`/`WriteToJsonFile`/
  `TrackVisitedLinksInFile`/`WithFileConfigStorage`/`WithFileCookieStorage`/
  `WithTextFileScheduler`) reject a blank path; and `PageActionBuilder`'s
  `Repeat`/`RepeatWithDelay`/`RepeatAndWaitForNetworkIdle` throw a clear
  `InvalidOperationException` when called before any action (was a bare
  `ArgumentOutOfRangeException`). Correct code is unaffected — only
  previously-invalid input now throws; a major is the right window for the
  few cases that were silently accepted before.

### Migration

A fluent-API consumer (`new ScraperEngineBuilder()…BuildAsync()` /
`.RunAsync()`) needs **no changes** — the builder surface and behaviour are
preserved. A direct `ISpider` / `BuildSpider()` consumer updates to the
`JobReport` shape (re-enqueue `report.Outcome.NextJobs`; fan a
`CrawlOutcome.Parsed` page out to its sink itself) and drops any
`PageCrawlLimitException` handling; the rewritten
`Examples/WebReaper.AzureFuncs` is the reference distributed Crawl-driver
adapter. The one behavioural caveat for a fluent consumer: builder method
*signatures* are unchanged, but a call that previously passed a blank
string / empty start-URL set (or `Repeat*` before any action) now throws at
that call — pass valid input.

## 7.1.0 — WebReaper.Sqlite satellite: opt-in robust-local durable scheduler & tracker (additive)

New satellite package **WebReaper.Sqlite** — a local durable scheduler and
visited-link tracker backed by an embedded SQLite store via
`Microsoft.Data.Sqlite`. "Resume" is a `SELECT … WHERE consumed = 0` over an
indexed table: `FileScheduler`'s append-only job file + sidecar position file
+ `O(skip N)` line cursor (and the cursor↔job-file desync failure mode) are
gone for the consumer who opts in. Rationale, the satellite-not-core
constraint, and the considered options:
[`docs/adr/0012-sqlite-embedded-store-satellite.md`](docs/adr/0012-sqlite-embedded-store-satellite.md).

This is **additive** — nothing is removed or changed in core or the existing
satellites. The core file scheduler and visited-link tracker are
byte-unchanged and remain the zero-dependency local default; SQLite is the
opt-in robust-local tier between them and the distributed Redis / Azure
Service Bus satellites.

- `ScraperEngineBuilder.WithSqliteScheduler(databasePath, dataCleanupOnStart?, logger?)`
  over the public `WithScheduler` seam; `SqliteScheduler : IScheduler`.
- `ScraperEngineBuilder.TrackVisitedLinksInSqlite(databasePath, dataCleanupOnStart?)`
  over the public `WithLinkTracker` seam; `SqliteVisitedLinkTracker :
  IVisitedLinkTracker`. The `visited(url PRIMARY KEY)` table *is* the set —
  no in-memory mirror (a deliberate ADR-0012 deviation from the file
  tracker, mirroring the Redis tracker).
- The `Job` payload uses the same `WebReaperJson` grammar as the core file
  scheduler and the Redis scheduler (ADR-0008) — full type fidelity.
- Satellite per ADR-0009 / ADR-0012: core does not reference
  `Microsoft.Data.Sqlite`, so the native `e_sqlite3` (SQLitePCLRaw) graph
  stays off the dependency-light, Native-AOT-zero-warning core. Like the
  other satellites it is deliberately not marked `IsAotCompatible`.
- Versioned `7.1.0`: a new satellite added after the `7.0.0` satellite wave;
  it depends on `WebReaper` core `7.0.0`.

ADR-0012 itself carried a one-line correction (landed with the first
implementation slice, called out loud): its Mechanism section had wrongly
said `FileScheduler` writes its position file *after* the yield — the whole
`IScheduler` family is claim-before-yield / at-most-once for the in-flight
job, and `SqliteScheduler` matches it. Decision and shape unaffected.

## 7.0.0 — Satellite adapter packages; dependency-light core (breaking)

Heavy third-party adapters move out of the core `WebReaper` package into
per-technology satellite packages, wired through the builder's public
registration seam. Rationale, design, and the deliberate clean-cut (no compat
shell): [`docs/adr/0009-registration-seam-and-satellite-adapters.md`](docs/adr/0009-registration-seam-and-satellite-adapters.md).

This release lands the full ADR-0009 satellite set: **Cosmos**, **Mongo**,
**Redis**, **Azure Service Bus** and **Puppeteer**. The core `WebReaper`
package no longer references any of those SDKs — a plain HTTP→file crawl
pulls none of them.

It also closes the ADR-0008-named JSONPath follow-up: `JsonSchemaBackend`'s
Newtonsoft `JToken` JSONPath cursor — the last Newtonsoft reach in core — is
migrated to an in-repo JSONPath-subset evaluator over
`System.Text.Json.Nodes.JsonNode`. The supported dialect is preserved exactly
(optional `$`/`$.` root, `.`-separated property paths, trailing `[*]` array
wildcard — the whole surface the `Schema` model drives, pinned by the JSON
test corpus). With `CookieStore` already on System.Text.Json, **core is now
entirely Newtonsoft-free**: the `Newtonsoft.Json` `PackageReference` is
dropped and the *whole* core (not just a scoped path) publishes Native-AOT
zero-warning — verified by `WebReaper.AotSmokeTest`, now extended to exercise
the JSON backend. Rationale and the doc-lag correction:
[`docs/adr/0008-system-text-json-typed-pipeline.md`](docs/adr/0008-system-text-json-typed-pipeline.md).

### Breaking changes

- **`WriteToCosmosDb` moved to the `WebReaper.Cosmos` package.** It is now an
  extension method over `ScraperEngineBuilder`'s public `AddSink` registration
  seam, not a core builder method. `SpiderBuilder.WriteToCosmosDb` is removed.
- **`CosmosSink` moved**: `WebReaper.Sinks.Concrete` → namespace and package
  `WebReaper.Cosmos`.
- **Core no longer references `Microsoft.Azure.Cosmos`.** A core-only consumer
  no longer pulls Cosmos (or its Newtonsoft + native ServiceInterop graph).
- **No compat forwarders.** A forwarder would still reference the package and
  defeat the dependency-light core (ADR-0009 SemVer).
- `WriteToCosmosDb` no longer auto-uses the builder's logger; it takes an
  optional `ILogger` argument (defaults to `NullLogger`).
- **`WriteToMongoDb`, `WithMongoDbConfigStorage`, `WithMongoDbCookieStorage`
  moved to the `WebReaper.Mongo` package.** They are now extension methods
  over `ScraperEngineBuilder`'s public `AddSink` / `WithConfigStorage` /
  `WithCookieStorage` registration seams. `SpiderBuilder.WithMongoDbConfigStorage`
  and `SpiderBuilder.WithMongoDbCookieStorage` are removed.
- **Mongo adapter types moved** to namespace and package `WebReaper.Mongo`:
  `MongoDbSink` (was `WebReaper.Sinks.Concrete`), `MongoDbScraperConfigStorage`
  (was `WebReaper.ConfigStorage.Concrete`), `MongoDbCookieStorage` (was
  `WebReaper.Core.CookieStorage.Concrete`), `MongoBlobStore` (was
  `WebReaper.DataAccess`).
- **Core no longer references `MongoDB.Driver`.** A core-only consumer no
  longer pulls it — and the transitive `SharpCompress` `GHSA-6c8g-7p36-r338`
  audit-suppression moves out of core with it (now in `WebReaper.Mongo`).
- The three Mongo builder extensions no longer auto-use the builder's logger;
  each takes an optional `ILogger` argument (defaults to `NullLogger`).
- **`WithRedisScheduler`, `TrackVisitedLinksInRedis`, `WriteToRedis`,
  `WithRedisConfigStorage`, `WithRedisCookieStorage` moved to the
  `WebReaper.Redis` package.** They are now extension methods over
  `ScraperEngineBuilder`'s public `WithScheduler` / `WithLinkTracker` /
  `AddSink` / `WithConfigStorage` / `WithCookieStorage` registration seams.
  `SpiderBuilder.WriteToRedis` / `WithRedisConfigStorage` /
  `WithRedisCookieStorage` are removed.
- **Redis adapter types moved** to namespace and package `WebReaper.Redis`:
  `RedisScheduler` (was `WebReaper.Core.Scheduler.Concrete`),
  `RedisVisitedLinkTracker` (was `WebReaper.Core.LinkTracker.Concrete`),
  `RedisSink` (was `WebReaper.Sinks.Concrete`), `RedisScraperConfigStorage`
  (was `WebReaper.ConfigStorage.Concrete`), `RedisCookieStorage` (was
  `WebReaper.Core.CookieStorage.Concrete`), `RedisBlobStore` and
  `RedisConnectionPool` (were `WebReaper.DataAccess`).
- **Core no longer references `StackExchange.Redis`.** A core-only consumer
  no longer pulls it. ADR-0005's one-`ConnectionMultiplexer`-per-connection-string
  invariant is preserved: `RedisConnectionPool` moves whole and stays the
  single resolver every Redis adapter in the package goes through.
- The Redis builder extensions that took a logger no longer auto-use the
  builder's; each takes an optional `ILogger` argument (defaults to
  `NullLogger`).
- **`WithAzureServiceBusScheduler` moved to the `WebReaper.AzureServiceBus`
  package.** It is now an extension method over `ScraperEngineBuilder`'s public
  `WithScheduler` registration seam, not a core builder method. There was no
  `SpiderBuilder` equivalent to remove.
- **`AzureServiceBusScheduler` moved** to namespace and package
  `WebReaper.AzureServiceBus` (was `WebReaper.Core.Scheduler.Concrete`).
- **Core no longer references `Azure.Messaging.ServiceBus`.** A core-only
  consumer no longer pulls it. `WithAzureServiceBusScheduler` took no logger,
  so its signature is unchanged.
- **The headless-browser page loader moved to the `WebReaper.Puppeteer`
  package; the core default page loader is now HTTP-only.**
  `BrowserPageLoadTransport` moved (`WebReaper.Core.Loaders.Concrete` →
  namespace and package `WebReaper.Puppeteer`); `CookieExtensions` /
  `ToPuppeteerCookies` moved (`WebReaper.Extensions` → namespace and package
  `WebReaper.Puppeteer`).
- **Core no longer references `PuppeteerSharp` / `PuppeteerExtraSharp`.** A
  core-only consumer no longer pulls them or the Chromium provisioning path,
  and the ADR-0008-named `BrowserPageLoadTransport` `Assembly.Location` IL3000
  trim/AOT finding leaves core with it (now in `WebReaper.Puppeteer`).
- **New `WithLoadTransport` registration seam on `ScraperEngineBuilder`.** It
  takes a factory —
  `Func<ICookiesStorage, IProxyProvider?, ILogger, IPageLoadTransport>` —
  invoked at build time with the builder's resolved cookie storage, optional
  proxy provider and logger (a deliberate refinement of ADR-0009's stated
  `WithLoadTransport(IPageLoadTransport)`; see that ADR's Deliberate
  consequences note). ADR-0004's one-`IPageLoader` /
  two-`IPageLoadTransport` dispatcher is unchanged — only the default
  composition of the Dynamic slot moved out of core.
- **`GetWithBrowser` / `FollowWithBrowser` / `PaginateWithBrowser` still
  compile, but Dynamic pages now require `WebReaper.Puppeteer`.** With no
  browser transport registered a Dynamic load throws an actionable
  `InvalidOperationException` (`BrowserNotConfiguredPageLoadTransport`)
  pointing at the package and `.WithPuppeteerPageLoader()`, instead of a
  silent default. `.WithPuppeteerPageLoader()` is parameterless and — because
  the seam is a factory — reproduces the pre-7.0 behaviour exactly: the one
  shared cookie container (issue #26) and the optional proxy applied the
  browser's own way.
- **`SpiderBuilder` is now `internal`; the bare-spider seam is
  `ScraperEngineBuilder.BuildSpider()`.** The ADR-0009 capstone: the public
  registration seam lives *only* on `ScraperEngineBuilder`, and
  `SpiderBuilder`'s duplicate public surface is gone. The distributed-worker
  pattern (crawl one queued `Job`, re-enqueue its children — see
  `Examples/WebReaper.AzureFuncs`) gets a bare `ISpider` from the new public
  `ScraperEngineBuilder.BuildSpider()` instead of `new SpiderBuilder()…Build()`.
  Unlike `BuildAsync()`, `BuildSpider()` does not build or persist a
  `ScraperConfig`, so it does not require `Get`/`Parse` — the worker's config
  is persisted separately and read from storage at crawl time.

### Why

- Dependency-light core: a plain HTTP→file crawl stops transitively pulling
  Cosmos + Newtonsoft + native interop, `MongoDB.Driver` + its transitive
  SharpCompress CVE, `StackExchange.Redis`, `Azure.Messaging.ServiceBus`, and
  `PuppeteerSharp` + the Chromium provisioning path (carrying the ADR-0008
  `BrowserPageLoadTransport` IL3000 finding out of core).
- The builder deepens into a small public registration seam; per-adapter
  `WriteToX` sugar ships with its adapter. See ADR-0009.

### Migration

- Add the package: `dotnet add package WebReaper.Cosmos`.
- Add `using WebReaper.Cosmos;` wherever you call `.WriteToCosmosDb(...)` or
  reference the `CosmosSink` type. `WriteToCosmosDb`'s existing arguments are
  unchanged (an optional `ILogger` is appended).
- Add the package: `dotnet add package WebReaper.Mongo`.
- Add `using WebReaper.Mongo;` wherever you call `.WriteToMongoDb(...)`,
  `.WithMongoDbConfigStorage(...)`, `.WithMongoDbCookieStorage(...)` or
  reference the `MongoDbSink` / `MongoDbScraperConfigStorage` /
  `MongoDbCookieStorage` / `MongoBlobStore` types. Existing arguments are
  unchanged (an optional `ILogger` is appended).
- Add the package: `dotnet add package WebReaper.Redis`.
- Add `using WebReaper.Redis;` wherever you call `.WithRedisScheduler(...)`,
  `.TrackVisitedLinksInRedis(...)`, `.WriteToRedis(...)`,
  `.WithRedisConfigStorage(...)`, `.WithRedisCookieStorage(...)` or reference
  the `RedisScheduler` / `RedisVisitedLinkTracker` / `RedisSink` /
  `RedisScraperConfigStorage` / `RedisCookieStorage` / `RedisBlobStore` /
  `RedisConnectionPool` types. Existing arguments are unchanged (an optional
  `ILogger` is appended where one was passed).
- Add the package: `dotnet add package WebReaper.AzureServiceBus`.
- Add `using WebReaper.AzureServiceBus;` wherever you call
  `.WithAzureServiceBusScheduler(...)` or reference the
  `AzureServiceBusScheduler` type. Its arguments are unchanged.
- Add the package: `dotnet add package WebReaper.Puppeteer`.
- Add `using WebReaper.Puppeteer;` and call `.WithPuppeteerPageLoader()` on
  the builder wherever you scrape Dynamic pages (`GetWithBrowser` /
  `FollowWithBrowser` / `PaginateWithBrowser`), or wherever you reference the
  `BrowserPageLoadTransport` type or `CookieContainer.ToPuppeteerCookies(...)`.
  `.WithPuppeteerPageLoader()` takes no arguments and reproduces the pre-7.0
  default behaviour. A core-only (HTTP) crawl needs no change.
- If you constructed `new SpiderBuilder()…Build()` directly (the
  distributed-worker pattern), switch to
  `new ScraperEngineBuilder()…BuildSpider()` — the same `WithLogger` /
  `WithLinkTracker` / `AddSink` / etc. configuration, returning the same
  `ISpider`. Fluent `ScraperEngineBuilder` consumers need no change.
- If your code used `Newtonsoft.Json` and relied on getting it *transitively*
  through the `WebReaper` package, add an explicit
  `<PackageReference Include="Newtonsoft.Json" .../>` — core no longer
  references it. WebReaper's own APIs are `System.Text.Json` throughout, so a
  consumer that does not use Newtonsoft itself needs no change.

No code or API change, listing only: each satellite package
(`WebReaper.Cosmos` / `.Mongo` / `.Redis` / `.AzureServiceBus` / `.Puppeteer`)
now ships a focused README, the shared WebReaper icon, and release notes in its
`.nupkg`, so its nuget.org page renders like the core package's instead of
blank.

No code or API change, IntelliSense only: the core package now ships its XML
documentation as `lib/<tfm>/WebReaper.xml` (it previously shipped an `API.xml`
the IDE never resolved next to `WebReaper.dll`, so consumers got no doc
tooltips); the `DocumentationFile` redirect that also wrote a build artifact
into the tracked tree is gone. Each satellite now generates and ships its own
XML doc too, with the builder-extension API (`WriteToCosmosDb`,
`WithRedisScheduler`, …) documented; the moved adapter classes remain
undocumented by design (CS1591 suppressed in the satellites only).

## 6.0.0 — System.Text.Json typed pipeline (breaking, AOT-clean)

The extraction and persistence pipeline moved off `Newtonsoft.Json` +
`TypeNameHandling.Auto` to `System.Text.Json` source-gen with a typed
`JsonObject` terminal. Rationale, design, and bounded scope:
[`docs/adr/0008-system-text-json-typed-pipeline.md`](docs/adr/0008-system-text-json-typed-pipeline.md)
(supersedes the serialization grammar of ADR-0002/0003; closes the ADR-0005
`RedisScheduler` `Job` round-trip).

### Breaking changes

- **`ParsedData.Data`** is now `System.Text.Json.Nodes.JsonObject` (was
  `Newtonsoft.Json.Linq.JObject`).
- **`IFileSinkFormat.Header(JsonObject)` / `FormatRow(JsonObject)`** (was
  `JObject`). Observable file content (CSV header/rows, JSON-lines) is
  unchanged.
- **`PostProcess(Func<Metadata, JsonObject, Task>)`** on `ScraperEngineBuilder`
  / `SpiderBuilder` (was `JObject`).
- **`IContentParser` removed.** The Newtonsoft `JObject`-returning
  `ParseAsync` is gone. Use `IJsonContentParser.ParseToJsonAsync` →
  `JsonObject`. The built-in parsers (`AngleSharpContentParser`,
  `JsonContentParser`, `XPathContentParser`, `SchemaContentParser<TNode>`)
  implement `IJsonContentParser`; `WithContentParser` now takes
  `IJsonContentParser`.
- **Persisted/wire format changed.** Config, every `Job` (Redis, Azure Service
  Bus, File schedulers), and cookies now serialize via System.Text.Json
  source-gen (no `TypeNameHandling`). Polymorphic `PageAction.Parameters`,
  the `ImmutableQueue<LinkPathSelector>` chain, and `Schema`/`SchemaElement`
  round-trip via dedicated converters. **Clear distributed job queues and
  stored scraper config on upgrade** — old Newtonsoft-format payloads are not
  read by the new grammar.

### Why

- Removes Newtonsoft's reflection / `TypeNameHandling` — a *documented* bug
  class (the ADR-0003 file-adapter serialize-`Auto`/deserialize-defaults
  asymmetry; the ADR-0005 `RedisScheduler` `Job` asymmetry), now closed
  uniformly across all schedulers.
- AOT-clean typed pipeline: the Newtonsoft-free configuration
  (markup/CSS/XPath + STJ config/schedulers/sinks) publishes Native-AOT with
  zero trim/AOT warnings (verified by a CI `WebReaper.AotSmokeTest`); the
  library declares `IsAotCompatible`.
- Smaller, single-file, no-runtime-install deploy footprint.

### Migration

- Replace `JObject`/`JToken` in your `PostProcess`, custom sink, or custom
  parser code with `System.Text.Json.Nodes`: `obj["k"]!.GetValue<T>()`,
  `obj["k"]!.ToString()`, `obj["k"]!.AsArray()`, `JsonNode.DeepEquals(...)`.
- `parser.ParseAsync(...)` → `parser.ParseToJsonAsync(...)`.
- Drain Redis/Azure Service Bus job queues and delete stored config produced
  by ≤ 5.1.0 before running 6.0.0.

### Not removed (still Newtonsoft, opt-in, AOT-dirty only if used)

- **JSON-endpoint scraping** (`JsonContentParser` / `JsonSchemaBackend`): the
  JSONPath scope cursor is Newtonsoft `JToken` — System.Text.Json has no
  JSONPath. Named ADR-0008 follow-up.
- **`CosmosSink`**: the Cosmos SDK is itself Newtonsoft-coupled. See ADR-0008
  Bounded scope.

A consumer that uses neither still gets a fully AOT-clean publish (unreached
Newtonsoft is trimmed away).

## 5.1.0

- XPath selector backend (`AngleSharpXPathSchemaBackend`), discussion #17,
  ADR-0007.

## 5.0.0

- One page-loader seam with internal transports (ADR-0004); one keyed blob
  store + payload shells (ADR-0003); `RedisConnectionPool` (ADR-0005);
  buffered file-sink drain (ADR-0006).
