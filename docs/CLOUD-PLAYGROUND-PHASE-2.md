# WebReaper Cloud Playground, Phase 2 Build Slice (Tier B, live browser + stealth climb)

**Status:** Proposed build slice. Implements Phase 2 (Tier B) of [CLOUD-SCRAPE-PLAYGROUND-PLAN.md](CLOUD-SCRAPE-PLAYGROUND-PLAN.md).
**Date:** 2026-05-31
**Owner:** Alex
**Decision trail:** the 2026-05-31 Tier B scope grill. Phase 0 (canned climb hero) and Phase 1 (Tier A, HTTP to Markdown, live at `webreaper.ai/playground`) shipped. This slice adds the browser and stealth rungs so the live HTTP to browser to stealth climb, the product's headline, works end to end and user-driven.

## What this depends on (read first)

**A CloakHQ OEM/SaaS license is a pre-public-launch dependency.** The live stealth tier runs the CloakBrowser binary on our infrastructure to serve third-party visitors' URLs. That is browser-as-a-service, which CloakBrowser's `BINARY-LICENSE.md` ("Cloud, Container & Integration Use") puts behind a separate OEM/SaaS license (`cloakhq@pm.me`). Two consequences, both verified against the license text, not the ADR-0054 paraphrase:

- **Baking the binary into the image is permitted** (the license explicitly allows storing and running the unmodified binary in Docker images for internal infrastructure). The blocker is never redistribution or cold-start mechanics; it is the *purpose* of serving third parties.
- **The stealth rung must not be exposed to public traffic until the license is signed.** The vanilla browser rung (Playwright Chromium, Apache/BSD) carries no such constraint and can go public freely. Dev and staging behind the existing `X-Playground-Secret` gate are fine now; this slice is built on the assumption the license lands before the stealth rung is unsecreted.

## Goal

Paste any URL, watch the real climb live (HTTP challenged, vanilla browser challenged, stealth gets through), get clean Markdown. The same animation that is already the canned homepage hero becomes live and user-driven. Tier B is gated (email capture) and capped, and is WebReaper Cloud's first metered product surface, not a free toggle.

## Architecture (extends Phase 1)

```
[playground client, /playground]  --EventSource /api/playground/scrape?url=&cf=-->
   [Vercel edge gate]  Turnstile + email-capture + per-IP/global rate limit + daily-budget kill switch
                       proxy SSE through; backend URL stays private; inject X-Playground-Secret
        |
        v
   [Tier B app, Fly, DEDICATED ORG]   GET /scrape/stream?url=...  -> SSE of ClimbEvent
        require X-Playground-Secret; concurrency soft_limit = 1; per-job ~45s kill; auto-stop
        in-VM nftables egress firewall (no RFC1918 / loopback / link-local / fdaa::/8)
        baked image: Chromium + CloakBrowser binary, ~2 GB RAM Machine, --ipc=host
        |
        v
   ScraperEngineBuilder.Crawl(url).AsMarkdown()
        .WithCdpPageLoader(vanilla)        // rung 1
        .WithCloakBrowser(baked, no-download)   // rung 2
        .WithClimbObserver(observer)       // ADR-0085: per-rung steps
        .AddSink(resultSink)               // the `result` event
   -> EscalatingPageLoader climbs HTTP -> browser -> stealth (ADR-0083)
   -> observer -> bounded Channel -> SSE;  resultSink -> SSE `result`
```

Tier A stays exactly as shipped (its own direct `HtmlToMarkdown` path). Tier B is a second code path in the same app.

## Decisions

### 1. The backend drives the engine climb, not Tier A's direct primitive

Tier A calls `HtmlToMarkdown.ExtractMainContent` directly because an HTTP fetch has no climb. Tier B builds a real engine so the ADR-0083 `EscalatingPageLoader` does the climbing. The composition is all **public** builder surface (verified: `WithLoadTransport` is public at `ScraperEngineBuilder:640`; `WithCdpPageLoader` and `WithCloakBrowser` wrap it; the internal `SpiderBuilder` assembles the rungs into the `EscalatingPageLoader` at `:345-362`):

```csharp
var observer = new ChannelClimbObserver();   // OnStep -> bounded Channel -> SSE
var sink     = new MarkdownResultSink();      // EmitAsync -> the `result` event

await using var engine = await ScraperEngineBuilder
    .Crawl(url)                               // rung 0: HTTP (Static); start here so the viz shows the whole ladder
    .AsMarkdown()                             // MarkdownContentExtractor; output fans out to sinks
    .WithCdpPageLoader(new CdpLaunchOptions { // rung 1: vanilla Chromium (Dynamic)
        ExecutablePath = bakedChromiumPath,
        AdditionalArgs = ["--no-sandbox", "--disable-dev-shm-usage"],
    })
    .WithCloakBrowser(new CloakBrowserOptions { // rung 2: stealth (Dynamic), binary baked in
        ExecutablePath = bakedCloakBrowserPath,
        AutoInstall    = AutoInstallPolicy.Disabled,  // never download at runtime
    })
    .WithClimbObserver(observer)              // ADR-0085
    .AddSink(sink)
    .BuildAsync();

var report = await engine.RunAsync();          // climb runs; observer + sink stream; report carries the residual-block tally
```

`Crawl` (not `CrawlWithBrowser`) so the start page enters at the HTTP rung and the viz shows HTTP being challenged before the climb. No link selectors, so it is a single page (the plan's scrape-one-URL non-goal holds).

### 2. Climb to SSE via the ADR-0085 observer seam, not log-scraping

The grill rejected reconstructing the climb from the loader's `ILogger` strings (brittle; no success, winning rung, or status). [ADR-0085](adr/0085-climb-progress-observer-seam.md) adds a first-class `IClimbObserver` the loader notifies per rung. The backend maps `ClimbStep` to the existing `ClimbEvent` wire shape (`website/lib/playground/climb-events.ts`) one to one. The backend owns the index-to-name map because it built the ladder (`0=http, 1=browser, 2=stealth`); core never names "stealth" (ADR-0009):

| `ClimbStep.Phase` | source | `ClimbEvent` |
|---|---|---|
| `Attempt` | observer | `{ kind: "attempt", tier }` |
| `Blocked` | observer | `{ kind: "blocked", tier, status, reason }` |
| `Climbing` | observer | `{ kind: "escalate", from, to }` (to = idx+1) |
| `Succeeded` | observer | `{ kind: "success", tier, status }` |
| `Exhausted` | observer | `{ kind: "exhausted", tier, reason }` |
| (page output) | `IScraperSink.EmitAsync` | `{ kind: "result", title, markdown }` |
| (wrapper) | backend | `{ kind: "request", url }` / `{ kind: "error", message }` |

The observer feeds a bounded `Channel<ClimbStep>` (the `OnStep` contract is cheap, non-blocking, thread-safe); the SSE endpoint drains the channel plus the sink in order. The `result` stays on a custom `IScraperSink` because the observer is load-stage and the Markdown is post-extraction.

### 3. Isolation: pooled, one job per VM, recycled (model B)

A normal Fly app, `concurrency soft_limit = 1` per Machine (one hostile job per VM at a time), a fresh browser **context** per job, the browser **process recycled** and killed on a ~45s timeout, `auto_stop_machines` for scale-to-zero. Firecracker VM + per-job recycle + the egress firewall (decision 4) + the gate/caps (decision 6) is the industry-standard posture for running arbitrary web content (Browserless et al. pool; they do not microVM-per-request). It is continuous with the Tier A app already deployed.

**Per-request ephemeral Machine (model A)** (create / run / destroy a VM per job via the Machines API) is the documented production-hardening upgrade if abuse or a Chromium-escape tail risk ever warrants it. Each job is already stateless, so the design does not foreclose it.

### 4. Browser-layer SSRF: in-VM egress firewall + a dedicated org

Phase 1's SSRF defense is a DNS-pinned `SocketsHttpHandler.ConnectCallback` inside `HttpClient`. Once Chromium drives the page it issues its own requests (subresources, redirects, `fetch`/XHR, WebSocket) straight from the OS stack, bypassing that handler. Fly has **no built-in egress firewall** (verified: its network controls are ingress-side, public vs Flycast/6PN; every Machine sits on `fdaa::/8` 6PN with other org apps reachable by default). So:

- **In-VM nftables egress firewall** applied at container start: drop outbound to RFC1918, loopback, `169.254.0.0/16`, `fc00::/7` (covers Fly's `fdaa::/8` ULA), `fe80::/10`, and IPv4-in-IPv6 embeddings. This is the `SsrfPolicy` blocklist (already unit-tested in Phase 1) lifted to the network layer, where it covers every rung including the browser.
- **A dedicated Fly org** for this app, so even a 6PN reach finds nothing sensitive (Tier B moves out of org `personal`).
- The app-layer DNS-pinned guard is no longer needed on the HTTP rung: the engine's default `HttpPageLoadTransport` runs unguarded, but the network-layer firewall contains it along with the browser. (This is why we do **not** promote `HttpPageLoadTransport`'s internal handler-factory ctor to public for Tier B; the network layer is the single control.)

### 5. Browsers baked into the image

Both the vanilla Chromium (via the Playwright base image `mcr.microsoft.com/playwright/dotnet:vN-noble`, which carries Chromium + system deps) and the CloakBrowser binary are baked in. `WithCloakBrowser(AutoInstall: Disabled, ExecutablePath: <baked>)` never downloads at runtime. Run with `--ipc=host` (avoids Chromium OOM on `/dev/shm`) and `--no-sandbox` (the Firecracker VM is the trust boundary), on a ~2 GB RAM Machine. Image lands ~1 GB+, which Fly pulls fine. This is what the license's internal-Docker-use allowance buys; there is no cold-download step.

### 6. Gate + cost (carried from the 2026-05-30 resolution; cost model shifted to browser-minutes)

Unchanged decisions: **email-capture gate** on Tier B (feeds the Stripe-ready waitlist), **Turnstile**, per-IP + global **rate limit** (Upstash, still unprovisioned), a **daily-budget kill switch** that flips the playground to "demo at capacity, sign up". What changed: the $5/day ceiling now meters **browser-machine-time**, not LLM tokens (LLM extraction is deferred, see non-goals), so the kill switch keys off Machine-seconds, and BYO-key rides with the deferred LLM slice (nothing to BYO a key for yet). For dev and staging, the `X-Playground-Secret` gate already protects the endpoint; the email gate + Upstash + budget wire on before the stealth rung goes public.

### 7. Honest-loss UX (already built) + the Tier A dead-end fix

A residual block at the top rung emits `exhausted` and the UI shows the real outcome plus "captcha tier on the roadmap", never a bare error (already shipped in the climb component). This slice also fixes the standing Tier A loose end: `TierAScraper`'s "sign in to run the full climb" message points at a sign-in that does not exist; repoint it at the CLI (`webreaper scrape <url> --stealth`, which does the real climb locally and free, and is the licensed path for an end user on their own machine).

## Repo placement

Extend `cloud/WebReaper.PlaygroundApi/` with a `TierBScraper` (engine + `ChannelClimbObserver` + `MarkdownResultSink`) beside `TierAScraper`. The project gains references to `WebReaper.Cdp`, `WebReaper.Playwright`, and `WebReaper.Stealth.CloakBrowser`. Still not in `WebReaper.sln` (separate deployable; local + Docker build is the gate, as in Phase 1).

## Buildable now (no accounts) vs needs your accounts

- **Now, locally verifiable:** the ADR-0085 seam in core (`IClimbObserver` + `ClimbStep` + `WithClimbObserver` + the four notify sites + `NullClimbObserver`) + unit tests; the `TierBScraper` engine climb + observer-to-Channel-to-SSE + the result sink; the index-to-name mapping; the `Dockerfile` baking Chromium + a real CloakBrowser binary; the nftables egress script; a local Docker run that climbs a known-hard target end to end and curl-verifies the SSE.
- **Needs your accounts / secrets:** the CloakHQ OEM license (before the stealth rung goes public); a dedicated Fly org + a ~2 GB Machine; Upstash (rate limit, still unprovisioned from Phase 1); Cloudflare Turnstile keys; the email-capture store.

## Build order (tracer-bullet, legal-clean rung first)

1. **ADR-0085 seam in core** (`IClimbObserver`, `ClimbStep`, `WithClimbObserver`, four notify sites, `NullClimbObserver`) + unit tests. The contract everything else consumes; additive, no behavior change.
2. **`TierBScraper`, HTTP + vanilla browser rungs only** (no stealth yet, legally clean, bakeable): engine climb, observer-to-Channel-to-SSE, the result sink. Local Docker, curl-verifiable against a JS-rendered SPA (climbs HTTP to browser).
3. **Add the stealth rung** (`WithCloakBrowser` + the baked binary). Behind the `X-Playground-Secret` gate only until the OEM license lands. Verify against a known hard-Cloudflare target.
4. **Isolation + egress on Fly:** in-VM nftables firewall, dedicated org, ~2 GB Machine, `concurrency = 1`, `auto_stop`, per-job timeout + recycle.
5. **Edge gate additions:** email capture + the browser-minutes budget kill switch (Turnstile + Upstash were designed in Phase 1; wire them on and provision Upstash).
6. **Refresh the canned hero** from a real Tier B climb recording (optional; the hero already exists).

## Non-goals (this slice)

- **LLM extraction / `ExtractWithPrompt`** (deferred by default this grill, flip if wanted). Tier B's output is empty-prompt Markdown; the plain-English prompt and its LLM budget + BYO-key are a later additive slice.
- **Per-request ephemeral Machine isolation (model A)** (documented hardening upgrade, not this slice).
- **Accounts / API keys / billing** (Phase 3, WebReaper Cloud proper).
- **Crawl / map in the playground** (scrape-one-URL only).
- **A captcha-solver rung** (the fourth rung above stealth; ADR-0083's residual-block `RunReport` signal is its future integration point).

## Linked

[CLOUD-SCRAPE-PLAYGROUND-PLAN.md](CLOUD-SCRAPE-PLAYGROUND-PLAN.md) · [CLOUD-PLAYGROUND-PHASE-1.md](CLOUD-PLAYGROUND-PHASE-1.md) · [ADR-0083](adr/0083-escalating-page-loader.md) (the climb) · [ADR-0085](adr/0085-climb-progress-observer-seam.md) (the observer seam) · [ADR-0054](adr/0054-stealth-backend-pattern-cloakbrowser.md) (the stealth satellite + its license model)
