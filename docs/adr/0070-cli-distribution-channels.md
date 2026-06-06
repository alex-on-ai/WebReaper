# `webreaper` CLI distribution channels for v10.0.0 — Homebrew tap + install.sh

## Status

**Proposed** (2026-05-26; targets the v10.0.0 launch).

Pairs with [ADR-0071](0071-macos-codesigning-and-notarization.md) — install.sh
UX on macOS depends on codesigned + notarized binaries. The decisions split
because cert rotation, Notary API breaks, and signing-cert revocation each
warrant their own revisit cadence; bundling them obscures that.

## Context

[ADR-0043](0043-cli-and-agent-skill.md) shipped `WebReaper.Cli` as the
Native-AOT single-binary primitive. The existing
[`release.yml` `cli-publish` job](../../.github/workflows/release.yml#L291-L419)
already produces AOT binaries for six RIDs (linux-x64, linux-arm64, osx-x64,
osx-arm64, win-x64, win-arm64), renames `WebReaper.Cli` → `webreaper`, bundles
LICENSE + README, and attaches each as a GitHub Release asset on every tagged
push.

That is the **distribution mechanism**. What is missing is the **distribution
UX**.

The v10.0.0 launch goal — *every Claude Code user has heard of `webreaper
scrape`* — depends on the install command being **one short line a
landing-page reader can copy and run**. Today the user has to:

1. Open the GitHub Releases page
2. Identify the correct RID for their platform
3. Download the archive, extract it, `chmod +x`, move into `$PATH`

Each step is a drop-off point. The viral install must collapse them into one
line per platform.

`dotnet tool install -g WebReaper` is out of scope: `PackAsTool=true` is
structurally incompatible with `PublishAot=true` on a single target
(`release.yml:53–55`, RELEASE-RUNBOOK §1.48 — settled). Surrendering AOT
cold-start to gain .NET-dev convenience is the wrong trade for the audience
this launch targets (Claude Code users, who lean mac/Linux generalist). The
runbook's existing v10.x revisit note stands; this ADR does not reopen it.

## Decision

Two channels are added on top of the existing GitHub Releases automation:

### 1. Homebrew tap — `alex-on-ai/homebrew-webreaper`

A separate repo per the Homebrew tap convention, containing a single
`Formula/webreaper.rb`. End-user install path:

```bash
brew install alex-on-ai/webreaper/webreaper
```

(The shorter `brew install webreaper` requires graduation to `homebrew-core`,
which has a non-trivial acceptance bar — notability, version maturity, a
visible maintenance track record. Out of scope for v10.0.0; worth attempting
in v10.x once distribution proves traction.)

A new `homebrew-publish` job in `release.yml`, gated on `cli-publish`
success:

1. Downloads the four mac + Linux artifacts uploaded by `cli-publish`
   (`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`)
2. Reads the per-artifact SHA256 from the `SHA256SUMS` Release asset (see §2)
3. Renders `Formula/webreaper.rb` from a checked-in
   `homebrew/webreaper.rb.template`
4. Pushes the rendered formula to the tap repo using a fine-grained PAT
   scoped only to that repo

The formula declares four bottles (mac arm64, mac x64, linux x64, linux
arm64) each pointing at the matching GitHub Release asset URL. Windows is not
a Homebrew platform.

Per-release work in the tap repo is one auto-commit ("WebReaper 10.0.0") —
no manual intervention. The formula template lives in **this** repo so its
history is auditable here, not split across two repositories.

### 2. install.sh — bash one-liner served from raw.githubusercontent.com

A `scripts/install.sh` checked into this repo. Install command:

```bash
curl -fsSL https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/scripts/install.sh | sh
```

The script is the maximum-care version — ~120 lines, every failure-mode
handled, no surprises. Specifically:

- `set -eu` plus explicit `err`/`warn`/`info`/`ok` helper functions for
  diagnostic output. The script targets POSIX `/bin/sh` for distribution
  portability — bash extensions (`pipefail`, `ERR` trap, `$LINENO`,
  `[[`) are intentionally avoided so the one-liner runs cleanly under
  `dash`, `ash`, and `bash` alike. `set -e` covers the "fail loudly on
  any unhandled error" goal; tmpdir cleanup runs from a single `EXIT`
  trap.
- OS + arch detection via `uname -sm` → RID, with an explicit, copy-pasteable
  error for unsupported combinations (e.g. FreeBSD, illumos)
- Latest-release resolution via `https://api.github.com/repos/alex-on-ai/WebReaper/releases/latest`;
  prints the resolved tag (`Installing webreaper v10.0.0…`) before extracting;
  `WEBREAPER_VERSION=v10.0.0` env override for reproducibility / pinning
- Download with retry on network failure (three attempts, exponential
  back-off — 2s, 5s, 10s)
- SHA256 verification against the `SHA256SUMS` Release asset (see §3); failure
  aborts the install with the expected vs. actual hashes printed
- Install to `${PREFIX:-/usr/local/bin}` if writable, fall back to
  `${HOME}/.local/bin` with a clear note about PATH; honour an explicit
  `PREFIX` env override before either default
- Idempotent: existing `webreaper` at the target compared by `--version`; the
  default is "abort, ask the user" — `--force` / `WEBREAPER_FORCE=1`
  overwrites; `--upgrade` overwrites only if the new version is strictly
  newer (semver compare)
- Conflict detection: `command -v webreaper` reveals a different existing
  binary; the script warns with both locations and aborts unless `--force`
- Uninstall instructions printed at the end alongside the
  first-command hint (`webreaper init`)
- `WEBREAPER_INSTALL_VERBOSE=1` enables `set -x` and verbose `curl`
- Exit code documentation in the header comment block

### 3. SHA256 manifest

A new `checksums` job in `release.yml` runs after `cli-publish` succeeds.
For every binary archive produced by the matrix, the job computes a SHA256
and writes a single `SHA256SUMS` file in the standard format:

```
<hash>  webreaper-v10.0.0-osx-arm64.zip
<hash>  webreaper-v10.0.0-osx-x64.zip
…
```

`SHA256SUMS` is uploaded to the Release as a separate asset (not embedded in
each archive — verifiable independently, addressable by URL). Both
`install.sh` and `homebrew-publish` read from it.

### Trust model — bash-from-curl

The `curl … | sh` install is a remote-execution surface. Three layers of
mitigation:

1. **Auditable source.** `install.sh` lives at `master`/`scripts/install.sh`;
   its history is in git, reviewable by anyone. Same trust model as
   Homebrew's own `brew.sh/install.sh`.
2. **Binary verification.** The script verifies SHA256 against the
   release-side `SHA256SUMS` manifest. A tampered binary on GitHub Releases
   CDN cannot pass verification unless `SHA256SUMS` is also tampered, and
   the manifest is generated inside the same CI job that produces the
   binaries — a single point of trust, not two.
3. **Visible release tag.** Before extracting, the script prints the
   resolved tag so the user sees what version is being installed.

The remaining trust window: anyone with push access to `master` can swap
`install.sh`. That is the same trust model as every `curl | sh` installer in
production today — accepted as the cost of the one-line UX. Mitigated by
branch protection on `master` (the project already enforces this).

## Considered & rejected

**(a) `dotnet tool install -g WebReaper`** — deferred to v10.x per
RELEASE-RUNBOOK §1.48. `PackAsTool` × `PublishAot` incompatibility is
structural; the cold-start claim outranks .NET-dev convenience for this
audience. A future parallel framework-dependent package (`WebReaper.Tool`?)
is the v10.x escape hatch if real demand surfaces.

**(b) `winget install webreaper`** — deferred to v10.1. Windows users get the
AOT binary from GitHub Releases at launch. winget requires a per-release PR
to `microsoft/winget-pkgs` (manifest validation + external review cycle); the
pipeline addition is real but not launch-blocking.

**(c) `scoop install webreaper`** — deferred to v10.1. Smaller Windows
audience than winget; bucket repo would mirror the Homebrew tap shape,
addable in its own ADR.

**(d) Docker image (`docker run alex-on-ai/webreaper`)** — deferred
post-launch. Container distribution adds CVE-scan + base-image-refresh
maintenance that isn't justified by the launch audience. CI users can pull
the Linux binary from the Release page directly.

**(e) Homebrew formula directly in `homebrew-core`** — deferred
post-traction. The acceptance bar (notability, maintenance commitments) is
incompatible with a day-one launch. A tap is the standard first step.

**(f) `webreaper.dev/install.sh` vanity URL** — deferred to v10.1 with the
landing page. Requires owning a domain + a static-hosting + CDN story.
`raw.githubusercontent.com` has the same trust model and zero infrastructure.

**(g) Sigstore / cosign for Linux binaries** — deferred. The Linux ecosystem
does not gate on signatures the way macOS Gatekeeper does. Add when the
binary acquires enough distribution that signature gives a meaningful trust
signal.

**(h) Minimal install.sh (`uname → curl → tar → install`, ~40 lines)** —
rejected. Saves an evening of work but leaks every failure mode (network
flap, partial download, conflicting install, no PATH) into raw error
output. The launch UX bar is "no surprises"; the hardened script is the
right cost.

**(i) Bundle codesigning into this ADR** — rejected per HITL decision.
Codesigning has its own setup, failure modes, and revisit cadence (cert
rotation, Notary API breaks) that warrant a separate decision document.
See [ADR-0071](0071-macos-codesigning-and-notarization.md).

## Consequences

- **New repo to create**: `alex-on-ai/homebrew-webreaper` (one-time setup).
- **New files in this repo**:
  - `homebrew/webreaper.rb.template` — source of the rendered formula.
  - `scripts/install.sh` — the hardened ~120-line installer.
- **New `release.yml` jobs**: `checksums`, `homebrew-publish`. Both gated on
  `cli-publish` success.
- **New repo secret**: `GH_TOKEN_HOMEBREW_TAP` (fine-grained PAT scoped to the
  tap repo only — least privilege).
- **Failure semantics**: a failed `homebrew-publish` does NOT roll back the
  GitHub Release / NuGet release — matches the runbook's one-way-on-tag
  philosophy. Recovery is `gh workflow run release.yml --ref vX.Y.Z` re-dispatch
  against the same tag.
- **v10.1 distribution queue (deferred from this ADR)**: winget, Scoop,
  Docker image, `webreaper.dev` vanity URL + landing page.
- **v10.x potential revisit**: framework-dependent `dotnet tool` package if
  real .NET-dev demand surfaces.

## Implementation slices

| # | Slice | Blocks on |
|---|---|---|
| 1 | Create `alex-on-ai/homebrew-webreaper` repo with an empty placeholder Formula | — |
| 2 | Generate `GH_TOKEN_HOMEBREW_TAP` (fine-grained PAT, contents: write on tap repo only) and add to this repo's secrets | step 1 |
| 3 | `homebrew/webreaper.rb.template` checked into this repo | — |
| 4 | `release.yml` — new `checksums` job producing the `SHA256SUMS` Release asset | — |
| 5 | `release.yml` — new `homebrew-publish` job | steps 1–4 |
| 6 | `scripts/install.sh` (hardened) | step 4 (`SHA256SUMS` shape) |
| 7 | Dry-run end-to-end on a prerelease tag `v10.0.0-rc1` → throwaway release → verify all channels install cleanly on fresh macOS, Ubuntu, and Windows | steps 4–6 + [ADR-0071](0071-macos-codesigning-and-notarization.md) |

Slices 1, 3, 4, 6 can land in parallel PRs; 5 depends on the tap repo +
secret existing; 7 is the integration gate before tagging v10.0.0 proper.
