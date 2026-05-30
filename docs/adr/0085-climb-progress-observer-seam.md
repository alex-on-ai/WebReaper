# Climb-progress observer seam: an `IClimbObserver` the escalating loader notifies per rung, so a consumer streams the live climb without scraping logs

## Status

**Proposed** (2026-05-31). Design-pass-first draft for review.

- **Extends [ADR-0083](0083-escalating-page-loader.md)** (the block-aware escalating loader). 0083 built the HTTP to browser to stealth climb; this ADR makes the climb *observable* through a first-class push seam instead of only through `ILogger` side effects.
- **Touches [ADR-0004](0004-one-page-loader-transport-seam.md)** (the seam stays single; the observer is a collaborator on the loader, not a new loader), **[ADR-0066](0066-engine-cost-telemetry.md)** (`RunReport` stays the post-hoc aggregate; live per-rung progress is a separate streaming concern, argued below), and **[ADR-0022](0022-crawl-driver-and-outstanding-work-latch.md)** (the driver stays transport-blind; observing the climb is a loader concern, like the climb itself).
- **Motivated by** the WebReaper Cloud playground Tier B live-climb UX ([docs/CLOUD-PLAYGROUND-PHASE-2.md](../CLOUD-PLAYGROUND-PHASE-2.md)) and the still-unbuilt CLI `--progress json` NDJSON affordance named in [docs/CLOUD-SCRAPE-PLAYGROUND-PLAN.md](../CLOUD-SCRAPE-PLAYGROUND-PLAN.md). Both need per-rung progress as data, not as log text.
- **Minor (additive).** No existing contract changes. See SemVer.

## Context

ADR-0083 turned the page loader into a climbing loader: for one page it loads at the current rung, runs the `IBlockDetector`, and climbs HTTP to browser to stealth on a block. The climb is now the product's headline behaviour. The cloud playground animates it live; the CLI wants to print it.

But the loader surfaces the climb only as `ILogger` information lines, and only partially. Reading `EscalatingPageLoader.LoadAsync` (the climb loop):

- It logs the **attempt** ("Loading {PageType} page {Url} at tier {Tier}"), the **block-and-climb** ("Page {Url} blocked at tier {Tier} ({Reason}); climbing to tier {Next}"), and the **residual block** ("Page {Url} still blocked at the top tier: {Reason}").
- It does **not** log the **success**, the **winning rung**, or the **HTTP status**. On a clean load it returns the result with no log line at all.

So a consumer that wants to render the climb has only two unappealing routes:

1. **Scrape the logs.** Register an `ILoggerProvider`, filter the loader's category, and parse the message templates' structured state back into events. This makes a set of debug log strings an undocumented wire protocol for a product surface: a future reader who rewords a log line silently breaks the live demo. And it still cannot recover success, the winning rung, or the status, because those are never logged. The winning rung has to be *inferred* ("the last attempt with no following block") and the status is simply absent.
2. **Reconstruct from the `RunReport`.** The report (ADR-0066) carries the residual-block tally, but it is a per-run post-hoc summary available only after the run completes. A live climb needs a push *during* the run, rung by rung.

"Do not make every consumer reinvent this" is exactly the argument ADR-0083 used to pull block detection into a core `IBlockDetector`. Climb progress is intrinsic to a climbing loader; forcing each consumer to scrape logs for it is the same anti-pattern one layer along.

## Decision

Add a core push seam the loader notifies at each rung transition. Three types and one builder method.

### 1. The observer and the step

```csharp
public enum ClimbPhase { Attempt, Blocked, Climbing, Succeeded, Exhausted }

public sealed record ClimbStep(
    ClimbPhase Phase,
    string Url,
    int TierIndex,
    PageType PageType,
    int? HttpStatus,   // the loaded result's status; null pre-load (Attempt) or when a transport cannot determine it
    string? Reason);   // the block verdict's reason on Blocked / Exhausted; null otherwise

public interface IClimbObserver
{
    // Called on the load hot path, possibly concurrently (a crawl loads pages
    // under Parallel.ForEachAsync). Implementations must be cheap, non-blocking,
    // and thread-safe (e.g. a Channel write). Must not throw back into the loader.
    void OnStep(ClimbStep step);
}
```

`Url` rides on every step so a concurrent crawl's interleaved climbs are disambiguable by the consumer; a single-URL `scrape` ignores it.

### 2. The loader notifies; it does not change what it does

`EscalatingPageLoader` takes an `IClimbObserver` defaulting to a `NullClimbObserver` no-op singleton, the same null-object idiom the loader already uses for `IPageCache` (`NullPageCache`) and the Spider for `IActionResolver` (`NullActionResolver`). It calls `OnStep` at the four points it already reaches:

- before each rung load, `Attempt`;
- on a block with a higher real rung, `Blocked` then `Climbing` (the consumer reads the escalation as `TierIndex` to `TierIndex + 1`);
- on the clean-load return, `Succeeded` (the first time the winning rung and its status are reported at all);
- at the ceiling (still blocked at the top rung), `Exhausted`.

`HttpStatus` and `Reason` come straight off the `PageLoadResult` and the `BlockVerdict` the loop already holds, so the two gaps that made log-scraping lossy (status, winning rung) close by construction. No control flow changes; the null-object default means an unwired engine behaves and performs exactly as before.

### 3. Wiring: a public builder method, the index stays the identity

`ScraperEngineBuilder.WithClimbObserver(IClimbObserver)` forwards to the internal `SpiderBuilder`, which passes it into the `EscalatingPageLoader` constructor next to the `IBlockDetector` and `HostTierFloor` it already supplies. This mirrors `WithBlockDetector` and `WithRetryPolicy` exactly.

A `ClimbStep` identifies its rung by **index**, not a name. A `PageLoadTier` is `(PageType, IPageLoadTransport)`; a vanilla browser rung and a stealth rung are both `Dynamic` CDP transports, indistinguishable to core, and ADR-0009 quarantine means core must not learn the word "stealth". The consumer built the ladder (HTTP is 0, then each `WithLoadTransport` rung in registration order), so it owns the index-to-label map. Core reports structure; the consumer names it.

### 4. The result stays on the sink

The observer is load-stage: it reports the climb, not the extracted data. The page's Markdown or records continue to reach a consumer through `IScraperSink` (ADR-0031), exactly as today. A consumer that wants both the climb and the output (the cloud backend does) wires an observer and a sink. This keeps the observer pure over the load stage, the same line ADR-0083 drew when it kept `IBlockDetector` pure and let record count re-enter one layer up at the driver.

## Considered options

- **An injected `IClimbObserver` push seam (chosen).** Matches the codebase's interface-seam convention (`IBlockDetector`, `IScraperSink`, `IPageLoader`); the null-object default is zero-cost and non-breaking; it reports the status and winning rung that the logs never had.
- **Log interception (rejected).** Zero core change, but it turns debug log templates into a product wire protocol, cannot recover success / winning rung / status, and forces a winning-rung heuristic. That fragility is the whole reason this ADR exists.
- **`IProgress<ClimbStep>` instead of a named interface (rejected).** The BCL idiom, but `Progress<T>` captures and posts to the `SynchronizationContext`, unwanted on a server hot path, and it reads less intentionally than the explicit seam the rest of the loader uses. `OnStep` is `IProgress<T>.Report` by another, house-style name.
- **A C# `event` on the loader (rejected).** `EscalatingPageLoader` is internal and per-engine; a consumer never holds the instance to subscribe to. Threading an injected collaborator through the builder is the established shape, the way every other loader collaborator arrives.
- **Carry climb steps on `RunReport` (rejected).** The report is the per-run post-hoc summary (ADR-0066); per-step live progress is a push during the run. The same split ADR-0083 made when it put the per-page verdict on `PageLoadResult` rather than only in the run aggregate.
- **Reuse `IScraperSink` for progress (rejected).** The sink is the post-extraction target-data fan-out; it never sees a blocked or climbing load, and it is keyed to records, not rungs. Different lifecycle, different payload.

## Accepted cost

- **One more public seam to keep stable.** A new interface, record, enum, and builder method enter the public surface. The null-object default keeps every existing consumer and benchmark unchanged.
- **The observer is on the load hot path.** A slow or throwing observer would stall or break the climb. The contract (cheap, non-blocking, thread-safe, never throw) is documented on `OnStep`, not enforced; the cloud backend satisfies it with a non-blocking `Channel` write. A defensive try/catch around the call site is a reasonable hardening if a misbehaving observer ever bites, deferred until it does.
- **The consumer owns rung naming.** Core emits an index; the human label ("HTTP", "Headless browser", "Stealth browser") lives with the consumer that built the ladder. Trivial today (the cloud backend and the CLI each know their own ladder), named here so a future "core should label rungs" suggestion has its rejection on record: it would force core to name a stealth rung it deliberately cannot see (ADR-0009).
- **Two payloads to wire for the full picture.** Climb on the observer, result on the sink. Deliberate (load-stage purity), but a consumer must remember both.

## Deliberate consequences

- **The live climb streams without log-scraping.** The cloud playground Tier B backend maps `ClimbStep` to its existing `ClimbEvent` SSE shape one-to-one, with no dependency on log message wording.
- **The CLI `--progress json` affordance (the PLAN doc) gets a clean target.** The same seam serves the NDJSON-on-stderr progress stream the original playground plan called for and never built.
- **The loader reports success for the first time.** The clean-load path was silent; `Succeeded` now carries the winning rung and its HTTP status, useful to any consumer (telemetry, the CLI exit summary, the demo).
- **Log lines stay logs.** The information lines in the climb loop remain for operators and are now free to change wording without breaking a consumer, because they are no longer the progress contract.

## SemVer

**Minor (additive).** A new public interface, record, enum, and `ScraperEngineBuilder.WithClimbObserver` method; a new optional constructor parameter on the internal `EscalatingPageLoader` and a new field on the internal `SpiderBuilder`. No existing public contract changes, and the default null-object preserves current behaviour exactly. Ships within the v11.0.0 wave alongside ADR-0083 but is not itself a breaking change.

## v2 deferrals (named so they don't drift)

- **A per-rung label on the seam.** If a second non-cloud consumer also has to reconstruct index-to-name, carry an optional rung label (sourced from the `WithLoadTransport` registration) on `ClimbStep`. The index suffices for the two consumers in hand.
- **The Markdown / result on the observer.** Keeps load-stage purity today; revisit only if a consumer genuinely cannot also wire a sink.
- **An async `OnStep` for backpressure.** The sync fire-and-forget plus a bounded `Channel` covers the streaming case; an async observer is a change to make only if a real consumer needs the loader to await it.
- **A defensive guard around the `OnStep` call.** Add the try/catch if a misbehaving observer ever threatens a real run; left out now to keep the hot path bare.
- **`CONTEXT.md` glossary terms** (`climb observer`, `climb step`, `climb phase`) land with the implementation PR, when the types exist, keeping the glossary a description of built state (the ADR-0083 precedent).
