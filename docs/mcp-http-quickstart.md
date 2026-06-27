# WebReaper MCP over HTTP (n8n quickstart)

`WebReaper.Mcp.AspNetCore` (ADR-0086) serves WebReaper's scraping tools over
**Streamable HTTP**, so URL-based MCP clients like **n8n** can reach them. The
stdio `WebReaper.Mcp` satellite stays the choice for local process-spawning
agents (Cursor, Claude Desktop); use this when the client connects to a URL.

## Tools

`scrape`, `map`, `extract`, `extract_with_prompt`, and `crawl`. Same tools as the
stdio satellite (one shared `WebReaperTools`).

- `crawl` is a single long **blocking** call with no progress feedback, bounded
  by `maxPages` (default 50, hard cap 1000). For a large site prefer `map` to
  list URLs, then `scrape` each, so every call stays short.

## Run it (Docker)

The image bakes a headless Chromium, so `browser=true` works out of the box.
Each release publishes it to GHCR as
`ghcr.io/alex-on-ai/webreaper-mcp-http:<version>` (and `:latest`):

```bash
docker run --rm -p 8080:8080 \
  -e WEBREAPER_MCP_TOKEN=change-me-to-a-long-random-secret \
  ghcr.io/alex-on-ai/webreaper-mcp-http:latest
```

Or build it yourself (context = repo root):

```bash
docker build -f WebReaper.Mcp.AspNetCore/Dockerfile -t webreaper-mcp-http .
docker run --rm -p 8080:8080 -e WEBREAPER_MCP_TOKEN=change-me webreaper-mcp-http
```

`WEBREAPER_MCP_TOKEN` is **required** here: the server binds all interfaces in
the container, and the bind guard refuses a non-loopback bind without a token
(an unauthenticated network-reachable scraper is an SSRF amplifier). For local
development without Docker you can bind loopback and omit the token:

```bash
dotnet run --project WebReaper.Mcp.AspNetCore   # http://localhost:5000, no token
```

A `docker-compose.example.yml` next to the Dockerfile runs it alongside n8n,
with an optional browser sidecar.

## Wire it into n8n

In your workflow add an **MCP Client Tool** node:

| Field | Value |
|---|---|
| Server Transport | HTTP Streamable |
| MCP Endpoint URL | `http://webreaper-mcp:8080` (same Docker network) or your server URL |
| Authentication | Bearer, value = your `WEBREAPER_MCP_TOKEN` |

n8n fetches the tool list automatically. Attach the node to an AI Agent, or call
a tool directly, then iterate over results with n8n's own nodes (e.g. `map` then
a Loop/Split that `scrape`s each URL).

## Configuration (environment)

| Variable | Purpose |
|---|---|
| `WEBREAPER_MCP_TOKEN` | Bearer token. Mandatory off-loopback. |
| `WEBREAPER_CDP_URL` | Connect `browser=true` to a shared CDP / browserless sidecar instead of launching Chromium. |
| `WEBREAPER_MCP_MAX_CONCURRENT_BROWSERS` | Cap on concurrent managed-Chromium launches (default 4). |
| `WEBREAPER_LLM_MODEL` / `WEBREAPER_LLM_BASE_URL` / `WEBREAPER_LLM_API_KEY` | OpenAI-compatible endpoint for `extract_with_prompt`. The key is environment-only; `extract_with_prompt` takes an optional per-call `model` override. |

## Scope

Single-tenant, self-run. Multi-tenant hosting, per-tenant LLM/browser, and
progress streaming are out of scope (see ADR-0086).
