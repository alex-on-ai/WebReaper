# macOS codesigning + notarization for the AOT `webreaper` CLI

## Status

**Proposed** (2026-05-26; targets the v10.0.0 launch).

Pairs with [ADR-0070](0070-cli-distribution-channels.md) — both Homebrew and
install.sh distribution paths on macOS depend on this being in place.

## Context

The AOT-published `webreaper` binaries for `osx-arm64` and `osx-x64`
produced by the existing
[`release.yml` `cli-publish` job](../../.github/workflows/release.yml#L291-L419)
are unsigned. macOS Gatekeeper blocks unsigned binaries downloaded from the
internet with:

> *"webreaper" cannot be opened because the developer cannot be verified.*

The block applies to **every distribution path** in [ADR-0070](0070-cli-distribution-channels.md):

- **Homebrew install** — Homebrew binaries are quarantined; first run hits
  Gatekeeper unless the binary is signed and notarized.
- **install.sh** — the `curl` download path applies the `com.apple.quarantine`
  extended attribute; Gatekeeper blocks the first invocation.
- **Manual download from Releases** — same quarantine, same block.

The user's escape paths are: right-click → Open → "Open Anyway" (clicked once
per binary, never sticks across versions); or System Settings → Privacy &
Security → "Allow Anyway" (one click but requires navigating settings).
Either kills the *agent picks up `webreaper` and runs it* flow in unattended
scenarios, and tanks the "10-second install" pitch in attended ones.

The Apple-approved fix: sign with a Developer ID Application certificate and
notarize with Apple's notary service.

The project owner is enrolled in the Apple Developer Program; the cost is
already paid.

## Decision

`release.yml`'s `osx-arm64` and `osx-x64` matrix arms gain three new steps
after AOT publish, before the archive step:

### 1. Codesign

```bash
codesign \
  --sign "$APPLE_DEVELOPER_ID" \
  --options runtime \
  --timestamp \
  --identifier "io.highcraft.webreaper" \
  --force \
  webreaper
```

- **Identity** — `Developer ID Application: <Registered Name> (<TEAM_ID>)`.
  Confirmed (HITL) as the correct cert type for binaries distributed outside
  the Mac App Store. Mac App Store would require "Apple Development" +
  sandbox entitlements + a fundamentally different distribution path; not
  relevant here.
- **Hardened runtime** (`--options runtime`) — required by Apple notarization
  since 2019. Adds JIT / library-loading restrictions; a Native-AOT binary
  needs none of those entitlements, so no `--entitlements` plist is supplied.
- **Timestamp** — required so the signature remains verifiable after the cert
  expires. Without `--timestamp`, an expired cert invalidates all binaries
  signed under it.
- **Identifier** — `io.highcraft.webreaper`, a stable bundle identifier
  reserved on the project owner's contact domain (`business@highcraft.io`).
  Not Mac App Store-scoped; the namespace is project-controlled.
- **Force** — overwrites a prior signature if one is present. Idempotent
  re-runs of CI must not fail because of a stale signature.

### 2. Notarize

```bash
# Zip the binary (notarytool requires a container, not a raw Mach-O).
zip notarize-input.zip webreaper

xcrun notarytool submit notarize-input.zip \
  --apple-id "$APPLE_ID" \
  --password "$APPLE_NOTARIZATION_PASSWORD" \
  --team-id "$APPLE_TEAM_ID" \
  --wait
```

- `notarytool` is the modern API; `altool` is deprecated (removed by
  Apple in late 2023). No fallback path.
- `--wait` blocks until Apple returns a verdict — typically 1–5 minutes per
  binary; the long tail is ~30 minutes if Apple's notary service is under load.
- A failed verdict (hardened-runtime issue, malware signal, infra flap) fails
  the CI step loudly with the Apple ticket UUID. Logs are pulled with
  `xcrun notarytool log <uuid> --apple-id … --password … --team-id …` for
  diagnosis.
- `--password` is the app-specific password (generated at
  appleid.apple.com), NOT the Apple ID password. Standard Apple-side guidance.

### 3. Staple + verify

```bash
xcrun stapler staple webreaper
spctl -a -t exec -vvv webreaper
```

- `stapler` writes the notarization ticket onto the binary itself so
  Gatekeeper verifies offline on first run, without contacting Apple. Faster
  user UX; works on air-gapped machines.
- `spctl -a -t exec -vvv` is the verification gate. The CI step fails if
  Gatekeeper would reject the binary — turning a notarization regression into
  a CI failure before a single user sees a broken binary.

The stapled, signed binary is the artifact that proceeds to the archive +
upload steps in the existing `cli-publish` flow.

### Required setup

| Item | Storage | Notes |
|---|---|---|
| Developer ID Application certificate (.p12) | Repo secret `APPLE_CERTIFICATE_P12_BASE64` | Base64-encoded .p12 export from a Keychain; standard CI ingestion shape |
| .p12 password | Repo secret `APPLE_CERTIFICATE_PASSWORD` | The password set when exporting the .p12 |
| App-specific password for notarytool | Repo secret `APPLE_NOTARIZATION_PASSWORD` | Generated at appleid.apple.com → Sign-In and Security → App-Specific Passwords. NOT the Apple ID password. |
| Apple ID (notarization submitter) | Repo secret `APPLE_ID` | The email of the Apple Developer account |
| Apple Team ID | Repo variable `APPLE_TEAM_ID` | Non-secret; visible at developer.apple.com → Membership |
| Developer ID label | Repo variable `APPLE_DEVELOPER_ID` | Non-secret. E.g. `Developer ID Application: Alex Pavlov (XXXXXXXXXX)` — exact string from `security find-identity -v -p codesigning` |

The CI pre-step imports the .p12 into a temporary keychain created with a
random password; the keychain is destroyed at job teardown so the cert does
not persist on the runner. Standard pattern; community action
`apple-actions/import-codesign-certs@v3` implements it correctly.

## Considered & rejected

**(a) Ad-hoc signing (`codesign -s -`)** — rejected. Ad-hoc signing satisfies
the executable-format requirement but does NOT pass Gatekeeper for
internet-downloaded binaries. Same broken UX as unsigned for the user.

**(b) Self-signed certificate** — rejected. Gatekeeper trusts only
Apple-issued Developer ID certs in the public chain; a self-signed cert is
treated as unsigned for the end user. (It would work in a fleet under MDM,
which is not our distribution model.)

**(c) Sign without notarizing** — rejected. Signing alone passes Gatekeeper
on macOS 10.14 (Mojave) but is **blocked on macOS 10.15 (Catalina) and later**
for binaries downloaded from the internet. Catalina is from 2019; "blocked on
every supported macOS" is not a viable launch.

**(d) `altool`** (older notarization CLI) — rejected. Deprecated by Apple,
removed by late 2023. `notarytool` is the only supported path.

**(e) Notarize the .zip archive, not the inner binary; rely on archive-level
notarization** — rejected. Apple supports zip submission, but the staple
attaches to the contained binary — not the zip — when the zip is opened.
Notarizing the archive without stapling onto the binary forces Gatekeeper to
re-check over the network on first run, which is slower and breaks on
air-gapped or restricted-network installations.

**(f) `--keychain-profile` for notarytool credentials** — deferred. Profile-based
auth (`notarytool store-credentials …` once, then `notarytool submit
--keychain-profile <name>`) is cleaner than passing `--apple-id`/`--password`
per call but requires an additional one-time setup step inside the CI
keychain. Adopt in a hardening pass; not a launch blocker.

**(g) Sign Linux binaries via sigstore/cosign** — out of scope (see
[ADR-0070](0070-cli-distribution-channels.md) §considered). Linux distribution
does not gate on signature.

**(h) Sign Windows binaries with an EV / OV code-signing cert** — deferred.
Windows SmartScreen warns on unsigned binaries but does not block; the UX
hit is real but smaller than macOS Gatekeeper, and EV certs are non-trivial
to acquire and rotate. Revisit in v10.x with SmartScreen reputation data
from the launch.

**(i) Bundle this ADR into ADR-0070 ("CLI distribution channels")** —
rejected. The decisions have different revisit cadences: distribution
channels evolve when audiences grow (winget, Scoop, Docker), codesigning
evolves when Apple changes the rules (notary API, cert types, hardened
runtime requirements). Pinned together in a single ADR, each revisit
churns the other's content.

## Consequences

- **Annual cost**: $99 Apple Developer Program membership — already paid.
- **Per-release CI cost**: 1–5 minutes added per macOS arm (×2 = up to 10
  minutes total); occasional 30-minute tail when Apple's notary service is
  loaded. Tractable for any reasonable release cadence.
- **Cert rotation**: Developer ID Application certs are valid for five years.
  Binaries signed under an expired cert remain verifiable thanks to
  `--timestamp` (Gatekeeper accepts the signature; the cert's expiry does
  not invalidate the past signature). New binaries need to be re-signed with
  a fresh cert. **Calendar reminder belongs on the maintainer**; the rotation
  procedure (issue new cert, export, re-encode `APPLE_CERTIFICATE_P12_BASE64`,
  update `APPLE_DEVELOPER_ID` if the cert label changes, dry-run a tag)
  belongs in [RELEASE-RUNBOOK.md](../RELEASE-RUNBOOK.md) as a follow-up doc
  task.
- **Notarization failure recovery**: if Apple's notary service is down or
  returns a soft rejection, the macOS arms of `cli-publish` fail loudly with
  the ticket UUID. Linux + Windows arms remain unaffected (they don't depend
  on the macOS jobs). Recovery is `gh workflow run release.yml --ref vX.Y.Z`
  once Apple is back; the upload step's `--clobber` makes the rerun
  idempotent.
- **Secrets surface**: four new secrets + two new variables (table above).
  `APPLE_CERTIFICATE_P12_BASE64` is the most sensitive — a leak lets an
  attacker sign binaries under this identity. Revocation path: Apple
  Developer portal → certificates → revoke; the revocation propagates to
  Gatekeeper via the CRL within ~24h. Standard CI-secret threat model.
- **`spctl -a` verify step** acts as a quality gate — a regression that
  produces a Gatekeeper-rejected binary fails CI before the binary reaches
  a Release asset.
- **Bundle identifier reserved**: `io.highcraft.webreaper` becomes the
  project's reserved namespace. Future Mac-related artifacts (e.g. a hypothetical
  v11+ desktop companion) should fit under `io.highcraft.webreaper.*` or pick
  a sibling namespace deliberately; an identifier collision with another
  signed binary on a user's system is undefined behaviour under Gatekeeper.

## Implementation slices

| # | Slice | Blocks on |
|---|---|---|
| 1 | Generate Developer ID Application certificate at developer.apple.com → Certificates | — (Apple Developer enrollment is already complete) |
| 2 | Export the cert as a .p12 from Keychain Access; base64-encode the file (`base64 -i cert.p12 \| pbcopy`) | step 1 |
| 3 | Generate an app-specific password at appleid.apple.com for `notarytool` | — |
| 4 | Look up Team ID and the exact Developer ID label string (`security find-identity -v -p codesigning`) | step 1 |
| 5 | Add the four secrets + two variables to this repo's settings | steps 2–4 |
| 6 | `release.yml` — codesign + notarize + staple + `spctl -a` verify steps in `osx-arm64` and `osx-x64` matrix arms | step 5 |
| 7 | Smoke test: dry-run on prerelease tag `v10.0.0-rc1` → download from the throwaway Release on a clean macOS user account → confirm Gatekeeper accepts and binary runs without warning | step 6 + [ADR-0070](0070-cli-distribution-channels.md) §7 |

Step 1 is single-threaded for the project owner; steps 2–5 follow in ~30
minutes. Step 6 is the CI work, parallel to ADR-0070's implementation. Step
7 is the joint integration gate before tagging v10.0.0 proper.

## Amendment (2026-05-28): Stapler does not support raw Mach-O

The v10.0.0 launch on 2026-05-27 discovered that the §3 *Staple + verify*
step does not work on the artifact this ADR ships. Section §3 should be
read together with this amendment; the pipeline that actually shipped to
users is **codesign + notarize, no staple**.

### Original claim (wrong)

§3 instructed `release.yml` to run `xcrun stapler staple webreaper`
followed by `spctl -a -t exec -vvv webreaper`, with the justification that
stapling "writes the notarization ticket onto the binary itself so
Gatekeeper verifies offline on first run, without contacting Apple."

### Discovery

During the v10.0.0 launch run of `release.yml`, both macOS matrix arms
(`osx-arm64` and `osx-x64`) failed at the stapler step with exit code 66
(`EX_NOINPUT`, "unparsable input"). The preceding notarization step was
**successful**; Apple returned `status: Accepted` (submission UUID
`5df240ae-1aae-428f-ba6d-410ae2c2a530` for `osx-arm64`). Stapler ran on
the same binary moments later and refused it:

```
→ stapling ticket onto binary...
Processing: …/publish/WebReaper.Cli
##[error]Process completed with exit code 66.
```

### Apple-side reason

`xcrun stapler` only supports notarization tickets on **container
artifacts**: `.app` bundles, `.pkg` installers, and `.dmg` images. The
ticket is attached to those containers (it travels with the container's
filesystem metadata), not to a Mach-O executable on its own. The
WebReaper CLI ships as a raw Mach-O (a single AOT-published binary, not
an `.app` bundle), so stapler has no container to anchor the ticket to.
Exit 66 is Apple's "stapler does not recognise this input" verdict.

The original §3 conflated two distinct cases. For `.app`/`.pkg`/`.dmg`
distributions, stapling is real and useful (Gatekeeper verifies offline).
For raw Mach-O CLI binaries, stapling is unsupported by design.

### Fix (PR #131)

[PR #131](https://github.com/alex-on-ai/WebReaper/pull/131) (merged
2026-05-26, folded into the v10.0.0 launch on 2026-05-27) dropped the
`xcrun stapler staple` step **and** the `spctl -a -t exec -vvv` verify
step from both `osx-arm64` and `osx-x64` matrix arms of `release.yml`.
The codesign and notarize steps (the meaningful ones) remain unchanged.

The `spctl` verify step was removed alongside stapler for a related
reason: `spctl` on an unstapled binary requires the same online lookup
that Gatekeeper will perform at first launch, and at fresh-notarization
time Apple's CDN has a small propagation window where the lookup can
spuriously fail. The contract that survives is `notarytool submit
--wait` returning `status: Accepted`; the CI step trusts that and
proceeds.

### Post-fix behavior

The shipping pipeline is **codesign + notarize only**. macOS Gatekeeper
performs an online lookup the first time the binary runs:

1. User installs via Homebrew / install.sh / direct download.
2. Binary lands on disk with the `com.apple.quarantine` extended
   attribute.
3. First invocation triggers Gatekeeper, which queries Apple's notary
   service for the binary's hash.
4. Apple confirms the binary is notarized under Team ID `U8UJCM9X76`.
5. Gatekeeper allows the binary silently; the quarantine xattr is
   cleared; subsequent invocations are unimpeded.

This is the documented Apple pattern for standalone CLI distribution.
Every CLI on Homebrew that does not ship as a `.pkg` uses the same
model (yt-dlp, fly, wrangler, gh, fnm, rclone). The single requirement
is that the user has internet at first launch; every realistic install
path (Homebrew, `curl | sh`, direct download from a GitHub Release) is
already an internet-connected path, so the practical user impact is
zero.

The only scenario this amendment loses, relative to the original §3
plan, is **first launch on an air-gapped machine** that has the binary
on disk but no route to Apple's notary service. That scenario is not
served by the WebReaper CLI's distribution channels (which all assume
internet for the download itself); a hypothetical future air-gapped
distribution would have to revisit container-based packaging.

### What stays from the original ADR

- Developer ID Application certificate (§3.1)
- `codesign --options runtime --timestamp` with hardened runtime (§3.1)
- `xcrun notarytool submit --wait` (§3.2)
- Apple Developer Program enrollment and all four secrets plus two
  variables (§Required setup)
- The bundle identifier `io.highcraft.webreaper`
- The Considered & rejected list (still accurate; (e) "notarize without
  stapling" was the right call for the wrong reason, the practical
  outcome matches)

### What is removed

- `xcrun stapler staple` (§3.3): unsupported on Mach-O, no replacement
- `spctl -a -t exec -vvv` verify (§3.3): would fail spuriously against
  fresh notarization

### Reading §3 today

Section §3.3 *Staple + verify* should be treated as historical context
for the design pass, not as instructions to follow. The current
shipping pipeline lives in `.github/workflows/release.yml`; this
amendment reflects what the file does and why.
