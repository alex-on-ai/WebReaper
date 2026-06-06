# Block-aware escalating page loader: a `PageLoadResult` contract, a core `IBlockDetector` seam, and host-sticky HTTP→browser→stealth climbing

## Status

**Proposed** (2026-05-30). Design-pass-first draft for review.

- **Supersedes [ADR-0056](0056-cli-bot-check-escalation.md)** (CLI bot-check escalation). Its CLI-only "run once, post-mortem, rebuild engine, retry" model is replaced by a core escalating loader that climbs per page.
- **Amends [ADR-0004](0004-one-page-loader-transport-seam.md)** (the one page-loader / transport seam). The seam stays single; its *return type* widens from `Task<string>` to `Task<PageLoadResult>` and its *failure contract* narrows from "any non-success is an exception" to "only a no-response failure is an exception".
- **Touches** [ADR-0022](0022-crawl-driver-and-outstanding-work-latch.md) (the driver stays transport-blind; escalation is a loader concern, not a driver concern), [ADR-0026](0026-retry-policy-seam.md) (retry comes to mean "the transport faulted", never "the server returned a status we dislike"), [ADR-0066](0066-engine-cost-telemetry.md) (`RunReport` grows residual block info), and [ADR-0081](0081-site-sweep-whole-site-crawl.md) (whole-site `crawl` gains per-page climbing).
- **v11.0.0 major** (breaking loader contract). See SemVer.

## Context

[ADR-0056](0056-cli-bot-check-escalation.md) shipped a CLI bot-check detector and a vanilla→stealth retry. Verifying it end-to-end on `master` showed **the escalation never fires in production**, and two further latent defects sit next to it. All three share one root cause.

### The escalation is dead-wired

`BotCheckDetector.Detect(httpStatus, renderedHtml, recordCount)` is correct in isolation, but both production call sites in `ScrapeCommand` feed it inputs that can never trip either signal:

- **Signal 1 (status `403/429/503`)** is passed `httpStatus: null` always. The CDP transport never surfaced an HTTP status (ADR-0056 §"Accepted cost" named this). Dead on the browser path, which is the only path with escalation.
- **Signal 2 (`recordCount == 0` AND a challenge marker in `renderedHtml`)** is unsatisfiable as wired: `renderedHtml` is `records[^1].Data.ToJsonString()`, set **only when `records.Count > 0`** (`ScrapeCommand` populates `LastHtml` inside `if (records.Count > 0)`). So `renderedHtml != null` implies `recordCount > 0`, which contradicts the `recordCount == 0` clause. The two preconditions are mutually exclusive.

Both branches always return `NoSignal`. The unit tests pass only because they call `Detect()` directly with input pairs (`status=403, records=5`; `html!=null, records=0`) the real wiring never produces.

### Two defects in the same family

- **`--stealth` is inert.** `ctx.Stealth` is parsed but never consulted in the run flow; the only stealth code path is the unreachable retry. `--stealth` therefore behaves exactly like `--browser` and never reaches the stealth backend.
- **HTTP hard-blocks stack-trace.** `HttpPageLoadTransport` throws `InvalidOperationException` on any non-2xx (with `statusCode` stashed in `Exception.Data`). A job that throws after retries propagates out of `Parallel.ForEachAsync` and is rethrown by `ScraperEngine.RunAsync`. So `webreaper scrape <403-site>` exits with a stack trace, not a clean result. The status the transport *had* dies in the exception bag.

### The one root cause

**The detector is fed extracted output, never the raw transport response.** HTTP status is not in the contract at all (`IPageLoader.LoadAsync : Task<string>`), and a non-2xx is thrown rather than returned, so its status is discarded. "Raw HTML" reaches the CLI only as the JSON of an extracted record. Every symptom above follows from that.

[PR #166](https://github.com/alex-on-ai/WebReaper/pull/166) shipped a stopgap: a stderr hint when a scrape returns zero records ("retry with `--browser`/`--stealth`"). That is the **Empty result** affordance and it stays; it is not, and was never meant to be, block detection.

## Decision

Make the raw transport response a first-class value, detect blocks in core from that value, and let a single core loader climb transports per page. Nine parts.

### 1. Widen the page-loader contract to `PageLoadResult`

`IPageLoader.LoadAsync` and `IPageLoadTransport.LoadAsync` return `Task<PageLoadResult>` instead of `Task<string>`.

```csharp
public sealed record PageLoadResult
{
    public required string Html { get; init; }
    public int? HttpStatus { get; init; }          // null only when a transport genuinely can't determine it
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = ReadOnlyDictionary<string, string>.Empty; // case-insensitive comparer
}
```

**Init-only properties, not a positional record.** This is the one place we deviate from the codebase's positional-record convention (`CrawlOutcome`, `JobReport`, `RunReport`, `PageContext`), and deliberately: `PageLoadResult` is a *growing bag of response metadata consumed by field access*, where deconstruction buys nothing and positional arity changes have repeatedly cost us (the [ADR-0066](0066-engine-cost-telemetry.md) `RunReport` and [ADR-0061](0061-agent-decision-outcome.md) `AgentRunSnapshot` arity breaks). Init-only lets future fields (`FinalUrl`, `ContentType`) land additively.

**Why widen rather than reuse existing seams.** Raw HTML is already reachable (`PageContext.Html`) and HTTP status is already known at the throw site (`Exception.Data["statusCode"]`). We could juggle those. We do not, because (a) it perpetuates "the detector sees second-hand data", and (b) it leaves the library *unable to report an HTTP status to anyone* — a real capability gap a `PageLoadResult` closes for every consumer, not just the CLI.

### 2. Failure contract: a completed response is data, not a fault

A response the server actually returned — `200`, `403`, `503` alike — is returned as a `PageLoadResult`. `LoadAsync` throws **only** when there is no response at all: DNS failure, connection refused, TLS error, timeout. Those throw a typed `PageLoadException` (no status).

This reverses [ADR-0004](0004-one-page-loader-transport-seam.md)'s "a page that cannot be retrieved is surfaced as an exception" *for the non-2xx case*. That stance was written when the only consumer was "give me HTML to parse", where a non-2xx was useless. Status is now a feature; a `403` is the server *successfully* answering.

**Consequence for [ADR-0026](0026-retry-policy-seam.md).** The retry policy retries exceptions; with non-2xx no longer throwing, transient `5xx`/`429` are no longer auto-retried. This is acceptable and arguably an improvement: today that retry is four immediate, zero-delay re-hits (`FixedAttemptsRetryPolicy` has no backoff), which for a `429` is counterproductive and for a `503`-as-challenge is wasted — the correct response to a challenge-class code is to *climb*, which is this ADR. Retry now means exactly "the transport faulted" (timeouts, connection drops), never "the server returned a code we dislike". Status-aware retry with real backoff is a clean, separately-motivated future policy (deferred).

### 3. A core `IBlockDetector` seam producing a `BlockVerdict`

Block detection becomes a core scraping capability, not a CLI feature. "Am I being blocked?" is intrinsic to a scraper; making every consumer reinvent it is the same anti-pattern that justified widening `PageLoadResult`.

```csharp
public enum BlockConfidence { None, Weak, High }  // High = status/header; Weak = body-marker only

public sealed record BlockVerdict(BlockConfidence Confidence, string? Reason)
{
    public bool IsBlocked => Confidence != BlockConfidence.None;
}

public interface IBlockDetector
{
    BlockVerdict Detect(PageLoadResult result);
}
```

The verdict carries the **confidence tier**, not just a bool. We said earlier it would be a minimal `(bool, Reason)`, but the drop rule (part 8) and the host-floor rule (part 5) both branch on *which* signal fired, so the tier has to ride on the verdict; the `Reason` string stays for humans.

The default implementation ports ADR-0056's `BotCheckDetector` heuristic and **operates purely on `(HttpStatus, Headers, Html)`**:

- **Status** (`403/429/503`) → blocked, **high** confidence.
- **Headers** — challenge-signalling response headers (`cf-mitigated`, `cf-ray`, `x-datadome`, …) → blocked, **high** confidence. The most reliable signal, and the one the widening unlocks.
- **Body markers** — a challenge-string list scanned against raw `Html` → blocked, **weak** confidence. Tightened from ADR-0056's list to challenge-*structural* strings ("Just a moment...", "cf-chl-bypass", "Incapsula incident ID"); bare vendor names ("DataDome", "Akamai") are dropped, because they appear in legitimate content and — under suppression (part 8) and host-stickiness (part 5) — a false positive is now costly.

**Record count leaves the detector but does not vanish — it moves to the driver's drop decision.** `IBlockDetector` is pure over the load stage, so it cannot (and should not) see record count; that deletes ADR-0056's unsatisfiable zero-records-AND-marker precondition by construction. But record count is exactly what disambiguates a *weak* verdict from a false positive, so it re-enters one layer up, post-extraction, in the driver's **drop** rule (part 8) — never back inside the seam. Detection stays reporting, not acting: the loader classifies the load; the driver acts on the verdict, as it already does for the visited-link and stop-rule verdicts.

This sharpens two terms (for `CONTEXT.md` at implementation time):

- **Blocked page** — a load result `IBlockDetector` classifies as a challenge from status, headers, or body markers. Reliable, load-stage, drives escalation.
- **Empty result** — a page that loaded fine but yielded zero extracted records. Could be genuinely empty or a missed block. Weak, extraction-stage; stays a CLI hint (PR #166), never a verdict.

ADR-0056 fused these; the fusion was the bug.

### 4. The escalating page loader (the climb lives in one core decorator)

A core `EscalatingPageLoader` decorates an ordered set of transport **tiers** and an `IBlockDetector`. For one page it: loads at the current tier, runs `IBlockDetector` on the result, and if `IsBlocked` and a higher tier exists, climbs and reloads; it returns the best `PageLoadResult` it reached. Tiers, lowest to highest:

```
HTTP  ──blocked?──▶  browser (vanilla Chromium)  ──blocked?──▶  stealth (CloakBrowser)
```

Because the climb is entirely inside one `LoadAsync` call for one page, **the engine, driver, scheduler, and visited-link authority are untouched** — to them it is just an `IPageLoader` (ADR-0004's single-seam shape holds; ADR-0022's transport-blind driver holds). The loader is itself **core**: its tiers are injected `IPageLoadTransport`s, so the browser/stealth tiers come from the `Cdp`/`Playwright` satellites the consumer referenced without core ever referencing them (ADR-0009 quarantine intact). And because it is a loader, **the same mechanism serves every Spider-driven command — `scrape` and `crawl`** — with no separate per-command escalation path (`map` does not load through the Spider; see part 7). This is why it supersedes ADR-0056's "rebuild the whole engine and re-run" model (which could never scale to a multi-page crawl) and the originally-considered CLI-level ladder.

### 5. Host-sticky climbing

The escalating loader holds a per-run, per-host **floor tier** (a concurrent `host → tier` map, reset per engine — the [ADR-0050](0050-semantic-page-actions.md) `SemanticActCoordinator` per-crawl cache is the precedent). The floor lifts **only on a high-confidence block** (status/header), never on a weak body-marker climb — so one false-positive page (a post that merely names a vendor) cannot promote a whole legitimate host. Subsequent same-host pages start at the lifted floor. Bot protection is near-always site-wide, so a whole-site `crawl` of a status-signalling domain settles at the right tier after the first concurrent wave: the floor is written *post*-climb, so the first ~`P` (the parallelism level) concurrent same-host pages each climb before any of them lifts the floor — a bounded one-time cost of ≤ `P` climbing pages per host, noise against a large crawl. A "first-touch lock" that serialises the first probe per host would make it exactly one, at the price of per-host contention for a one-time cost — rejected as premature (named here in case it ever bites small single-host crawls). A body-marker-only site does not get the floor lift and re-climbs per page (the safe trade). Pure per-page climbing was rejected for that waste.

### 6. Flags set the starting rung; the verdict drives the climb

- default `scrape` → start at HTTP, climb on a block.
- `--browser` → start at the browser rung.
- `--stealth` → start at the stealth rung. **This is the `--stealth` fix**: it stops being inert and simply means "start at the top".
- **HTTP→browser auto-climbs, with no opt-out.** It is the headline behaviour ("automatic browser fallback for blocked sites") and it is cheap; a fast-fetch purist is not a case we serve here.
- **Stealth is decided once, at startup.** A core loader cannot run an interactive `Y/n` prompt, and prompting on page 47 of a crawl would be wrong anyway. The CLI decides the top tier before building the engine — the existing ADR-0056 `Y/n` + `--auto-stealth` + `WEBREAPER_AUTO_STEALTH` policy gates whether the stealth tier is *included*. After that the loader climbs autonomously. `--no-auto-stealth` now means "top tier is browser" (climb stops there).

### 7. `crawl` gets full per-page climbing; `map` stays best-effort

Because escalation is a loader concern (part 4), `crawl` (ADR-0081 whole-site) gets the same per-page climb as `scrape`, made affordable by host-stickiness (part 5) — no driver/queue/visited surgery, no mid-crawl transport-swap machinery, the single fixed loader is the climbing loader.

`map` is the exception. `SiteMapper` fetches through its own `HttpClient`, not `IPageLoader` (ADR-0042 — "discovery is one request, not a Crawl"), so it gets no climbing. A block during discovery degrades gracefully (fewer URLs, logged), and the one fetch climbing would help — the root page — is re-loaded by the `crawl` that typically follows, where climbing applies. Routing `SiteMapper` through the escalating loader is a deferred follow-up, not this ADR.

### 8. Residual-blocked pages are suppressed; `RunReport` carries the tally

A page still blocked after exhausting every tier must not be mistaken for data. Its content (a challenge page) is **dropped by default** — not extracted, not emitted — so a blocked scrape yields zero records plus a clear signal, never challenge-page garbage in the sinks (the failure mode this ADR is removing, not introducing in a new shape).

This requires the verdict to flow **per page**, not only as a run aggregate: the loader already computed it, so it rides out on `PageLoadResult` → `JobReport`. The driver then applies the **confidence-split drop rule**:

- **High-confidence** residual block (status/header at the top tier) → drop before extraction; a confirmed challenge is not worth extracting.
- **Weak** residual block (body-marker only) → extract anyway, then drop **iff** zero records. A page that yielded real records was a false positive (or a beatable challenge) and is **kept** — this is where record count re-enters (part 3), and what stops a vendor-name false positive from destroying a real page.

Loader reports, driver acts — ADR-0022's line holds: the driver still classifies nothing, it acts on a reported verdict exactly as it already does for the visited-link and stop-rule verdicts. Every drop is tallied for the run report.

`ScraperEngine.RunAsync` already returns `RunReport` (ADR-0066), which the CLI currently discards. It gains the residual-block tally — for single-URL `scrape` the page's final verdict; for multi-page an aggregate ("3 of 340 pages still blocked at the top tier — consider a captcha solver"). The CLI captures `RunReport` to report it and to set its exit code. (Positional-record arity change on `RunReport`, consistent with ADR-0066's own evolution.)

Keeping the raw challenge HTML (for debugging a block) rather than dropping it is an opt-in flag, deferred.

### 9. Page cache never stores a blocked result

The `IPageCache` (ADR-0041, `WithMaxAge`) caches by `(url, page-type)`. A result whose `BlockVerdict.IsBlocked` is true is **never written** to the cache, and a climb bypasses the cache for the higher tier — so the cache only ever holds clean loads, and a stale cached block can never silently defeat a later climb. The verdict on `PageLoadResult` makes this a one-line guard on the cache-write path.

## Considered options

- **Escalating loader + `PageLoadResult` + core `IBlockDetector` + host-sticky climb (chosen).** Fixes the root cause once; serves every command from one mechanism; preserves ADR-0022 and the single-seam ADR-0004 shape.
- **No-widen: feed the detector from existing seams (`PageContext.Html` + a typed status-carrying exception) (rejected).** Smallest change, but perpetuates "detector sees second-hand data" and leaves the library unable to report a status. The widening's capability gain is the whole point.
- **Surface status via a `RunReport.object?` side-channel à la ADR-0066's `Llm` (rejected).** `RunReport` is a per-run summary; per-page status belongs on the per-page load value.
- **Keep throwing on non-2xx, add only a typed `PageLoadException { StatusCode }` (rejected).** Status would exist only on the failure path — the asymmetry we are trying to remove.
- **CLI-only detection over raw core data (rejected).** "Don't make every consumer reinvent block detection" is the same argument that justifies widening `PageLoadResult`; a scraper should know when it is blocked.
- **CLI-level escalation ladder: re-run the whole engine at a higher transport (rejected; was the interim Q6 shape).** Fine for one URL, absurd for a thousand-page crawl. The core loader unifies and subsumes it.
- **Pure per-page climbing, no host-stickiness (rejected).** Every page of a uniformly-blocked site pays the failed-lower-tier tax.
- **Keep status-based auto-retry via a status-aware retry policy (deferred).** Keeps ADR-0026 generic now; transient-`5xx` backoff is a separate future policy.

## Accepted cost

- **A v11 major breaking change.** `IPageLoader`/`IPageLoadTransport` return types change, so every transport (`Http`, `Cdp`, `Playwright`, proxy-rotating variants, the `BrowserNotConfigured` sentinel) and every third-party transport satellite must return `PageLoadResult`; `JobReport.Document` and the `PageContext` build path thread it through. Accepted deliberately — breaks are fine when they buy a capability, and this buys library-wide status/headers.
- **Core takes on the churny challenge-marker list.** Marker updates become core releases. The `IBlockDetector` seam lets consumers override; the default list still lives in core.
- **Transient `5xx`/`429` are no longer auto-retried on the HTTP path.** They were four no-delay re-hits anyway; genuine transport faults still retry.
- **A blocked page is loaded more than once,** and the first concurrent wave per host each climbs before the floor lifts. Bounded — ≤ two climbs per page; ≤ `P` (the parallelism level) climbing pages per host, once — then amortised by host-stickiness. Noise against a large crawl.
- **Stealth consent is a startup decision, not per-page.** A crawl cannot interactively consent to a 220 MB download mid-run; including the stealth tier is decided before the engine is built.

## Deliberate consequences

- **Automatic browser fallback for blocked sites now exists** — the question that started this — for `crawl` too, not just `scrape`.
- **The library can report an HTTP status and response headers for the first time.**
- **The `403` stack-trace is gone**; a challenge response is data the loader can act on.
- **`crawl` stops silently harvesting challenge pages**; it climbs, drops the unrecoverable ones, and reports residual blocks.
- **`--stealth` finally does something** (starts at the top rung).

## SemVer

**v11.0.0 major.** The loader/transport return-type change is a public breaking change to a core seam and to the transport-satellite contract. Bundle the related cleanups (delete the dead CLI `BotCheckDetector` escalation in favour of the core seam; keep PR #166's `EmptyResultAdvisor`) into the same major.

## v2 deferrals (named so they don't drift)

- **`PageLoadResult.FinalUrl` / `ContentType`.** Additive later thanks to init-only.
- **Status-aware retry policy with backoff** (the transient-`5xx` resilience dropped in part 2).
- **Marker registry as data, not code** (carried over from ADR-0056; the `IBlockDetector` seam makes a data-driven default detector a clean future swap).
- **Captcha-solver tier** — a fourth rung above stealth; the residual-block `RunReport` signal (part 8) is its integration point.
- **Self-sufficient `map` escalation** — route `SiteMapper`'s fetches through the escalating loader so discovery climbs too (grill #3 left it best-effort).
- **`CONTEXT.md` glossary terms** (`PageLoadResult`, escalating loader, block detector / verdict, blocked-vs-empty) land with the implementation PR, when the types exist, keeping the glossary a description of built state.
