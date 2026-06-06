# 0082. Self-update: an update notifier now, a guarded `update` command later

**Status:** Accepted (design pass 2026-05-30). Designed, not yet built; the notifier targets a near-term minor.
**Date:** 2026-05-30
**Deciders:** Alex (HITL), Claude (design pass)

## Context

The `webreaper` CLI has no in-tool upgrade path. A user who installed it weeks ago has no signal that a newer version exists and, depending on how they installed it, no obvious command to upgrade. That costs the funnel (people stay on stale versions and hit already-fixed bugs) and costs support ("is this fixed?" when it was fixed two releases ago). Firecrawl and the polished CLIs (gh, rustup, deno) all solve this; WebReaper should too.

Three facts shape the design.

1. **The self-update flow already exists, in shell.** `scripts/install.sh` (ADR-0070) performs exactly what a self-update would: resolve the latest tag from `api.github.com/repos/alex-on-ai/WebReaper/releases/latest`, download `webreaper-<version>-<rid>.{tar.gz,zip}` from `releases/download`, verify it against the published `SHA256SUMS`, then install atomically (stage next to the target, then `mv -f`). It already supports `--upgrade` (overwrite only if strictly newer) and an idempotency check that shells out to `webreaper version`. The artifact-naming contract and the download/verify/swap sequence are proven; a `webreaper update` command is that flow in C#, or the binary re-running install.sh with `--upgrade`.

2. **The install channels have different, conflicting upgrade stories** (ADR-0070). Homebrew (`alex-on-ai/homebrew-webreaper`) owns its binary under the Cellar and upgrades via `brew upgrade`; a self-replace that overwrites that file desyncs brew's metadata, so the next `brew upgrade` clobbers or conflicts. winget is the same on Windows. Only the `curl | sh` install.sh users and the raw GitHub-release downloaders (including Windows manual) have *no* package manager to upgrade them. So self-replace is wrong for some channels and the only option for others.

3. **The CLI's stdout is the payload.** `scrape` / `crawl` / `map` write JSON Lines or Markdown that agents, `>` redirects, and shell pipelines consume. Anything the tool says about updates must go to **stderr** and must be silent in non-interactive, piped, and CI contexts, or it corrupts that payload.

Two supporting facts make self-replace tractable when it is wanted. First, the release binaries are codesigned and notarized but **not stapled** (ADR-0071's amendment: `xcrun stapler` exits 66 on a raw Mach-O, so the shipping pipeline is codesign + notarize only). Gatekeeper clears a notarized binary via an online notary lookup at first launch, and a self-update downloads over HTTP (not a LaunchServices download, so no quarantine attribute is applied) while inherently having connectivity at the moment of update, so macOS is unobstructed. Second, the CLI is a single Native-AOT binary, so swapping it is a one-file atomic rename with no dependency directory to reconcile.

## Decision

Two parts, deliberately sequenced.

### Part 1 (build now): an update notifier

After a data command (`scrape` / `crawl` / `map`) completes, best-effort check the latest released tag, compare it to the running version, and if a newer one exists print **one stderr line**:

```
webreaper 10.3.0 is available (you have 10.2.0). Upgrade: brew upgrade webreaper
```

It never touches the binary. It is gated hard:

- **Interactive only.** Runs only when stderr is a TTY, not under CI (respect the `CI` env var), and not disabled by `WEBREAPER_NO_UPDATE_CHECK` (also honor the de-facto `NO_UPDATE_NOTIFIER`). Agents and pipelines see nothing.
- **Never on stdout, never blocking, never fatal.** All errors (offline, rate-limited, parse failure) are swallowed; the command's own exit code and output are unaffected. The check runs inline after the command's real work, with a short timeout (~1.5s). A detached background refresh (the npm `update-notifier` model) is the escape hatch if this once-a-day pause ever proves annoying; v1 stays inline because `gh`, the closest single-binary analog, does the same.
- **Throttled.** Cache the last-check timestamp and the latest-known tag in `~/.webreaper/update-check.json` (the existing `~/.webreaper/` home). Hit the network at most once per 24h; between checks the cached tag drives the message with no network call.
- **Channel-aware hint.** If the running binary's path is inside a Homebrew or winget prefix, the hint is `brew upgrade webreaper` / `winget upgrade`; otherwise it is the install.sh one-liner (or `webreaper update` once Part 2 ships).
- **Read-only, no telemetry, no consent gate.** The check is a plain `GET` to GitHub's public `releases/latest` (a host the user already hit to install); it sends no machine ID, usage, or analytics payload. So there is no first-run consent notice (the gh / npm `update-notifier` model, not Homebrew's analytics-notice model). It is documented in the README and `--help`, and the disable hint (`WEBREAPER_NO_UPDATE_CHECK=1`) travels in the notifier's own message.

### Part 2 (guarded follow-up): a `webreaper update` self-replace command

Reuses the ADR-0070 artifact contract: resolve the latest (or a pinned) tag, download the current-RID asset, verify it against `SHA256SUMS`, and atomically swap `Environment.ProcessPath`. Either as a C# port of install.sh's flow (reusing the browser/stealth-install HTTP-download helpers) or by shelling out to install.sh `--upgrade` on macOS/Linux (cheaper, reuses the exact tested path; the CLI already self-invokes as a subprocess for stealth install per ADR-0056). Four guards make it safe:

- **Refuse under a package manager (the load-bearing guard).** If the binary path is inside a Homebrew prefix (`brew --prefix` / a `Cellar` path) or a winget/scoop location, do not self-replace. Print "installed via Homebrew, run `brew upgrade webreaper`" and exit non-zero. When the install method is ambiguous, err toward refusing.
- **Permissions.** If the target is not writable (root-owned `/usr/local/bin`), fail clean with guidance (re-run with elevation, or reinstall under `~/.local/bin`); never half-write.
- **Windows atomic replace.** A running `.exe` cannot be overwritten: rename self to `.old`, move the new binary into place, delete `.old` on the next launch. Linux/macOS use the install.sh temp-then-rename pattern.
- **Integrity verification (SHA256, the install.sh trust model).** Verify the download against `SHA256SUMS`, exactly as install.sh does (ADR-0070): the manifest is generated in the same CI job as the binary, so a tampered binary fails verification unless the attacker also rewrites the sums, which is the residual risk ADR-0070 already accepts for `curl | sh`. Signatures (minisign or cosign over `SHA256SUMS`) are **deferred on the same trigger ADR-0070(g) set** ("add when the binary acquires enough distribution that signature gives a meaningful trust signal"), not a prerequisite for shipping Part 2. The one reason to revisit signatures sooner for self-update than for install.sh: self-update is recurring and automatic (the user is not re-reading an auditable script each time), so it is a slightly higher-trust action even though the cryptographic guarantee is identical.

**Why split.** The notifier is small, channel-agnostic, safe, and funnel-positive, so it ships first. The self-replace command helps only the non-package-manager users and carries the brew-desync, Windows, and permissions complexity, so it is a deliberate second step gated on real demand from those users.

## Considered options

- **(a) Unguarded on-by-default self-update.** The canonical CLI-self-update footgun: it desyncs Homebrew, the primary macOS/Linux channel. Rejected; self-replace must detect and refuse under a package manager.
- **(b) Silent auto-update (swap the binary in the background, no prompt).** A supply-chain and agent-trust hazard: an agent or CI step invoking `webreaper` must get a deterministic binary, never a surprise swap mid-pipeline. Rejected; updates are always explicit (the user runs `update` or `brew upgrade`).
- **(c) Notifier on stdout.** Corrupts the JSON Lines / Markdown payload downstream consumers parse. Rejected; stderr-only and TTY-gated.
- **(d) Package-manager-only, no notifier.** Leaves `curl | sh` and raw-download users with no upgrade signal at all. Rejected; the notifier is the cheap win that covers every channel.
- **(e) Notifier on even in CI / pipes.** Log noise and stderr-mixing risk; agents do not need it. Rejected; auto-off unless interactive.
- **(f) A separate bundled updater binary (Squirrel-style).** Over-built for a single AOT file; the install.sh flow plus an atomic rename suffices. Rejected.

## Consequences

Good:
- Every channel gets a correct upgrade story (notifier hint plus, for non-PM users, `webreaper update`). Stale-version support load drops and the funnel improves.
- Little new surface: the notifier reuses the `releases/latest` call install.sh already depends on; `webreaper update` reuses the ADR-0070 artifact contract and the existing browser/stealth-install download helpers.

Costs:
- The notifier's correctness work is entirely in the gating (TTY, CI, env, throttle, timeout, stderr-only). Gated wrong, it becomes the thing that breaks someone's pipe or spams a log.
- `webreaper update` owns the package-manager-detection heuristic (a path-prefix match that can be wrong on unusual layouts, so it errs toward refusing), the Windows rename dance, and permission handling. Signatures stay deferred per ADR-0070(g), with a note to revisit them sooner for self-update than for install.sh.

Reconciles ADR-0070 (the distribution channels and the install.sh flow this builds on) and ADR-0071 (codesign plus notarize, and the online notary lookup that lets a self-downloaded macOS binary launch without stapling). This is a CLI-distribution concern, sibling to ADR-0070 / ADR-0071; it touches no crawl-engine concept and adds no term to CONTEXT.md.

## Implementation sketch (not yet built)

- **Notifier:** an `UpdateNotifier` in `WebReaper.Cli` (check + 24h cache file + gate + one stderr line), invoked from `Program.cs` after a successful `scrape` / `crawl` / `map` (deliberately not `version`, whose stdout stays the bare version string install.sh parses with `awk '{print $NF}'`). AOT-clean (System.Text.Json over the cache file and the `releases/latest` JSON, no reflection). Around 80 lines.
- **`webreaper update`:** a new `UpdateCommand` reusing the existing HTTP-download helpers and the install.sh artifact naming; an install-method-detection helper that matches the binary's realpath against known package-manager prefixes (`/opt/homebrew/`, `/usr/local/Cellar/`, `/home/linuxbrew/.linuxbrew/`, winget's `WinGet\Packages\`, scoop's `scoop\`) and refuses to self-replace when managed or ambiguous (no shelling to `brew --prefix`, AOT-clean); the per-OS atomic swap.
- **Tests:** notifier gating (TTY off, CI set, env disabled, inside the throttle window) and the version comparison; install-method detection (a brew-prefix path refuses to self-replace); an offline test of the asset-URL plus SHA256-verification path against a fake release manifest.
- **Semver:** additive (an opt-out notifier and, later, a new `update` subcommand). The notifier's first appearance is a visible behaviour change for interactive users (a new stderr line), so it ships in its own minor, **10.3.0** (after 10.2.0 / site sweep), with a CHANGELOG note, following the ADR-then-implementation cycle site sweep used (ADR-0081 / PR #163, then PR #164). The deferred `update` subcommand follows in a later minor.
