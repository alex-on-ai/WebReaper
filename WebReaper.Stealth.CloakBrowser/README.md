# WebReaper.Stealth.CloakBrowser

[CloakBrowser](https://github.com/CloakHQ/CloakBrowser) stealth Chromium fork backend for [WebReaper](https://github.com/alex-on-ai/WebReaper). One-liner `.WithCloakBrowser()` that finds (or downloads from upstream) the CloakBrowser binary, launches it with the fork's recommended flags, and wires its CDP endpoint into [`WebReaper.Cdp`](https://www.nuget.org/packages/WebReaper.Cdp).

The first concrete satellite of the ADR-0054 stealth-backend pattern.

## What CloakBrowser solves

C++ source-level fingerprint patches make the browser indistinguishable from a real user's. Designed for sites that block automation:

| Challenge | Outcome |
|---|---|
| Cloudflare Turnstile, reCAPTCHA v3, FingerprintJS, BrowserScan | **Silent pass**; challenge never appears |
| reCAPTCHA v2 image grid, hCaptcha (interactive puzzles) | Not handled by stealth; needs a separate captcha-solving service |
| DataDome on aggressive sites | Partial; vendor recommends headed mode + residential proxies (use [`WithProxy(...)`](https://github.com/alex-on-ai/WebReaper)) |

## Install

```bash
dotnet add package WebReaper.Stealth.CloakBrowser
# WebReaper.Cdp is pulled transitively
```

## Quick start

```csharp
using WebReaper.Builders;
using WebReaper.Stealth.CloakBrowser;

var engine = await ScraperEngineBuilder
    .CrawlWithBrowser("https://protected-site.example/catalog")
    .Follow("a.product-link")
    .Extract(productSchema)
    .WithCloakBrowser()             // one-liner: find/download/launch/wire
    .WriteToMongoDb(connStr, "scrapes", "products")
    .BuildAsync();

await engine.RunAsync();
```

## License acknowledgment

CloakBrowser's binary license: **free to use, no redistribution**. This satellite:

- **Does NOT** bundle the binary in the NuGet package (cannot redistribute).
- **DOES** download it from CloakHQ's own GitHub releases on first use (same legal model as `playwright install`, `winget`, `brew install --cask`).
- Logs a license-acknowledgment line on first install (via the wired `ILogger`); CLI surface (per ADR-0055) gets a Y/n prompt.

See [BINARY-LICENSE.md](https://github.com/CloakHQ/CloakBrowser/blob/main/BINARY-LICENSE.md) for the binding terms; by using this satellite you accept them.

## How it composes

```csharp
.WithCloakBrowser()
    │
    ├── CloakBrowserInstaller.EnsureInstalledAsync()  → finds/downloads to ~/.webreaper/stealth/cloakbrowser/
    ├── CloakBrowserLauncher.LaunchAsync(path)        → uses CdpLaunchHelpers; spawns with stealth flags
    └── builder.WithCdpPageLoader(endpoint.CdpUrl)    → connects WebReaper.Cdp to the running browser
```

Composes naturally with [`WithProxy(...)`](https://github.com/alex-on-ai/WebReaper) (residential proxies for the hardest sites) and the LLM action resolver from `WebReaper.AI` (`.WithLlmActionResolver(...)`; semantic clicks like "dismiss popup" work through stealth).

## SemVer

10.0.0 (initial release). See [ADR-0054](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0054-stealth-backend-pattern-cloakbrowser.md) for the design.
