# `WebReaper.Mcp.AspNetCore`: Streamable HTTP transport for remote MCP clients (n8n)

## Status

proposed

## Context

`WebReaper.Mcp` (ADR-0049) ships the MCP satellite over **stdio only**, and that ADR explicitly parked HTTP for "a future `WebReaper.Mcp.AspNetCore`". ADR-0073 baked `WebReaper.Cdp` into the satellite so `browser=true` has a transport. ADR-0084 made `WebReaper.AI` AOT-clean and gave the satellite its `extract_with_prompt` tool, backed by `WebReaper.AI.Http`'s `OpenAiCompatibleChatClient` configured from `WEBREAPER_LLM_*`. The satellite today exposes four single-page tools: `scrape`, `map`, `extract`, `extract_with_prompt`.

The trigger for reopening the transport decision is **n8n** as a first-class consumer. Two verified facts make stdio a dead end there:

- **n8n's built-in MCP Client Tool node is URL-based.** Its parameters are Server Transport + MCP Endpoint URL + Authentication; the transport choices are Streamable HTTP and a deprecated legacy SSE, both endpoint-URL transports. It has no command / args / env fields, so it cannot spawn a local stdio server, and a typical n8n deployment (Docker, n8n Cloud) has no way to run the stdio binary as a child process. The entire stdio surface is unreachable from stock n8n. The same holds for any hosted or remote MCP client.
- **The C# SDK ships a clean HTTP server path.** `ModelContextProtocol.AspNetCore` provides `AddMcpServer().WithHttpTransport(...)` + `app.MapMcp()` for Streamable HTTP, with `Stateless = true` the documented recommendation for servers that make no server-to-client requests, and legacy SSE disabled by default (stateful-only, backpressure warnings). `.WithTools<T>()` reuses an existing `[McpServerToolType]` class verbatim.

So for n8n, the tool-surface gaps (no `crawl`, no stealth) are secondary; the binding constraint is that the server cannot be reached at all. This ADR adds an HTTP transport. Scope is a **self-run, single-tenant** server (the user operates it; LLM and browser are the host's); multi-tenant hosting is the deferred WebReaper Cloud milestone.

## Decision

Add a new satellite **`WebReaper.Mcp.AspNetCore`** that references `WebReaper.Mcp`, reuses the `WebReaperTools` class via `.WithTools<WebReaperTools>()`, and hosts it over Streamable HTTP. One tools class, two hosts: the existing stdio console (ADR-0049, the local-agent surface) and this ASP.NET Core HTTP host (the remote / n8n surface).

- **Transport:** Streamable HTTP via `WithHttpTransport(o => o.Stateless = true)` + `app.MapMcp()` at the root path. Legacy SSE is not enabled. Stateless fits the tools (single-shot, no server-to-client calls) and is the SDK's scalability recommendation; n8n's client negotiates Streamable HTTP against the root URL.
- **Auth:** a single bearer token from `WEBREAPER_MCP_TOKEN`, enforced by middleware (401 on mismatch). Matches n8n's Bearer auth option. An unset token is allowed only when the server binds loopback (local dev); binding a non-loopback interface without a token is refused at startup. Single token = the single-tenant scope chosen.
- **Tool surface (both):** keep the four single-page tools as the documented default, and add a fifth, **`crawl(url, max_pages, browser)`**, a bounded whole-site sweep mirroring the CLI's `CrawlCommand` (`Crawl(url).Sweep(...).PageCrawlLimit(max_pages)`). Its default `max_pages` is **small (50, not the CLI's 1000)** and its tool description states plainly that it is a single long blocking call with no progress feedback, pointing at `map` + per-URL `scrape` for large sweeps.
- **Browser host:** ship a Docker image with managed Chromium baked in (modeled on `cloud/WebReaper.PlaygroundApi/Dockerfile.tierb.lean`), **and** honor `WEBREAPER_CDP_URL`: when set, `browser=true` connects-to-existing (`WithCdpPageLoader(url)`) instead of launching, so a Chromium / browserless sidecar can run in the same compose network as n8n. The launch-vs-connect selection lives in the **shared `WebReaper.Mcp`**, not the new HTTP package, so both hosts (stdio and HTTP) honor `WEBREAPER_CDP_URL`; only the ASP.NET host glue is new (see Amendment).
- **Concurrency:** stateless means each call builds and runs its own engine (the ADR-0049 no-shared-state property already holds). A `WEBREAPER_MCP_MAX_CONCURRENT_BROWSERS` semaphore caps launched-Chromium fan-out under parallel n8n executions; when `WEBREAPER_CDP_URL` is set no launch happens, so the sidecar's own pool governs concurrency instead.
- **LLM:** the API key stays env-only (`WEBREAPER_LLM_API_KEY` / `OPENAI_API_KEY`), reusing the ADR-0084 contract; `extract_with_prompt` gains an optional per-call `model` override so a workflow can vary model without restarting the server. The key is never a tool parameter.
- **Not AOT** (ASP.NET Core host + MCP SDK reflection paths), consistent with ADR-0049's no-`PublishAot` stance for the MCP satellite. A `/health` endpoint supports container orchestration.

## Considered options (the grilling pass)

- **First-class HTTP vs a stdio bridge / community node:** chose HTTP. A `mcp-remote` / `supergateway` stdio-to-HTTP bridge, or the community `n8n-nodes-mcp` stdio node, would avoid library work, but each pushes an extra supervised process or a non-default node onto the user and still needs the binary somewhere n8n can reach. HTTP is the only path that works on stock n8n (Cloud included) and is the same seam a future hosted Cloud endpoint needs anyway.
- **Streamable HTTP vs SSE:** Streamable HTTP. SSE is legacy in both the C# SDK (off by default, stateful-only, backpressure) and n8n (deprecated). Enabling it buys nothing for new clients.
- **Separate satellite vs a flag on `WebReaper.Mcp`:** separate package. The HTTP host needs the ASP.NET Core dependency and a `WebApplication`; the stdio console host should not carry it. Sharing the `WebReaperTools` class gives reuse without coupling the two hosts or their dependency graphs.
- **`crawl` tool: ship bounded vs keep MCP strictly single-page.** n8n is itself an orchestrator, so `map` then a per-URL `scrape` loop is the timeout-safe idiom and stays the documented default. But a one-node crawl is a real convenience and trivially wired, so it ships with a small cap and a loud no-progress caveat rather than not at all. The no-streaming constraint is exactly why the cap is small and why crawl is not the default.
- **Browser host: image + CDP sidecar vs baked-only vs document-only.** Image + sidecar. Baked-only forces a large image and rules out an external pool; document-only pushes all browser setup onto whoever runs the server. Honoring `WEBREAPER_CDP_URL` also sidesteps the per-call-launch concurrency problem whenever a pool is present.
- **Auth: single bearer token vs no-auth vs multi-tenant per-key.** A network-bound scraper with no auth is an SSRF amplifier, so a token is mandatory off-loopback. Per-tenant keys are the Cloud milestone and out of scope for a self-run single-tenant server.

## Consequences

- One new package to ship and version in lockstep (the release `CANDIDATES` list): `WebReaper.Mcp.AspNetCore`, plus a published container image with Chromium.
- The stdio satellite (ADR-0049) is unchanged and stays the local-agent surface (Cursor, Claude Desktop). There are now two MCP surfaces: stdio (local spawn) and HTTP (remote / n8n), sharing one tools class.
- `browser=true` now has three host stories: baked Chromium in the image, an external CDP sidecar via `WEBREAPER_CDP_URL`, or a system Chrome on a bare host. n8n-in-Docker uses one of the first two.
- A network-bound server adds SSRF and request-volume exposure the stdio process never had, and the per-host escalating-loader cost (ADR-0083) now runs server-side. Bearer auth is mandatory off-loopback; rate-limiting is flagged as an operational concern, not built here.
- No streaming remains (the ADR-0049 bound). The `crawl` tool's long-call / timeout risk is *managed* by the small cap plus the documented `map` + `scrape` alternative, not solved. Surfacing the ADR-0085 climb-progress observer as MCP progress notifications is a possible later ADR.
- Multi-tenant auth and per-tenant LLM / browser are explicitly deferred to the WebReaper Cloud ADR; this server is single-tenant and self-run.

## Amendment (2026-06-04)

**Placement of the browser-transport selection.** The `WEBREAPER_CDP_URL` launch-vs-connect decision, and its pure selection module (`NoBrowser | LaunchManagedChromium | ConnectToCdp(url)`), land in the **shared `WebReaper.Mcp`**, not solely in the new `WebReaper.Mcp.AspNetCore` package. The original draft framed `WEBREAPER_CDP_URL` as an HTTP-host feature. Sharing it gives the stdio host CDP-sidecar support for free and keeps one browser-wiring path across both hosts, at the cost of a slightly wider stdio surface than ADR-0049 first shipped. Decided during the PRD pass; see [#239](https://github.com/pavlovtech/WebReaper/issues/239) and slice [#243](https://github.com/pavlovtech/WebReaper/issues/243).
