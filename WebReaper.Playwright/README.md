# WebReaper.Playwright

[Microsoft.Playwright](https://playwright.dev/dotnet/)-backed `IPageLoadTransport` for [WebReaper](https://github.com/alex-on-ai/WebReaper); the modern, multi-browser headless-scraping satellite. Replaces the deleted `WebReaper.Puppeteer` in v10.

## Why Playwright (not Puppeteer)

- **Multi-browser**: Chromium, Firefox, WebKit out of the box
- **Auto-wait**: actions wait for the right state by default; less explicit `WaitForSelector` boilerplate
- **Better network interception**: `page.RouteAsync` over Puppeteer's older interception API
- **Official Microsoft .NET-native maintenance**
- **All 7 `PageAction` arms**; closes the ADR-0004 §"Out of scope" four-arm gap the deleted Puppeteer transport had

For AOT-compiled consumers (and for stealth Chromium forks), use [`WebReaper.Cdp`](https://www.nuget.org/packages/WebReaper.Cdp) instead; Microsoft.Playwright's reflection-driven serialisation is not AOT-clean by design (ADR-0009 satellite quarantine).

## Install

```bash
dotnet add package WebReaper.Playwright
# First run: Playwright downloads browser binaries
playwright install
```

## Quick start

```csharp
using WebReaper.Builders;
using WebReaper.Playwright;

// Default: Chromium
var engine = await ScraperEngineBuilder
    .CrawlWithBrowser("https://example.com")
    .Extract(schema)
    .WithPlaywrightPageLoader()
    .BuildAsync();

// Multi-browser
.WithPlaywrightPageLoader(PlaywrightBrowser.Firefox)
.WithPlaywrightPageLoader(PlaywrightBrowser.Webkit)

// Custom launch options (channel, headed, proxy, extra args)
.WithPlaywrightPageLoader(PlaywrightBrowser.Chromium, new PlaywrightLaunchOptions
{
    Headless = false,
    Channel = "chrome",   // use installed Chrome instead of bundled Chromium
})

await engine.RunAsync();
```

## Migration from `WebReaper.Puppeteer`

```diff
- using WebReaper.Puppeteer;
+ using WebReaper.Playwright;

- .WithPuppeteerPageLoader()
+ .WithPlaywrightPageLoader()
```

That is the full diff for the common case. See [ADR-0053](https://github.com/alex-on-ai/WebReaper/blob/master/docs/adr/0053-playwright-satellite-puppeteer-deletion.md) for the deletion rationale.

## SemVer

10.0.0 (initial release; carries the v10 major break; Puppeteer satellite deleted in the same release).
