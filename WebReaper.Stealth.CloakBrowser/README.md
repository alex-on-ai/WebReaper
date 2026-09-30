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

## Where the binary comes from

`WithCloakBrowser()` uses a CloakBrowser that is already on the machine before it downloads anything. First match wins:

1. `CloakBrowserOptions.ExecutablePath`, used as-is.
2. `CLOAKBROWSER_BINARY_PATH`, the vendor's own override. A value naming a missing file is an error, not a fall-through.
3. The WebReaper cache, `~/.webreaper/stealth/cloakbrowser/<build>/` (`%LOCALAPPDATA%\WebReaper\stealth\cloakbrowser\` on Windows). The CLI's `webreaper stealth install` uses the same cache, so an install made by either is reused by the other.
4. A build the cloakbrowser npm or pip package already downloaded: `~/.cloakbrowser/chromium-<build>/`, or `CLOAKBROWSER_CACHE_DIR`. The newest complete build wins.

PATH is not searched: the npm and pip packages put a CLI named `cloakbrowser` there, not the browser.

If nothing is found, the satellite downloads the platform's pinned build from CloakHQ's GitHub releases and verifies it against the release's `SHA256SUMS` before unpacking it into the WebReaper cache. Upstream does not publish every platform in every release, so each platform pins its own build, the same map the vendor's own wrappers pin:

| Platform | Build | Download |
|---|---|---|
| linux-x64 | 146.0.7680.177.5 | ~217 MB |
| linux-arm64 | 146.0.7680.177.3 | ~208 MB |
| osx-arm64 | 145.0.7632.109.2 | ~147 MB |
| osx-x64 | 145.0.7632.109.2 | ~159 MB |
| win-x64 | 146.0.7680.177.5 | ~562 MB |

There is no win-arm64 or 32-bit build; there, point `ExecutablePath` or `CLOAKBROWSER_BINARY_PATH` at a binary you supply. `CloakBrowserOptions.Version` pins another build for the current platform (the release tag `chromium-v146.0.7680.177.5` or the bare build `146.0.7680.177.5`); a release that lacks this platform fails fast, before the download. For CI or airgapped machines, set `AutoInstall = AutoInstallPolicy.Disabled`: an existing install is still used, but nothing is downloaded.

The launch flags (`CloakBrowserLauncher.RecommendedArgs`) include `--use-mock-keychain`, which keeps the macOS build off the login keychain; without it the browser stalls at startup and never publishes its CDP endpoint.

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
    ├── CloakBrowserInstaller.EnsureInstalledAsync()  → reuses an install, else downloads to ~/.webreaper/stealth/cloakbrowser/<build>/
    ├── CloakBrowserLauncher.LaunchAsync(path)        → uses CdpLaunchHelpers; spawns with stealth flags
    └── builder.WithCdpPageLoader(endpoint.CdpUrl)    → connects WebReaper.Cdp to the running browser
```

Composes naturally with [`WithProxy(...)`](https://github.com/alex-on-ai/WebReaper) (residential proxies for the hardest sites) and the LLM action resolver from `WebReaper.AI` (`.WithLlmActionResolver(...)`; semantic clicks like "dismiss popup" work through stealth).

## SemVer

10.0.0 (initial release). See [ADR-0054](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0054-stealth-backend-pattern-cloakbrowser.md) for the design.
