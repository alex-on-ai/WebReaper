# Contributing to WebReaper

Thanks for your interest! WebReaper is an ADR-driven codebase: most
non-trivial changes are documented as an ADR (`docs/adr/`) before
they ship, and the ADR's *reasoning* is as important as the code
diff.

## Quick start

1. Open an issue describing the change you want to make. For anything
   substantial, expect a short design-pass discussion first — we'd
   rather catch a shape problem before you write a thousand lines.
2. Fork → branch → PR against `master`.
3. Sign off your commits (`git commit -s`) — see [DCO](#dco) below.
4. CI runs the full unit suite + Native-AOT smoke. The PR is ready
   when those are green and the design discussion (if any) is
   concluded.

## Build & test

```bash
dotnet build WebReaper.sln
dotnet test WebReaper.Tests/WebReaper.UnitTests/WebReaper.UnitTests.csproj
```

For Native-AOT verification (the WebReaper core + the CLI both
publish clean):

```bash
cd WebReaper.Tests/WebReaper.AotSmokeTest
dotnet publish -c Release -r osx-arm64 --self-contained true
./bin/Release/net10.0/osx-arm64/publish/WebReaper.AotSmokeTest
```

The integration tests (`WebReaper.Tests/WebReaper.IntegrationTests`)
hit a live site and run real Puppeteer; skip them locally — CI runs
them.

## Coding standards

- **Match the surrounding code.** Comment density, naming, and idiom
  should be consistent with the file you're changing.
- **Public API additions are documented.** `WarningsAsErrors=CS1591`
  is on for the core (ADR-0023); every Tier-1 public type and member
  needs an XML doc.
- **No reflection / `dynamic` / `Activator.CreateInstance` on the
  hot path.** AOT-cleanliness is a core guarantee (ADR-0008);
  Native-AOT must publish zero IL/AOT warnings.
- **Tests-by-construction over tests-after.** A new seam typically
  comes with focused unit tests that pin its contract.
- **ADRs are the design memory.** When in doubt about why something
  is the way it is, search `docs/adr/`; the answer is usually there.

## ADRs

For changes that touch the public surface, introduce a new seam, or
make a non-obvious shape decision, write an ADR
(`docs/adr/00NN-*.md`). The existing ADRs are the format reference.
Number sequentially — the project deliberately jumps numbers when
ADRs are dropped (gaps are intentional, never reused).

The exception: small bug fixes, doc tweaks, and dependency bumps
don't need an ADR.

## Reporting bugs

Bugs are tracked as [GitHub Issues](https://github.com/alex-on-ai/WebReaper/issues).
A good bug report includes:

- A clear, descriptive title.
- The exact steps to reproduce — what you ran, what URL was crawled
  (if any), what Schema (if any).
- The behavior you observed and the behavior you expected.
- The shortest possible reproducing code snippet (a failing unit
  test is ideal).
- Stack trace if the library threw; relevant log lines if it didn't.

## Suggesting enhancements

Enhancement suggestions are also [GitHub Issues](https://github.com/alex-on-ai/WebReaper/issues).
A good enhancement suggestion includes:

- A clear description of the use case.
- An example of the current API workflow if any.
- A sketched API for the new functionality (a builder method
  signature is enough).
- Whether you're willing to take a swing at implementing it.

## DCO

WebReaper requires contributions to be signed off under the
[Developer Certificate of Origin v1.1](https://developercertificate.org/).
Adding `Signed-off-by: Your Name <you@example.com>` to every commit
message attests:

> By making a contribution to this project, I certify that:
>
> (a) The contribution was created in whole or in part by me and I
>     have the right to submit it under the open source license
>     indicated in the file; or
>
> (b) The contribution is based upon previous work that, to the
>     best of my knowledge, is licensed under an appropriate open
>     source license and I have the right under that license to
>     submit that work with modifications, whether created in whole
>     or in part by me, under the same open source license (unless
>     I am permitted to submit under a different license), as
>     indicated in the file; or
>
> (c) The contribution is based upon previous work that has been
>     provided under an open source license and has the right to
>     submit it under the same open source license (unless I am
>     permitted to submit under a different license), as indicated
>     in the file; or
>
> (d) The contribution is made free of any other party's rights
>     with permission, beyond a license signed off by me as
>     documented in the file.

The shortcut: `git commit -s` adds the sign-off automatically. To
sign off a recent commit you forgot to sign:
`git commit --amend --signoff`.

## License

WebReaper is licensed under [MIT](LICENSE.txt) (ADR-0017). By
contributing, you agree your contribution is licensed under the same
terms. The DCO sign-off is your attestation that you have the right
to so license your contribution.
