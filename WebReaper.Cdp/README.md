# WebReaper.Cdp

Raw [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/) `IPageLoadTransport` for [WebReaper](https://github.com/alex-on-ai/WebReaper); the **AOT-clean** browser transport, the bedrock for stealth Chromium fork backends (see [WebReaper.Stealth.CloakBrowser](https://www.nuget.org/packages/WebReaper.Stealth.CloakBrowser)).

## Why this satellite

Three things `WebReaper.Puppeteer` and `WebReaper.Playwright` cannot do that this one does:

1. **AOT-publishable inside `WebReaper.Cli`.** PuppeteerSharp and Microsoft.Playwright both depend on reflection-heavy serialisation. The CLI bakes `WebReaper.Cdp` and stays single-binary across 6 RIDs.
2. **Drive stealth Chromium forks** (CloakBrowser, Patchright, Camoufox, undetected-chromedriver). Those ship as bare Chromium binaries with `--remote-debugging-port`; CDP-direct is the only way to talk to them without re-introducing a heavy SDK dependency.
3. **Connect to any CDP endpoint**; a sidecar, a browser farm, a `playwright launch-server`, a remote debug session.

## Install

```bash
dotnet add package WebReaper.Cdp
```

## Quick start

```csharp
using WebReaper.Builders;
using WebReaper.Cdp;

// Option 1: connect to an existing CDP endpoint (BYO browser)
var engine = await ScraperEngineBuilder
    .CrawlWithBrowser("https://example.com")
    .Extract(schema)
    .WithCdpPageLoader("http://localhost:9222")
    .BuildAsync();

// Option 2: launch-and-connect (transport spawns Chromium itself)
var engine = await ScraperEngineBuilder
    .CrawlWithBrowser("https://example.com")
    .Extract(schema)
    .WithCdpPageLoader(new CdpLaunchOptions
    {
        // ExecutablePath = null → PATH-detected Chrome / Chromium / Edge
        Headless = true,
        AdditionalArgs = ["--no-sandbox"]
    })
    .BuildAsync();

await engine.RunAsync();
```

## `CdpLaunchHelpers`; the public utility every stealth satellite composes on

```csharp
using WebReaper.Cdp;

var path = CdpLaunchHelpers.FindOnPath("google-chrome", "chromium", "chrome", "msedge");
var endpoint = await CdpLaunchHelpers.LaunchAsync(
    new CdpLaunchSpec(path!, ["--headless=new", "--no-sandbox"]),
    CancellationToken.None);
// endpoint.CdpUrl, endpoint.DisposeAsync()
```

`WebReaper.Stealth.X` satellites use these helpers + the `WithCdpPageLoader(cdpUrl)` overload; they spawn their fork's binary, then hand the resulting CDP URL to this transport.

## SemVer

10.0.0 (initial release). See [ADR-0052](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0052-cdp-direct-loader-satellite.md) for the design.
