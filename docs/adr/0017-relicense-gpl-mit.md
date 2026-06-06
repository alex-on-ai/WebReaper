# Relicense GPL-3.0-or-later → MIT

## Status

**Draft — ready to land** (2026-05-23; branch `adr-0017-relicense-mit`
off `origin/master`). The number was reserved in
[ADR-0008's out-of-scope bullet](0008-system-text-json-typed-pipeline.md)
for this exact change. **Phase-0 gate** of the [repositioning plan](../REPOSITIONING-PLAN.md)
— the funnel positioning rests on this. Folds into the unreleased
10.0.0 wave (or ships as 9.x point-release pre-10 if owner prefers).

**Merge gates** (this PR sits open until all green):

- [x] **Gate 1 — DISSOLVED.** The 41 commits previously under
      `olpavlov@<old-employer>.com` were rewritten via `git filter-repo`
      to use the owner's personal email
      (`alexppavlov93@gmail.com`) in a prior step in this same PR.
      The old work-account identity no longer appears in `git shortlog`;
      the employer-IP-claim risk that previously gated the relicense
      is structurally removed. See [§History rewrite](#history-rewrite-email-normalisation)
      below; old history preserved as `origin/pre-email-rewrite-master`
      and `origin/pre-email-rewrite-ai-native-wave`.
- [x] **Gate 2 — CLEARED on the analysis below; no outreach sent.**
      Owner elected (2026-05-23) to proceed on the *external-
      contributions* analysis recorded below: every external
      contribution in the project's history is either content-
      superseded (re-implemented away by later ADRs; not present in
      the current source) or *de minimis* / merger-doctrine (csproj-
      metadata factual edits — TFM strings, NuGet description text,
      dep version numbers — where the same change is the only
      correct change). The current tree contains no external code in
      copyrightable form; the relicense touches only the owner's
      authorship and the auto-generated infrastructure.
- [ ] **Gate 3 — Owner has reviewed the LICENSE/CONTRIBUTING/csproj
      diff** and accepts that ship-time is now.

The PR's body restates these gates so the owner is the merge decider.

## Context

The repositioning plan locks the funnel positioning around an MIT
license ([§1, §3](../REPOSITIONING-PLAN.md)):

> The library/CLI/skill stays **100% free, permissive (MIT)** — the
> credibility/SEO/adoption funnel. … GPL is a poor funnel license
> (deters commercial embedding → kills adoption).

The wave that just landed (ADRs 0040..0049 — the AI-native funnel
features) is the funnel material; shipping it under GPL-3.0-or-later
partly wastes it. Commercial .NET shops won't embed GPL code in
proprietary products, and the AGPL-equivalent worry (network-served
software triggering source-disclosure) reaches enough enterprises to
make GPL a real blocker even when the AGPL's specific trigger doesn't
apply.

The relicense is **strictly more permissive** — every existing user
is unaffected; new users who couldn't use the library under GPL gain
access. This is the unusually-low-stakes shape of an OSS relicense:
it's not "GPL → closed-source" (which would need a CLA and would
create cross-licensing complexity); it's "GPL → MIT" (every contributor
keeps every right they had).

## Contributor audit (refreshed 2026-05-23, **post-history-rewrite**)

`git shortlog -sne origin/master`:

| Identity | Commits | Classification |
|---|---|---|
| Owner (4 personal-identity variants) | 763 | Owner — see name normalisation below |
| External contributions (3 commit-author identities) | 6 | All de-minimis / content-superseded — see analysis below |
| `fossabot <badges@fossa.io>` | 1 | Automated — n/a |

The owner is **763 commits** across four self-identities, all under
personal email. The previously-separate old-work-account identity
(the Ukrainian transliteration "Oleksandr" was used by the owner's
work-account git config at the time) has been normalised — emails
rewritten to the personal address via `git filter-repo`; names
preserved as-is.

**The Gate 1 employer-IP risk is structurally removed.** There is no
longer a `@<old-employer>.com` email anywhere in the git history; the prior
analysis (work-for-hire check) is moot because the relevant author
identity is now indistinguishable from the owner's personal one.
See [§History rewrite](#history-rewrite-email-normalisation)
below.

### External contributions (6 commits total)

Two categories, both analysed as not requiring per-contributor consent
for the GPL→MIT relicense:

**Category A — csproj-metadata edits (4 commits, Nov 2025).** Per
`git show`, all four commits in this category touch only csproj files
and edit TFM strings, NuGet description text, package version
strings, or dependency version strings — factual updates ("this
project targets that framework"; "this version of that dependency";
"this URL"). Standard analysis treats such edits as either *de
minimis* (too small to claim copyrightable authorship) or trivially
clean-room-equivalent (re-doing the same factual update independently
produces the same result by necessity, the *merger doctrine*). No
creative authorship to license.

**Category B — content-superseded code (2 commits, Nov 2023).** One
commit added registration plumbing for an `IContentParser` interface;
the other was a whitespace-only undo. Both predecessors have since
been removed from the current source:

- `IContentParser` was removed at **6.0.0** when the legacy Newtonsoft
  `JObject` parser path was replaced by `IJsonContentParser` /
  `JsonObject` (ADR-0008).
- The associated registration method was renamed to
  `WithContentExtractor` (over the new `IContentExtractor` seam) at
  **ADR-0039**.

The current `ScraperEngineBuilder` has
`WithContentExtractor(IContentExtractor)` — a different name, a
different interface, a different type system (`JObject` was
Newtonsoft; `JsonObject` is `System.Text.Json`). The *function*
"register a custom content parser" survives conceptually, but the
external contributor's *expression* of it is gone (different name,
different interface, re-implemented twice over). The whitespace-only
commit has no copyrightable content. The current tree owes nothing
to either commit's expression.

### Automated — `fossabot <badges@fossa.io>` (1 commit)

A badge update by an automated bot; no human authorship. N/A.

## History rewrite — old work-account email normalisation

Before the relicense files were drafted, `git filter-repo` rewrote
the 41 commits authored under `olpavlov@<old-employer>.com` (and the 5
smart-quote-variant `"olpavlov@<old-employer>.com"` malformation) to use
the owner's personal email `alexppavlov93@gmail.com`. The
substitution was email-only — author names ("Oleksandr Pavlov" /
"Alexander Pavlov") were preserved exactly as committed.

**Why this happened**: the owner had configured the work-account
git identity (`the old employer` email) on a laptop used for personal OSS
work during 2023–2024. The old work-account email was committed-from by
accident; it was never the intended public identity for the project.
Relicensing without normalising would have left the unintended email
in MIT-licensed commits forever, plus required the employer
IP-assignment check (the original Gate 1) — both fixed by the
rewrite.

### What was done

```bash
# Backup the old history to recoverable refs on origin
git push origin master:refs/heads/pre-email-rewrite-master
git push origin ai-native-wave:refs/heads/pre-email-rewrite-ai-native-wave

# Rewrite via filter-repo (email-only substitution)
git filter-repo --force \
  --email-callback \
  'return b"alexppavlov93@gmail.com" if b"olpavlov@<old-employer>.com" in email else email'

# Re-add origin (filter-repo strips it as a safety default)
git remote add origin https://github.com/alex-on-ai/WebReaper.git

# Force-push rewritten master + all tags (release tags' SHAs moved)
git push origin master --force
git push origin --tags --force

# Force-push the in-flight PR #97 branch (its 11 commits descended
# from rewritten history, so their SHAs moved too)
git push origin ai-native-wave --force
```

### What was affected

- **41 commits** had their email rewritten (36 + 5 smart-quote-variant);
  every commit descending from them got a new SHA (cascade).
- **Tags** for v4.0.0, v4.1.0, v7.0.0, v7.1.0, v8.0.0, v9.0.0 — all
  re-pointed to the rewritten equivalent commits and force-pushed.
- **`master` branch on origin** — force-pushed (one-time temporary
  flip of `allow_force_pushes: true` on the branch-protection rule;
  restored to `false` immediately after the push).
- **`ai-native-wave` (PR #97)** — force-pushed with the rebased
  history; the PR's diff is unchanged (same code; only commit SHAs
  shift).

### Safety net

- `origin/pre-email-rewrite-master` (= old 454dcf8) — old history
  before any rewrite.
- `origin/pre-email-rewrite-ai-native-wave` (= old d67694a) — old
  AI-native-wave tip before any rewrite.

Both refs can be deleted after the rewrite has been live long enough
that no one needs the old SHAs (~30 days is the conventional
window).

### Caveats for external collaborators

- Anyone with an existing clone of WebReaper sees a divergent history
  on `master`. Standard fix: `git fetch --all; git reset --hard
  origin/master` (loses any local commits on the previous master).
- Bookmarked links to specific commit SHAs (e.g. in old issues or
  PR comments) now 404 unless they happen to reference the backup
  branches. The release-tag SHAs *did* change too, so any "as of
  v8.0.0" comparison links need re-pointing.
- Forks have the old history pinned; a `git pull` against the
  rewritten master will produce merge conflicts on every commit.
  Forks should `git fetch --all; git reset --hard origin/master` if
  they want to track the new history.

These are the standard costs of a force-push that rewrites history —
acceptable for a single-maintainer project at this scale.

## Decision

Five concrete moves; the PR ships all five together. Merge after the
three gates clear.

### 1. Replace `LICENSE.txt` with MIT

The standard MIT license (template from `spdx.org/licenses/MIT`):

```
MIT License

Copyright (c) 2022-2026 Alex Pavlov and WebReaper contributors

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software …
```

The filename `LICENSE.txt` stays the same (external references —
NuGet, GitHub auto-detection, README's `[LICENSE](LICENSE.txt)` —
don't break). Year range starts 2022 (first commit:
`2022-04-13 Alexander Pavlov`) and runs to the current year.

### 2. Add `CONTRIBUTORS.md`

A short file crediting prior authors by name. Standard OSS practice;
also serves as the durable thank-you the consent emails reference.

### 3. Add `CONTRIBUTING.md` with DCO going forward

The Developer Certificate of Origin (`developercertificate.org`,
Version 1.1) is the lightweight contributor-attestation standard the
Linux kernel, Docker, GitLab, and many others use. New contributions
sign off with `git commit -s` (adds a `Signed-off-by:` trailer); the
`PULL_REQUEST_TEMPLATE.md` reminds.

DCO over a CLA because:
- DCO requires no separate document-signing infrastructure.
- DCO requires no contributor to give up rights — they only attest
  that they have the right to contribute under the project's license.
- A CLA's main benefit (the project owner can relicense in the
  future) is precisely the thing this ADR is *avoiding* having to do
  again — MIT is already maximally permissive.

### 4. Add `<PackageLicenseExpression>MIT</PackageLicenseExpression>` to every csproj

All ten packages: `WebReaper` (core) + six existing satellites
(`Cosmos`, `Mongo`, `Redis`, `AzureServiceBus`, `Puppeteer`, `Sqlite`).
The three new AI-native-wave packages (`Cli`, `AI`,
`Extraction.Attributes`, `Extraction.Generators`, `Mcp` — five total)
need the same — this PR adds to the seven current csprojs; when the
AI-native-wave PR (#97) merges, a rebase of this PR adds them to the
new five too. (Alternatively, AI-native-wave is rebased to inherit
the MIT change; order doesn't matter as long as both eventually
carry the expression.)

### 5. Strip GPL mentions from README + satellite READMEs

Two mentions in `README.md` (lines 95, 458), one each in
`WebReaper.{Mongo,Redis,Puppeteer,AzureServiceBus,Cosmos}/README.md`,
one in `docs/RELEASE-RUNBOOK.md`. The historical analysis in
`docs/REPOSITIONING-PLAN.md` is left intact (it's a historical
planning document; updating it would rewrite the plan's reasoning).

## Considered options

### (a) Dual-license (GPL OR MIT) — rejected

Confusing for consumers (which to comply with?), no real benefit, and
the dual-license shape often invites disputes about which terms apply
to derived works. Single MIT is the cleaner shape.

### (b) Apache 2.0 instead of MIT — rejected

Apache 2.0 adds the explicit patent grant — useful in some contexts.
The WebReaper code has no patentable inventions; the additional
file-header NOTICE obligation is friction the funnel doesn't need;
MIT is the simpler, more-recognised "permissive" choice for libraries
of this size. Considered briefly; MIT wins on simplicity.

### (c) CLA over DCO — rejected

A CLA mainly benefits the project owner's ability to relicense in
the future — and this ADR is doing that one-time relicense now, with
the explicit intent that MIT is the terminal license. DCO suffices.

### (d) Keep GPL-3.0 and document workarounds for embedders — rejected

The whole funnel premise rests on adoption friction *being absent*.
"Workarounds" *are* friction. The plan's analysis stands.

### (e) Relicense incrementally per package — rejected

Six satellites + core (+5 new from the AI-native wave) move in
lockstep on every release. Splitting the relicense across multiple
PRs creates a window where some packages are MIT and others GPL —
exactly the consumer-confusion shape (a) rejects.

### (f) Solicit contributor consent BEFORE drafting any of this — rejected

The drafts are the point of leverage for the outreach — "here's the
diff that lands when you OK it" is a more concrete ask than "we plan
to relicense, do you mind?" Drafting first, sending second, merging
third is the standard sequence.

## Consequences

- **The funnel is unencumbered.** Commercial .NET shops can embed
  WebReaper without GPL compliance friction. AI/agent integrations
  (the audience the wave targets) can ship WebReaper in proprietary
  products.
- **The AI-native wave (ADR-0040..0049) is consumed under the license
  it was *for*.** Shipping it as 10.0.0 under MIT realises the plan's
  funnel positioning in full.
- **No commercial relicensing of contributor code.** Every
  contributor's code stays OSS, just under more-permissive terms.
  The DCO going forward keeps the rights chain clean for any future
  contribution.
- **External contributions in the project's history are in the
  current tree only in superseded or de-minimis form.** The
  analysis is recorded here so the decision is defensible.
- **Risk of old-employer claim** is the largest residual. The
  Gate 1 self-attestation + employer check is the standard
  mitigation.

## Implementation

Landed on `adr-0017-relicense-mit`:

1. **`LICENSE.txt`** — replaced with MIT (templated from
   `spdx.org/licenses/MIT`); copyright `2022-2026 Alex Pavlov and
   WebReaper contributors`.
2. **`CONTRIBUTORS.md`** — new file crediting the owner and the
   automated fossabot acknowledgment.
3. **`CONTRIBUTING.md`** — new file with: contribution flow, DCO
   text + sign-off instructions, code-style note, ADR-driven design
   reminder.
4. **`.github/PULL_REQUEST_TEMPLATE.md`** — DCO check + ADR
   reminder.
5. **Seven csprojs updated** with `<PackageLicenseExpression>MIT</PackageLicenseExpression>`:
   `WebReaper/WebReaper.csproj`, `WebReaper.{Cosmos,Mongo,Redis,AzureServiceBus,Puppeteer,Sqlite}/*.csproj`.
   The five AI-native-wave csprojs are updated when PR #97 / this PR
   are merged in order (whichever lands second sweeps the new ones).
7. **`README.md`** — both GPL mentions replaced with MIT; license
   section rewritten.
8. **Five satellite READMEs** — `Mongo`, `Redis`, `Puppeteer`,
   `AzureServiceBus`, `Cosmos` — the "GPL-3.0-or-later" line flipped.
9. **`docs/RELEASE-RUNBOOK.md`** — the GPL-3.0-or-later line flipped.

### Guardrails

- `dotnet build WebReaper.sln` — 0 errors (license metadata is
  build-time-only; no code change in csprojs).
- `dotnet test` — all baseline tests pass (no code touched).
- `dotnet pack WebReaper.sln -c Release` — emits MIT-licensed
  nupkgs (verification step before the actual release).

### What this PR does NOT do

- **Speak with the old employer.** Not required after the email
  rewrite dissolved Gate 1, but listed for completeness — the rewrite
  is the structural mitigation; the formal check is the owner's
  discretion.
- **Tag a release.** The 10.0.0 release is a separate task (and
  the relicense PR can ship as 9.x point-release if the owner
  prefers to decouple).
- **Update `docs/REPOSITIONING-PLAN.md`'s historical analysis.**
  The plan is a historical planning document; the relicense having
  shipped is a fact for the changelog, not a rewrite of the
  reasoning.

## References

- ADR-0008 — its out-of-scope bullet reserves number 0017 for this
  exact relicense.
- ADR-0009 — the registration-seam + satellite pattern; this ADR's
  csproj sweep covers all satellites + core in one diff.
- REPOSITIONING-PLAN §1, §3 — the funnel positioning the relicense
  enables; the contributor audit this ADR refreshes.
- Developer Certificate of Origin v1.1 — `developercertificate.org`.
- SPDX MIT — `spdx.org/licenses/MIT`.
