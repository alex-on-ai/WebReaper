# WebReaper.Mcp.AspNetCore

A **Streamable HTTP** [MCP](https://modelcontextprotocol.io) server host for
[WebReaper](https://github.com/pavlovtech/WebReaper). It exposes WebReaper's
scraping tools over HTTP so URL-based MCP clients (n8n, hosted agents) can
reach them. Sibling to the stdio `WebReaper.Mcp` satellite; both expose the
same tools (`scrape`, `map`, `extract`, `extract_with_prompt`, `crawl`).

Use the stdio `WebReaper.Mcp` for local agents that spawn a process (Cursor,
Claude Desktop). Use this package when the client connects over a URL. See
ADR-0086.

## Run

```bash
dotnet run --project WebReaper.Mcp.AspNetCore
# then point a Streamable-HTTP MCP client at the printed URL
```

The container image (Chromium baked in) is the recommended way to self-host.
Each release publishes it to GHCR as `ghcr.io/alex-on-ai/webreaper-mcp-http`:

```bash
docker run --rm -p 8080:8080 -e WEBREAPER_MCP_TOKEN=change-me \
  ghcr.io/alex-on-ai/webreaper-mcp-http:latest
# or build it: docker build -f WebReaper.Mcp.AspNetCore/Dockerfile -t webreaper-mcp-http .
```

See [docs/mcp-http-quickstart.md](../docs/mcp-http-quickstart.md) for the n8n
wiring and a `docker-compose.example.yml` (server + optional browser sidecar).

## Configuration (environment)

| Variable | Purpose |
|---|---|
| `WEBREAPER_MCP_TOKEN` | Bearer token required on every request. Mandatory when binding a non-loopback interface. |
| `WEBREAPER_CDP_URL` | Connect `browser=true` calls to this CDP endpoint (a shared Chromium / browserless sidecar) instead of launching. |
| `WEBREAPER_MCP_MAX_CONCURRENT_BROWSERS` | Cap on concurrent managed-Chromium launches. |
| `WEBREAPER_LLM_MODEL` / `WEBREAPER_LLM_BASE_URL` / `WEBREAPER_LLM_API_KEY` | OpenAI-compatible endpoint for `extract_with_prompt`. The key is read from the environment only. |

## License

MIT.
