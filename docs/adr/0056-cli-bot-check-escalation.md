# Hybrid C bot-check escalation in `webreaper scrape`: conservative post-hoc detector, inline install, single retry

## Status

**Accepted — implementation complete** (2026-05-24). v10.x **Transports
cleanup wave** follow-up to ADR-0055; pins the **concrete detector
heuristic and retry composition** that ADR-0055 §"Hybrid C escalation
UX" named but deliberately did not detail. Lives alongside ADRs
[0057](0057-cdp-network-idle.md) (network-idle event tracking) and
[0058](0058-engine-teardown-disposal.md) (disposal chain) in the same
cleanup wave.

## Context

ADR-0055 set the **UX shape** for the CLI's vanilla→stealth escalation:
a conservative bot-check detector → Y/n prompt → inline
`stealth install --yes` → retry. The CLI substrate to make that flow
runnable shipped in the Transports wave:

- `--browser-cdp-url` flag (BYO endpoint plumbing) — already wired into
  `ScrapeCommand.RunAsync`.
- `webreaper stealth install [<backend>] [--yes]` — installer per
  ADR-0054, picker UX per ADR-0055.
- `webreaper stealth path <backend>` — prints the cached binary path;
  the hook the retry needs to compose its own launch.
- `KnownStealthBackends` registry — name → install/launch metadata; the
  registry is also how the escalation flow picks "what does
  `--auto-stealth` install" (CloakBrowser is the v10 default).

What's *not* in place is the **detector** that decides "this scrape
result looks like a bot-check" and the **retry path** that composes
install + relaunch + re-scrape. The 500-ms-shaped placeholder shipped
nothing: today `webreaper scrape <url> --browser` runs once against
vanilla Chromium, returns whatever it gets, and exits. A user staring
at an empty result on a Cloudflare-blocked URL has no signal that
stealth would unblock them.

The cost of getting the detector wrong is asymmetric: a **false
positive** is an extra Y/n prompt the user dismisses (cost: one keypress);
a **false negative** silently ships empty data the user can't explain.
Conservatism means erring toward the prompt — wide net on the signal
side, user consent on the action side.

## Decision

### The detector — a pure function

`WebReaper.Cli/Stealth/BotCheckDetector.cs`:

```csharp
internal static class BotCheckDetector
{
    public sealed record Verdict(bool LikelyBlocked, string? Reason);

    public static Verdict Detect(
        int? httpStatus,
        string? renderedHtml,
        int recordCount)
    {
        // Signal 1 — challenge-class HTTP status, regardless of body.
        if (httpStatus is 403 or 429 or 503)
            return new(true, $"HTTP {httpStatus} — typical bot-check response code.");

        // Signal 2 — zero records on a non-empty page that carries a known
        // challenge marker. The two-clause AND keeps the false-positive rate
        // bounded: an empty page that *should* be empty (a 200 with no
        // content) wouldn't match; a non-empty page that returned records
        // wouldn't match either.
        if (recordCount == 0 && !string.IsNullOrWhiteSpace(renderedHtml))
        {
            foreach (var marker in ChallengeMarkers)
                if (renderedHtml.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return new(true, $"Detected challenge marker: '{marker}'.");
        }

        return new(false, null);
    }

    // Substring matches against the rendered HTML. Empirical, not
    // exhaustive. The set the wave ships:
    //   • "Just a moment...", "Checking your browser" — Cloudflare Turnstile
    //   • "cf-mitigated", "cf-chl-bypass" — Cloudflare meta/cookie names
    //   • "DataDome", "dd-rd" — DataDome challenge page
    //   • "px-captcha", "_pxhd" — PerimeterX
    //   • "_Incapsula_", "Incapsula incident ID" — Imperva Incapsula
    //   • "Akamai", "ak_bmsc" — Akamai Bot Manager
    // Adding markers is a one-line PR + a new BotCheckDetectorTests row.
    internal static readonly string[] ChallengeMarkers = [/* ... */];
}
```

Pure function, no IO. Trivial to unit-test (`BotCheckDetectorTests`
ships in the same PR with one Theory row per marker + one each for the
HTTP-status arm and the no-signal arm).

### The detector's two inputs come from the engine

The scrape command captures both during the in-process Crawl driver run:

- **`int? httpStatus`** — non-null only when the HTTP transport set it
  on the last loaded page (the CDP transport currently does not surface
  the HTTP status from `Page.loadEventFired`; on the browser path this
  arm is `null` and only Signal 2 fires). v10.x ships the detector with
  whatever the existing transports surface; future widening to a
  per-transport `HttpStatus` capture is a deferred follow-up (CONTEXT.md
  noted; not gated on this ADR).
- **`string? renderedHtml`** — the page document the
  `IContentExtractor` saw. Available on the browser path because the
  CDP transport emits `document.documentElement.outerHTML` as the
  loaded payload; available on the HTTP path because the loader emits
  the response body. Captured via a one-page in-memory sink on the
  detector path (the existing `records` list capture in
  `ScrapeCommand.Emit` already runs).

The integration point is small: after `engine.RunAsync()` returns, the
command computes
`BotCheckDetector.Detect(httpStatus: null, renderedHtml: lastDoc, recordCount: records.Count)`
and dispatches on the verdict.

### Escalation flow — three modes, single retry

#### Default (`webreaper scrape <url> --browser`)

```
$ webreaper scrape https://example.com --browser
↓  Spawning managed Chromium...  ✓
↓  Loaded https://example.com (0 records)
⚠  Page returned no records and contains Cloudflare challenge markers ("Just a moment...").
?  Download CloakBrowser stealth backend (~220 MB) and retry? [Y/n] y
↓  Installing CloakBrowser via `webreaper stealth install cloakbrowser --yes`...  ✓
↓  Retrying scrape against the stealth backend...  ✓
✓  Scraped 1247 records
```

Detector verdict `LikelyBlocked: true` → emit warning to stderr →
prompt `Y/n` (default Y) → if yes: shell out to
`webreaper stealth install cloakbrowser --yes` as a subprocess (NOT a
direct call into `StealthCommand.RunAsync` — see Considered options),
then re-run the engine with the stealth binary path resolved via
`webreaper stealth path cloakbrowser`. Single retry, no recursion: if
the stealth retry also returns a `LikelyBlocked` verdict, exit with
code 1 and a "captcha-solver wave deferred" pointer.

#### Power-user (`--browser --stealth`)

Skip the vanilla attempt entirely. Resolve the stealth binary path on
first call; if not installed, fall straight to the prompt + install
flow (the install prompt fires immediately rather than after a wasted
vanilla scrape). One scrape, no retry.

#### Unattended (`--browser --stealth --auto-stealth` or `WEBREAPER_AUTO_STEALTH=1`)

Y/n prompts bypassed. The CI path. The install runs without
interaction; the scrape proceeds. Same single-retry semantics as
default; same exit code 1 on a second `LikelyBlocked` verdict.

#### Opt-out (`--no-auto-stealth`)

The detector still runs but its verdict is reduced to a stderr warning
("a bot-check was likely detected; pass `--stealth` to retry with
CloakBrowser"). No prompt, no install, no retry. The escape hatch for
users who hit a false-positive heuristic and want the warning silenced
on a future run via `2>/dev/null`.

### Subprocess vs in-process for the install

The retry shells out to `webreaper stealth install cloakbrowser --yes`
as a subprocess (the same binary, re-invoked with different args).
Rejected alternative: calling `StealthCommand.InstallAsync(args)`
directly from the scrape command.

**Why subprocess:** the install's UX is already self-contained and
tested; re-invoking the binary is exactly the contract the user sees
documented in `--help`. A user can run that same command by hand and
get the same result. Direct in-process call would couple
`ScrapeCommand` to the internals of `StealthCommand`'s output stream
and progress reporting; subprocess preserves the substitutability.

**Cost:** subprocess startup latency (a few hundred ms on first AOT
binary launch). Negligible against the 30-second install download.
Also: the subprocess inherits the controlling TTY, so the install's
progress lines render correctly even when the parent's stdout is
piped (a `webreaper scrape ... | jq .` pipeline doesn't muddy the
install's ↓ / ✓ markers).

### Retry composition

After a successful install, the retry path resolves the stealth binary
path via `webreaper stealth path cloakbrowser` (subprocess, captures
stdout) and re-runs the engine. The retry uses the same builder shape
as the first attempt, **with one difference**: a launch-and-connect to
the stealth binary via a new `CdpLaunchOptions { ExecutablePath = ... }`
overload of `WithCdpPageLoader`, rather than the default `null` path
that finds vanilla Chromium.

The engine's `IAsyncDisposable` chain (ADR-0058) guarantees the first
vanilla Chromium spawn is torn down before the stealth retry's
spawn — no two-Chromiums-running window.

## Considered options

- **Conservative detector + Hybrid C UX + subprocess install (chosen).**
  Convergent answer from ADR-0055's named UX + the asymmetric-cost
  analysis above. The detector's two-clause shape is the smallest
  thing that catches the named challenges; the subprocess install
  preserves the substitutability of `webreaper stealth install`.
- **Aggressive detector — flag *any* zero-records result on `--browser`
  as a potential block (rejected).** Maximises true positives,
  catastrophises false positives: a legitimately empty page (a
  not-found, an empty search result) becomes a 220 MB install prompt
  every time. Killed.
- **Just-the-status-code detector — drop the marker arm (rejected).**
  Half the named challenges respond with 200 + a challenge page
  (Cloudflare Turnstile, DataDome's "interstitial" mode). Status-only
  misses the most common case. Marker arm earns its keep.
- **Fingerprint the page via the `MarkdownContentExtractor` (rejected).**
  Markdown-extracted body is *post*-cleanup — the challenge page's
  iframe / inline script markers don't survive the extraction. Detector
  needs the raw `renderedHtml`.
- **In-process call into `StealthCommand.InstallAsync` (rejected, see
  above).** Couples the two commands; loses the substitutability
  guarantee.
- **Multi-retry with progressive escalation (vanilla → stealth →
  stealth+residential-proxy → captcha-solver) (rejected; deferred).**
  Single retry is the v10.x shape; multi-retry composition lives in
  the deferred captcha-solver wave (ADR-0055 F5 deferral). The same
  `BotCheckDetector` is the substrate either way.
- **Detector as an interface seam (`IBotCheckDetector`) (rejected,
  premature).** One implementation, no demonstrated need for a second.
  The pure-static-function shape is testable without an interface
  abstraction; the v10.x cleanup wave keeps it.

## Accepted cost

- **The browser path of the detector runs without `httpStatus`.** The
  CDP transport currently extracts no HTTP-response-code surface from
  `Page.loadEventFired`. Signal 1 fires only on the HTTP transport
  (where the loader already has the response code). For
  `--browser` scrapes — the most common case — only Signal 2
  (zero-records + marker substring) is active. Acceptable v10.x; the
  HTTP-status surface is a deferred follow-up (`CdpClient` would need
  to track `Network.responseReceived` per navigation and surface the
  main-document response code on the transport's return).
- **False positives surface as an extra prompt.** A legitimately
  empty-but-marker-containing page (e.g. a search-results page that
  happens to mention "DataDome" in its content) prompts. User says
  `n`; scrape concludes; one keypress paid. Documented in `--help`;
  `--no-auto-stealth` disables.
- **Subprocess `webreaper stealth install` requires the binary to find
  itself.** `Environment.ProcessPath` resolves the running binary on
  every platform we ship (`linux-x64`/`linux-arm64`/`osx-x64`/
  `osx-arm64`/`win-x64`/`win-arm64`); `dotnet test` runs the unit
  tests against a stubbed launcher so the test suite never shells out
  the actual install.
- **Single-retry contract is fixed.** A site that defeats both vanilla
  *and* CloakBrowser exits with `1` and a captcha-solver pointer. No
  third-rung in v10; arguably the most common compound-protection
  case (Cloudflare + hCaptcha) is unsolvable without that wave.

## Deliberate consequences

- **The `webreaper scrape ... --browser` UX walks end-to-end.** A new
  user installing the CLI, running it against a Cloudflare-protected
  tracker, sees the prompt, accepts, scrapes — no Python sidecar, no
  manual stealth-fork setup, all WebReaper-native. The audiobook
  scenario from ADR-0055's HITL Round 1 actually completes on a real
  protected URL after this ADR lands.
- **The detector becomes the seam every future stealth backend
  composes onto.** Adding Patchright (ADR-0054 recipe) to
  `KnownStealthBackends` makes it available as the
  `--auto-stealth` target without changes here; the detector
  decides "blocked", the registry decides "which backend to install".
  Decoupling pays as the registry grows.
- **The captcha-solver wave has a documented integration point.** The
  single-retry exit-code-1 path is exactly where a future captcha
  retry would slot in — the third rung of escalation. Deferred from
  v10 per ADR-0055 §F5; the ADR exists to flag the slot.

## SemVer

**Patch / minor (additive behaviour).** The change is purely additive
to `webreaper scrape`'s behaviour: the same flag now does more on
detection. New flags (`--no-auto-stealth`) are additive. No CLI
contract regression. v10.0.0's major is owned by ADR-0053; this rides
the v10.x cleanup wave as a minor.

## v2 deferrals (named so they don't drift)

- **HTTP-status surface from the CDP transport.** Wire
  `Network.responseReceived` per navigation; surface the main-document
  response code on the transport return; widens Signal 1 to cover the
  browser path. Mechanical work; deferred to keep this ADR scoped.
- **Marker registry as data, not code.** The substring list is hardcoded
  in `BotCheckDetector.ChallengeMarkers`. A future ADR may extract it
  to a `~/.webreaper/bot-markers.json` or a NuGet-shipped data file
  with curated updates. Not earning rent in v10.x.
- **Multi-retry / progressive escalation.** The captcha-solver wave
  (F5 from ADR-0055 grilling) — third rung after stealth.
- **Detector telemetry / metric for false-positive rate.** Would need
  an opt-in counter. Out of scope for v10.x's local-CLI shape.

## Amendment (2026-09-25): the install subprocess does not inherit the TTY (#264)

"Subprocess vs in-process for the install" says the subprocess "inherits
the controlling TTY", so a piped scrape stays clean. It does not: a child
started without redirection inherits the parent's stdout file descriptor,
which is the pipe or file itself. `webreaper scrape <url> --stealth >
out.json` wrote the install's `↓ Downloading ...` line into `out.json`
ahead of the records (reproduced on 11.3.1). The scrape now redirects the
child's stdout and forwards it, line by line, to its own stderr; the
child's stderr stays inherited. `webreaper stealth install` typed by hand
is unchanged, so the substitutability argument stands.
