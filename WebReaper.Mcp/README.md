# WebReaper.Mcp

MCP (Model Context Protocol) server satellite for [WebReaper](https://github.com/alex-on-ai/WebReaper).

The agent client (Cursor / Claude Desktop / Copilot Studio) spawns
this binary and communicates over stdio. Three tools:

- **`scrape`**: fetch a URL and return its main content as LLM-ready Markdown.
- **`map`**: discover URLs on a site via sitemap.xml + root-page links.
- **`extract`**: extract structured fields from a URL using a JSON schema.

The CLI ([ADR-0043](../docs/adr/0043-cli-and-agent-skill.md)) is the
*primary* agent surface; this MCP satellite is the **interop adapter**
for clients that speak MCP only.

## Install

```bash
dotnet tool install --global WebReaper.Mcp
```

Or build from source:

```bash
dotnet build WebReaper.Mcp/WebReaper.Mcp.csproj -c Release
```

## Configure your MCP client

### Claude Desktop

Edit `~/Library/Application Support/Claude/claude_desktop_config.json`
(macOS) or `%APPDATA%\Claude\claude_desktop_config.json` (Windows):

```json
{
  "mcpServers": {
    "webreaper": {
      "command": "dotnet",
      "args": ["WebReaper.Mcp.dll"]
    }
  }
}
```

### Cursor

Edit `~/.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "webreaper": {
      "command": "WebReaper.Mcp",
      "args": []
    }
  }
}
```

(Adjust the `command` to the absolute path of the installed binary.)

## Browser mode (`browser=true`)

The `scrape` and `extract` tools accept a `browser` boolean parameter.
Setting it `true` switches the page loader to a headless browser for
JS-rendered pages. The MCP server auto-spawns a system Chrome /
Chromium / Edge via [`WebReaper.Cdp`](../WebReaper.Cdp/README.md)
([ADR-0073](../docs/adr/0073-mcp-browser-transport-policy.md), mirroring
the CLI's [ADR-0055](../docs/adr/0055-cli-browser-stealth-policy.md)
policy).

Install a Chromium-family browser on the MCP host first:

- macOS: `brew install --cask google-chrome` or `brew install chromium`.
- Linux: distribution package or `apt install chromium-browser`.
- Windows: Chrome / Edge ship preinstalled or via winget.

The launcher searches `PATH` and platform-conventional install
locations (`/Applications/Google Chrome.app`, `C:\Program Files\Google\Chrome`,
etc.) for `google-chrome`, `chromium`, `chrome`, `microsoft-edge`,
`msedge`. Calls that need a browser when none is found fail with an
actionable error message.

Each MCP tool invocation spawns and tears down its own browser process
(per-call lifecycle). A Chromium instance is ~200 MB resident; if the
MCP server accepts calls from untrusted clients, run it under
appropriate process / memory limits (`ulimit`, systemd
`MemoryMax=`, container memory caps) so a flurry of `browser=true`
calls cannot exhaust host memory. Long-running stealth scenarios
should use the [WebReaper CLI](../WebReaper.Cli/) directly; the MCP
satellite stays thin and stateless.

## Why prefer the CLI / Skill over MCP?

Per the WebReaper repositioning plan, the **CLI** is ~35× cheaper than
MCP per token (no tool-schema payload, no JSON-RPC wrapping). For
agents that can run shell commands (Claude Code, etc.), prefer
`webreaper init` (the agent skill) over wiring MCP.
