# `WebReaper.Mcp` browser transport policy: bake `WebReaper.Cdp`, mirror ADR-0055

## Status

**Accepted**. Follow-through to [PR #122](https://github.com/alex-on-ai/WebReaper/pull/122),
which fixed only the stale tool description; the wiring fix was deferred
to its own ADR + slice. v10.0.2 patch.

## Context

The `WebReaper.Mcp` satellite (ADR-0049) exposes three MCP tools over
stdio: `scrape`, `map`, `extract`. Two of them (`scrape`, `extract`)
accept a `browser` boolean parameter that flips the seed from
`ScraperEngineBuilder.Crawl(url)` to `ScraperEngineBuilder.CrawlWithBrowser(url)`.
That seed sets the page type to `Dynamic`; on `RunAsync` the core
dispatches the Dynamic load to whichever `IPageLoadTransport` was
registered by `.WithCdpPageLoader(...)` / `.WithPlaywrightPageLoader(...)` /
a `WebReaper.Stealth.*` extension.

**The gap.** `WebReaper.Mcp.csproj` `ProjectReference`s only
`WebReaper.csproj`, no browser-transport satellite. When the MCP client
calls `scrape(url, browser=true)` the seed flips to Dynamic, the builder
composes without a transport, and the first dynamic page load hits the
core's `BrowserNotConfiguredPageLoadTransport` error directing the user
to add a satellite, except the MCP server process is the one that
should have it baked, and no MCP client can add a NuGet reference on
its behalf. The `browser=true` parameter is therefore *structurally*
unwired: it sets a flag that the satellite has no transport to honour.

PR #122 noted the gap as a follow-up; the description fix alone shipped.
This ADR records the wiring decision.

### Architectural precedent (ADR-0055)

The CLI faced the same question (which transport to bake into a
single-distributable binary) and answered:

> The CLI bakes **only** `WebReaper.Cdp` for browser support, the
> AOT-clean transport (ADR-0052's bedrock). … `Microsoft.Playwright`
> is never baked; nor is any `WebReaper.Stealth.X` satellite; those
> ship for library use only.

`WebReaper.Mcp` is not AOT-published (ADR-0049: "No PublishAot in v1;
the MCP SDK has reflection paths"), so the AOT half of ADR-0055's
rationale is *softer* here. The other half (lightweight dependency
graph, AOT-friendly bedrock so future MCP-AOT is on-ramp-able, stealth
forks compose on Cdp regardless) still applies, and the *consistency*
argument (one canonical browser baked across both satellite agent
surfaces) carries the rest.

## Decision

`WebReaper.Mcp` follows the ADR-0055 precedent: **bake only
`WebReaper.Cdp`**. The MCP tools wire `.WithCdpPageLoader(new CdpLaunchOptions())`
on the path where `browser=true`. The launch-and-connect overload finds
a system Chrome / Chromium / Edge on PATH (or fails actionably if
none), spawns it with `--remote-debugging-port=0 --headless=new`, and
the transport's `IAsyncDisposable` tears the process down on engine
disposal (ADR-0058 chain).

### What changes

```diff
 WebReaper.Mcp.csproj
   <ItemGroup>
     <ProjectReference Include="..\WebReaper\WebReaper.csproj" />
+    <ProjectReference Include="..\WebReaper.Cdp\WebReaper.Cdp.csproj" />
   </ItemGroup>
```

```diff
 WebReaperTools.cs (Scrape and Extract)
   var seed = browser
       ? ScraperEngineBuilder.CrawlWithBrowser(url)
       : ScraperEngineBuilder.Crawl(url);

-  var engine = await seed.AsMarkdown()
+  var builder = seed.AsMarkdown()
       .Subscribe(records.Add)
-      .StopWhenAllLinksProcessed()
-      .BuildAsync();
+      .StopWhenAllLinksProcessed();
+
+  if (browser)
+      builder = builder.WithCdpPageLoader(new CdpLaunchOptions());
+
+  await using var engine = await builder.BuildAsync();
   await engine.RunAsync();
```

The `await using` shape matters: the `CdpLaunchOptions` overload owns
the spawned-Chromium process lifecycle; without the `using` the child
process leaks across MCP tool invocations until the MCP server itself
exits (ADR-0054 gotcha, generalised to any launch-and-connect path).

## Considered options

### (a) Bake `WebReaper.Cdp` (chosen)

Lightweight dependency, no PuppeteerSharp / Microsoft.Playwright pull,
AOT-friendly bedrock (so a future MCP-AOT path is on-ramp-able). Mirrors
the CLI. Consistent agent-surface posture: one canonical transport
baked into both satellites that publish as agent-facing binaries.

### (b) Bake `WebReaper.Playwright` (rejected)

Microsoft.Playwright pulls a large dependency graph (Node.js runtime,
~100 MB browsers via `playwright install`). The MCP server boots on
every agent invocation in some hosts (Claude Desktop spawns fresh on
each conversation); a heavier package directly tax-charges every MCP
spin-up. Playwright's structural advantages (all seven `PageAction`
arms vs Cdp's six pre-ADR-0057, now closed) don't matter to the v1
MCP tool set, which calls `LoadAsync` once per URL with no action
chain.

### (c) Bake both `WebReaper.Cdp` and `WebReaper.Playwright`, pick at runtime (rejected)

Two transports configured + an MCP-tool-parameter to choose between
them. Twice the dependency surface; the picker UX duplicates the
MCP tool's existing `browser=true` parameter; no observed demand. The
satellite is the *interop adapter* (ADR-0049): keep its surface thin.

### (d) Keep MCP transport-pluggable; document the gap (rejected)

PR #122's option (c). Punts the wiring to the MCP-host operator,
who has no NuGet seam to add the satellite from outside the published
binary. The casual MCP-user (Cursor, Claude Desktop) cannot escape
the gap; this satellite would be permanently no-op on `browser=true`.

### (e) Remove the `browser` parameter from MCP tools (rejected)

PR #122's option (b). Honest but lossy: dynamic-page sites are the
hard cases agents most want to scrape. A satellite that refuses to
even *try* on JS-rendered pages is the wrong shape for the interop
adapter slot.

## Bounded scope (what v1 does and does not do)

### v1 does

- Wire `.WithCdpPageLoader(new CdpLaunchOptions())` on the
  `browser=true` path of `scrape` and `extract`.
- Use the launch-and-connect overload: PATH search for
  `google-chrome` / `chromium` / `chrome` / `microsoft-edge` /
  `msedge`, spawn `--remote-debugging-port=0 --headless=new`,
  teardown on engine disposal.
- Fail actionable when no system browser is found (the Cdp loader's
  built-in message points the user at installing Chrome / Chromium).
- `await using` the engine so the spawned-Chromium process exits
  with the MCP tool invocation.

### v1 does not

- **No `webreaper browser install` equivalent.** The CLI gates this
  on a curated managed-Chromium acquisition (ADR-0055); MCP relies
  on system-installed browsers in v1. A managed download path inside
  the MCP server would either bloat the package or require a
  user-prompt flow over a protocol that has no UX surface for one.
  If MCP hosts surface this as a real friction in practice, a
  future ADR adds a fallback (perhaps shelling out to the CLI when
  one is detected on PATH).
- **No stealth-backend selection.** The CLI integrates
  `KnownStealthBackends` via a curated registry (ADR-0055); the MCP
  tools do not expose a stealth parameter. Users blocked by bot-checks
  fall back to the CLI / the library directly; the MCP satellite is
  not the right surface for the Hybrid C escalation UX (no Y/n prompt
  channel; bot-check detection adds latency to every call).
- **No `--browser-cdp-url` BYO equivalent on the MCP tool parameter.**
  A future tool argument (`cdpUrl=...`) is a clean additive minor;
  zero observed demand today.
- **No new test project.** `WebReaper.Mcp.Tests` doesn't exist
  (ADR-0049 §Guardrails: "no unit tests in v1; the satellite's
  surface is dominantly SDK boilerplate"). The behaviour change is
  a one-line conditional builder forward; the surface tested is
  the `WebReaper.Cdp` extension's, already covered in
  `WebReaper.Cdp.Tests`. Adding a brand-new test project just to
  assert "the `if (browser)` branch calls `.WithCdpPageLoader(...)`"
  is over-weight for the slice; revisit if a regression surfaces.

## Implementation outline

Two-file change plus the ADR + CHANGELOG.

1. **`WebReaper.Mcp/WebReaper.Mcp.csproj`**: add the
   `ProjectReference` to `..\WebReaper.Cdp\WebReaper.Cdp.csproj`.
2. **`WebReaper.Mcp/WebReaperTools.cs`**: restructure `Scrape` and
   `Extract` so the builder is held in a local before `BuildAsync`,
   guarded by `if (browser) builder = builder.WithCdpPageLoader(new CdpLaunchOptions());`.
   Switch to `await using` for the engine so the spawned-Chromium
   process exits with the call.
3. **`WebReaper.Mcp/README.md`**: short section, "Browser mode
   (`browser=true`) auto-spawns a system Chrome / Chromium / Edge
   via the `WebReaper.Cdp` launch-and-connect overload; install a
   Chromium-family browser on the MCP host first."
4. **`CHANGELOG.md`**: entry under `## 10.0.2 (in progress)`
   describing the wiring fix and the ADR-0073 reference.

The `Map` tool does not take a `browser` parameter (sitemap-based
discovery is static; ADR-0042 doesn't drive a browser load), so it
needs no change.

## Consequences

- **`browser=true` actually works.** MCP clients (Cursor / Claude
  Desktop / Copilot Studio) can scrape JS-rendered pages without
  manual transport plumbing the protocol can't carry.
- **`WebReaper.Mcp` package gets `WebReaper.Cdp` as a transitive
  dependency.** Both packages are in lockstep on the WebReaper
  release cadence; no version-drift hazard.
- **Convention crystallises.** Two of the three agent-facing
  satellites (`WebReaper.Cli`, `WebReaper.Mcp`) now bake `WebReaper.Cdp`
  on the same rationale. A future third (a hypothetical
  `WebReaper.Mcp.AspNetCore` for hosted HTTP MCP) inherits the
  precedent without re-litigation.
- **First-call cost on a clean host.** On a machine with no Chrome
  / Chromium / Edge installed, `browser=true` fails actionably on
  the first call. v1 accepts this; v2 may add a fallback. Same
  posture the CLI has when run before `webreaper browser install`
  on a machine with no system Chrome.
- **Per-call Chromium spawn cost.** Each MCP tool invocation that
  uses `browser=true` spawns and tears down a Chromium process.
  Acceptable for the v1 single-shot tool shape (ADR-0049 bounded
  scope: "No persistent state across calls"). A long-running MCP
  session amortises this over many calls; a future "keep browser
  alive across calls" optimisation would graduate the satellite
  from stateless to stateful (out of scope for v1; observed-demand
  driven).
- **The `await using` on the engine fixes a latent leak on the
  non-browser path too.** Before this PR the MCP tools held the
  engine in a `var` and never disposed it; ADR-0058's reverse
  disposal chain (page processors → sinks → tracker → scheduler →
  spider) was never invoked. Per-invocation, the InMemory adapters
  in the no-browser path leak only memory (GC reclaims on process
  exit), but the discipline is now uniform across both branches:
  `await using var engine = await builder.BuildAsync();`.
- **Verified: launch is lazy, not eager.** `WithCdpPageLoader(CdpLaunchOptions)`
  in `WebReaper.Cdp/CdpPageLoaderBuilderExtensions.cs` captures a
  closure over `LaunchedCdpEndpoint? endpoint = null` and spawns
  Chromium inside `ProvideAsync` on the first `LoadAsync` call, not
  at the `WithCdpPageLoader` call site. So if `BuildAsync` throws
  before any page load runs, no subprocess was spawned and the
  `await using` has nothing to dispose: no leak path. The disposal
  contract for the spawn-and-run-then-throw case is already covered
  by `CloakBrowserSmokeTests` in `WebReaper.IntegrationTests` (the
  CloakBrowser stealth path composes on this exact extension).

## SemVer

**Patch (additive behaviour, fixed surface).** The `browser`
parameter previously threw on the first dynamic load; now it works.
Behaviour-delta-as-bug-fix, no public-surface change to the MCP
tools or the library; `WebReaper.Mcp.csproj` grows one
`ProjectReference` (transitive dependency note for downstream
package consumers, no breaking semver lever).

## References

- ADR-0049: `WebReaper.Mcp` satellite shape.
- ADR-0052: `WebReaper.Cdp` raw-CDP transport (the satellite this
  bakes).
- ADR-0053: `WebReaper.Playwright` (the satellite this does NOT
  bake; reasoning above).
- ADR-0055: `WebReaper.Cli` browser/stealth policy (the precedent
  this mirrors).
- ADR-0058: engine teardown disposal chain (why `await using` is
  load-bearing on the spawned-Chromium lifecycle).
- PR #122: the stale-description fix that flagged this wiring gap.
