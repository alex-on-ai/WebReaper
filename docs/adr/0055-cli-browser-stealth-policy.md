# `WebReaper.Cli` browser/stealth acquisition policy: layered auto-spawn, Hybrid C escalation UX, curated backend registry, AOT invariant preserved

## Status

**Accepted — implementation complete** (2026-05-24). Slice 4 of 4 in the **Transports wave** (v10.0.0
target). The CLI's documented browser story. Pins the policy decisions
the rest of the wave (ADR-0052..0054) imply but don't own.

## Context

ADR-0043 established `WebReaper.Cli` as a Native-AOT single binary
published across six RIDs (`linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64`, `win-x64`, `win-arm64`) via `release.yml`. The AOT
constraint is load-bearing: a single self-contained binary the user
downloads from a GitHub release and runs without a .NET runtime install.

Before this wave landed in v10, the CLI's relationship to browser-mode page
loading was implicit: the CLI referenced core only, and Dynamic page loading
threw the core's `BrowserNotConfiguredPageLoadTransport` error directing users
to add the `WebReaper.Puppeteer` satellite — which an AOT-distributed CLI
cannot bake (PuppeteerSharp is not AOT-clean). The CLI's `--browser` flag
existed at the surface but had no working backing transport.

The Transports wave gives the CLI three new options to choose between
(ADR-0052 `WebReaper.Cdp`, ADR-0053 `WebReaper.Playwright`, ADR-0054
`WebReaper.Stealth.X`). Picking one without a documented policy guarantees
drift; a contributor's next CLI feature would silently bake whichever
satellite was convenient. This ADR pins the picks before drift starts:

1. The CLI bakes **only** `WebReaper.Cdp` for browser support — the
   AOT-clean transport (ADR-0052's bedrock).
2. Browser binaries (vanilla Chromium *and* stealth Chromium forks) are
   acquired through documented CLI subcommands (`browser install`,
   `stealth install`); never bundled.
3. Stealth-backend selection on the CLI surface goes through a curated
   `KnownStealthBackends` static registry — community-contributed
   satellites get CLI integration via a small PR.
4. The user-facing escalation flow (vanilla → stealth) is the Hybrid C
   shape from HITL Round 1: auto-escalate by default with a Y/n prompt;
   power-user `--stealth` flag skips the vanilla attempt; unattended
   `--auto-stealth` / `WEBREAPER_AUTO_STEALTH=1` for CI.

## Decision

### What the CLI bakes

```
WebReaper.Cli
└── PackageReference Include="WebReaper.Cdp"
```

That single browser-related dependency. **`Microsoft.Playwright` is never
baked**; nor is `PuppeteerSharp` (already deleted in ADR-0053); nor is
any `WebReaper.Stealth.X` satellite — those ship for library use only.
The AOT smoke test (`WebReaper.AotSmokeTest`) extends to publish the CLI
binary with `WebReaper.Cdp` linked, asserting zero IL/AOT warnings — a CI
guardrail that fails the build if a contributor adds an AOT-unfriendly
reference to the CLI in a future PR.

### Layered auto-spawn — three rungs

When the CLI needs a browser endpoint (any command path that involves
dynamic page loading), it walks three rungs in order:

1. **BYO** — if `--browser-cdp-url <url>` is passed, the CLI uses it
   directly via `WithCdpPageLoader(url)`. No spawn, no detection. The
   power-user / sidecar / stealth path. The user owns lifecycle.

2. **System detection** — `CdpLaunchHelpers.FindOnPath(...)` searches for
   `google-chrome`, `chromium`, `chrome`, `microsoft-edge`, `msedge` on
   PATH and platform-conventional install locations
   (`/Applications/Google Chrome.app/...`, `C:\Program Files\Google\Chrome\...`).
   If found, the CLI spawns it with `--remote-debugging-port=0 --headless=new`
   and connects via `WithCdpPageLoader(CdpLaunchOptions)` (ADR-0052's
   launch-and-connect overload). Lifecycle owned by the CLI; teardown on
   process exit.

3. **Managed** — if no system browser is detected, the CLI fails with an
   actionable message pointing at `webreaper browser install`. The user
   runs the install command; subsequent invocations find the managed
   binary in `~/.webreaper/browsers/chromium-<version>/` and treat it as a
   "system" detection (rung 2 succeeds against the cached path).

The order matters: BYO first means stealth scenarios (which always come
in as BYO from `webreaper stealth install` then `--browser-cdp-url`)
take priority over any system browser; system detection second avoids
gratuitous downloads when Chrome is already there; managed last is the
fallback for clean machines (CI, fresh dev box).

### `webreaper browser install` — vanilla Chromium acquisition

```
$ webreaper browser install
ℹ  Downloading Chromium 146 for darwin-arm64 (152 MB) from Microsoft Playwright CDN...
✓  Installed to ~/.webreaper/browsers/chromium-146/
```

**Source: Microsoft's Playwright CDN.** The CDN hosts Chromium builds
under `https://playwright.azureedge.net/builds/chromium/...`,
versioned, signed, with checksums in the release manifest. Used by
`playwright install` worldwide; high availability; ungated by API key.
Mirrors are documented in the CLI's README for users in regions where
the CDN is slow. The alternative (Chromium's own
`commondatastorage.googleapis.com/chromium-browser-snapshots/` server)
hosts every snapshot ever built — millions of revisions; navigation by
build number is painful for a one-off-install UX. Playwright's CDN is the
curated subset that matters.

**Subcommand surface:**
- `webreaper browser install` — install the latest stable known version
- `webreaper browser install --version 146` — pin
- `webreaper browser list` — show installed cached versions
- `webreaper browser uninstall <version>` — remove a cached version
- `webreaper browser path` — print the cached binary path (for scripting)

### `webreaper stealth install` — stealth-fork acquisition

```
$ webreaper stealth install
ℹ  Available stealth backends (downloaded from upstream; not bundled):
   [1] CloakBrowser   v0.3.30   220 MB   58 fingerprint patches; recommended
       License: https://github.com/CloakHQ/CloakBrowser/blob/main/BINARY-LICENSE.md
   Choice [1]: ↵
?  By using CloakBrowser you accept its binary license. Proceed? [Y/n] y
↓  Downloading CloakBrowser v0.3.30 from CloakHQ GitHub releases... ✓
✓  Installed to ~/.webreaper/stealth/cloakbrowser/0.3.30/
```

**The `KnownStealthBackends` registry** is the curated list. AOT-friendly
static data in `WebReaper.Cli/Stealth/KnownStealthBackends.cs`:

```csharp
public static class KnownStealthBackends {
    public static readonly StealthBackend[] All = [
        new("cloakbrowser",
            displayName: "CloakBrowser",
            recommendedVersion: "0.3.30",
            sizeBytes: 220 * 1024 * 1024,
            description: "58 fingerprint patches; recommended",
            licenseUrl: "https://github.com/CloakHQ/CloakBrowser/blob/main/BINARY-LICENSE.md",
            releaseManifestUrl: "https://api.github.com/repos/CloakHQ/CloakBrowser/releases/...",
            launchSpec: CloakBrowserLaunchSpec.Default),
        // Future: Patchright, Camoufox, … added via PR
    ];
}
```

Adding a backend to the CLI's surface is one PR per backend; the library
satellite ships independently. The registry's job is the *picker UX* and
the *unattended-CI defaults* — not the launch logic (which lives in the
satellite the CLI does not bake; the CLI re-implements a minimal launcher
per registered backend using `CdpLaunchHelpers` directly).

**Subcommand surface:**
- `webreaper stealth install` — interactive picker + Y/n license prompt
- `webreaper stealth install cloakbrowser --yes` — unattended, name-pinned
- `webreaper stealth list` — show installed stealth backends + versions
- `webreaper stealth uninstall cloakbrowser` — remove cached install
- `webreaper stealth path cloakbrowser` — print binary path

### Hybrid C escalation UX — the scrape flow

The `webreaper scrape` command implements the three-rung escalation that
emerged from HITL Round 1:

```
$ webreaper scrape <url> --browser
↓  Spawning managed Chromium...  ✓
⚠  Page returned no records; last response looks like a bot-check (Cloudflare).
?  Download CloakBrowser stealth backend (220 MB) and retry? [Y/n] y
↓  Running 'webreaper stealth install cloakbrowser --yes' inline... ✓
↓  Retrying with CloakBrowser... ✓
✓  Scraped 1247 records
```

**Three modes:**
- **Default (`--browser`):** rung 1-3 escalation. Auto-prompt on detected
  bot-check; user accepts → inline install → retry. Detection is a
  conservative heuristic: HTTP 403/429/503 from the loader OR
  zero-records-extracted on a non-empty page that contains
  Cloudflare/DataDome/PerimeterX challenge markers. False positives are
  cheap (extra prompt, user says no, scrape continues with vanilla).
- **Power-user (`--browser --stealth`):** skip the vanilla attempt, go
  straight to stealth on request 1. Requires the stealth backend
  pre-installed (or the prompt fires immediately).
- **Unattended (`--browser --stealth --auto-stealth` or
  `WEBREAPER_AUTO_STEALTH=1`):** Y/n prompts are bypassed; the CI
  path. Same install + retry flow, no prompts.

### AOT invariant — the guardrail

The wave introduces no AOT-unfriendly dependency to the CLI. Two
CI checks:

1. **`WebReaper.AotSmokeTest` extended.** A new smoke step publishes
   `WebReaper.Cli` with `PublishAot=true` across all six RIDs and
   asserts zero IL/AOT warnings. The existing smoke test publishes a
   library consumer; this one publishes the actual CLI as a binary. Run
   per PR; a regression that pulls Microsoft.Playwright or
   PuppeteerSharp into the CLI graph fails the build immediately.
2. **The CLI's `.csproj` is the gate.** Reviewers see additions to
   `<PackageReference>` in PRs touching `WebReaper.Cli/*.csproj` and can
   block additions of AOT-hostile satellites. The pattern is the same as
   `WebReaper`'s own `.csproj` — `WarningsAsErrors=CS1591` (ADR-0023)
   applied to AOT warnings via the smoke test.

The invariant is also written down in this ADR (the part of the ADR
discipline that future contributors actually grep for): *the CLI bakes
only `WebReaper.Cdp` for browser support. Adding Microsoft.Playwright or
any `WebReaper.Stealth.X` satellite to the CLI's references regresses
the AOT guarantee and must be rejected; the install-from-upstream
pattern is the alternative.*

## Considered options

- **Layered auto-spawn + Hybrid C + curated registry, AOT preserved
  (chosen).** Convergent answer from the HITL forks: F3 picked layered
  auto-spawn, F4 picked per-backend satellites, the stealth-UX redesign
  picked Hybrid C, F8 folded the registry into this ADR. The shape is
  the resolution of those forks.
- **Pure BYO — CLI requires `--browser-cdp-url` always (rejected).** Was
  one of three F3 options. Cleanest possible CLI; biggest friction. The
  casual user with Chrome installed has to know the CDP launch incantation
  before they can scrape a JS-rendered page. Killed the casual scenario.
- **Auto-spawn only — CLI always finds-or-downloads its own Chromium, no
  BYO path (rejected).** Forecloses pointing at a stealth Chromium fork
  from the CLI surface. Forecloses CI scenarios where the user runs a
  proxied browser farm. F3-rejected on convergent reasons.
- **Two CLI binaries: AOT (no browser) + JIT (with Playwright)
  (rejected).** Doubles RID count to 12; sheds ADR-0043's single-binary
  guarantee; bifurcates the user-install story. F3-rejected.
- **Hardcoded list with no registry abstraction (effectively chosen, but
  the *registry abstraction* itself was reconsidered).** The
  `KnownStealthBackends` static array is the simplest "registry" the
  AOT-CLI can carry. F8 considered a dynamic manifest convention
  (`~/.webreaper/stealth-backends/*.json` scanned at runtime) but the
  AOT constraint plus NuGet's lack of post-install hooks killed
  dynamic discovery; the curated list is the working answer.
- **`webreaper stealth install` auto-running on first `--browser`
  bot-check detection without prompt (rejected).** Implicit 220 MB
  download is intrusive; Hybrid C's Y/n prompt was explicitly chosen for
  the user-consent surface. `--auto-stealth` is the explicit opt-out for
  unattended use; the default stays prompting.

## Accepted cost

- **Community stealth-backend integration with CLI requires a PR.** The
  satellite ships freely on NuGet; CLI's `KnownStealthBackends` entry +
  the minimal launcher need a small PR. Trade for the curated-quality
  bar (no surprise stealth backends in the CLI's `--stealth` picker)
  and the AOT-safe static registry shape.
- **The CLI re-implements minimal launcher logic per backend.** Because
  the CLI can't reference `WebReaper.Stealth.X` (AOT), each entry in
  `KnownStealthBackends` carries the fork-specific launch flags + binary
  layout the CLI needs. Duplication with the satellite; cheap to keep in
  sync (one tested place + one tested place is still better than one
  AOT-hostile dependency).
- **Bot-check heuristic for auto-escalation has false positives.** The
  HTTP-status + page-content-marker detector misfires on legitimately
  empty pages and on sites that return 403 for non-bot reasons. False
  positives surface as an extra prompt the user dismisses with `n`; the
  scrape continues. Documented in `--help`; tunable via
  `--no-auto-stealth` to disable the heuristic entirely.
- **Subcommand surface grows from one verb (`scrape`) to four (`scrape`,
  `map`, `init`, `browser`, `stealth`).** Already five with ADR-0043's
  `init`. This ADR adds two more verb roots. The CLI's surface area is
  still small (< 20 leaf subcommands); growth is on-pattern.

## Deliberate consequences

- **The CLI gets a coherent browser story end-to-end.** A new user
  installs the CLI, runs `webreaper scrape <url>`, hits the
  Cloudflare-blocked tracker, prompts to download CloakBrowser, runs it,
  ships data to MongoDB — all WebReaper-native, no Python, no manual
  sidecar. The audiobook scenario from HITL Round 1 walks end-to-end.
- **AOT-as-policy, not aspiration.** The CLI's AOT publishability is a
  smoke-tested CI invariant; ADR-0009's "AOT-clean is a core guarantee"
  extends concretely to the CLI surface. Future "let's bake Playwright
  into the CLI" proposals have this ADR to push back against.
- **Stealth-backend installer is a documented capability surface.**
  Future captcha-solver wave (deferred from F5) can layer onto the same
  install pattern (`webreaper captcha install <service>`); the precedent
  this ADR sets is the template.

## SemVer

**Minor (additive).** New CLI subcommands; no break to existing surface.
`webreaper scrape ... --browser` *behaviour* changes (previously threw,
now works via the auto-spawn path) — that's a bug-fix-shaped behaviour
delta, not a contract break. v10.0.0's major is owned by ADR-0053.

## v2 deferrals (named so they don't drift into v10)

- **Captcha-solver install pattern** (`webreaper captcha install`) — F5
  from Round 1, deferred to post-v10 follow-up wave.
- **Library-level transport escalation** (`TransportRouter`, the
  proposer-validator dock applied to transports) — F7 from Round 1,
  deferred to a follow-up wave pending demand.
- **CLI auto-update for cached browser/stealth binaries** — a separate UX
  question (background updater? manual? periodic check?); not in v10.
- **Mirror-CDN config for `webreaper browser install`** beyond the
  README documentation — first-class CLI flag (`--cdn-base-url`)
  deferred until users in restricted regions report blocking.
- **Multi-platform CloakBrowser RID coverage** — depends on what
  CloakBrowser publishes. v10 satellite covers the RIDs the upstream
  ships; gaps documented in the satellite README.

## Amendment (2026-09-25): per-platform CloakBrowser builds + existing-install detection (#264)

The registry sketch above (one `recommendedVersion`, a
`v{version}/cloakbrowser-{rid}` URL) stopped matching upstream.
CloakBrowser now tags releases `chromium-v<build>`, removed the v0.3.x
assets, and does not publish every platform in every release. Every
`webreaper stealth install cloakbrowser` 404'd, so `--stealth` always fell
back to the browser tier (#264). The library satellite was fixed in PR
#220; the CLI was not, because it re-implements the installer (the
"Accepted cost" above). What the CLI does now:

- **One pinned build per RID.** A `KnownStealthBackends` row carries a
  `StealthBuild` per platform (version, release asset, executable path
  inside the archive, size), mirroring the per-platform pins the vendor's
  own npm / pip / NuGet wrappers ship: linux-x64 and win-x64 at
  146.0.7680.177.5, linux-arm64 at 146.0.7680.177.3, osx-arm64 and
  osx-x64 at 145.0.7632.109.2. That closes the "Multi-platform
  CloakBrowser RID coverage" deferral for the CLI (macOS and linux-arm64
  install). A RID with no row (win-arm64, 32-bit) fails fast, naming the
  published platforms and the two ways out (`CLOAKBROWSER_BINARY_PATH`,
  `--browser-cdp-url`). `--version` pins another build for the same asset.
- **Verified install.** The release's `SHA256SUMS` is fetched first, so a
  release that lacks this platform fails before the 150 to 560 MB
  download. The archive is hashed while it streams to disk and must match,
  then it is unpacked with BCL APIs (`System.Formats.Tar`,
  `System.IO.Compression`; AOT-clean, no external `tar`) into a scratch
  directory that is renamed into place only once complete.
- **Existing installs first.** `install` is idempotent and `path` reuses
  what is already there, in order: the vendor's `CLOAKBROWSER_BINARY_PATH`
  override (naming a missing file is an error, never a fall-through); the
  CLI cache `~/.webreaper/stealth/cloakbrowser/<build>/`; then the vendor
  wrapper's cache (`CLOAKBROWSER_CACHE_DIR`, else `~/.cloakbrowser/`,
  laid out `chromium-<build>/`; the newest plain build wins, Pro builds
  are skipped). PATH is deliberately not searched: the npm wrapper puts a
  Node CLI named `cloakbrowser` there, not the browser.
- **The row's launch flags are passed.** `LaunchArgs` was never handed to
  the stealth rung; it now is, and it gains `--use-mock-keychain`.
  Without that switch the macOS build stalls at startup on the login
  keychain and never publishes its CDP endpoint (no endpoint after 15 s
  without it, 2 s with it, on osx-arm64).

Not done: verifying `SHA256SUMS.sig`, the Ed25519 signature the vendor
wrappers check. .NET 10 has no standalone Ed25519 API and the CLI takes no
crypto dependency, so the checksum guards against a corrupt download, not
a compromised release origin.
