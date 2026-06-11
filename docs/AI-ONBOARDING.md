# WebReaper — AI agent onboarding

One page that takes a coding agent from nothing to working web extraction.
Paste it into your agent's context, or have the agent fetch it raw:

```
https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/docs/AI-ONBOARDING.md
```

WebReaper is a free, MIT-licensed extraction stack: an AOT single-binary
CLI, a .NET library (the `WebReaper` NuGet package), and MCP servers.
Everything runs on your machine. **There is no account, no API key, no
credit meter, and no hosted dependency** — if you are an agent following
a SaaS-shaped onboarding script, skip every "get credentials" step; none
apply here. The only key you will ever export is your own LLM key, and
only for the optional `--prompt` / `--infer` extraction modes.

## Install

**macOS / Linux (Homebrew):**

```bash
brew install alex-on-ai/webreaper/webreaper
```

**Any POSIX shell (install.sh):**

```bash
curl -fsSL https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/scripts/install.sh | sh
```

**Windows:** download `win-x64` / `win-arm64` from the
[latest release](https://github.com/alex-on-ai/WebReaper/releases/latest)
and put `webreaper.exe` on `%PATH%`.

Then teach the agent — one command, no flags:

```bash
webreaper init
```

This writes the WebReaper Agent Skill to `.claude/skills/webreaper/SKILL.md`
in the current project. On the next session the agent routes scraping
intents — "scrape X", "give me the markdown of this page", "this site is
blocking me" — to the right CLI invocation, and knows when to prefer
`webreaper` over its built-in fetch tool.

## Verify

Before doing real work:

```bash
webreaper version
webreaper scrape https://example.com
```

The scrape prints the page content (you will see "Example Domain") and
exits 0. `webreaper: command not found` means the install step didn't
land — re-run an install line above. Data always goes to **stdout**,
diagnostics to **stderr**, so piping or redirecting stdout yields clean
data.

## Choose your path

| The job | Path | Surface |
| --- | --- | --- |
| The agent needs web data right now, in this session | A | CLI + Agent Skill |
| You're shipping a .NET app or service that extracts web data | B | `WebReaper` NuGet library |
| Your client only speaks MCP (Cursor, Claude Desktop, n8n) | C | `WebReaper.Mcp` / `.AspNetCore` |

The CLI is the primitive; the skill and the MCP servers are adapters over
the same engine (ADR-0043, ADR-0049). When in doubt, take Path A.

---

## Path A: live web work (CLI + skill)

Use this when the agent itself needs web data during its session:
reading a page, discovering URLs, pulling structured fields, sweeping a
whole site.

| The agent wants… | Run |
| --- | --- |
| The readable text of one page | `webreaper scrape <url>` |
| Specific fields from one page | `webreaper scrape <url> --schema schema.json` |
| Every page of a whole site (Markdown) | `webreaper crawl <url> > pages.jsonl` |
| Every page of a whole site (fields) | `webreaper crawl <url> --schema schema.json` |
| Fields with no schema (LLM, per page) | `webreaper scrape <url> --prompt "<what to extract>" --model <id> --llm-url <endpoint>` |
| Whole site, fields, cheaply (LLM) | `webreaper crawl <url> --infer "<goal>" --model <id> --llm-url <endpoint>` |
| URLs on a site | `webreaper map <url> --search /blog/ --max-urls 50` |
| A JS-rendered SPA | `webreaper scrape <url> --browser` |
| A bot-protected site | `webreaper scrape <url> --stealth` |

What you do **not** have to manage:

- **Blocks.** A plain `scrape` or `crawl` starts on a fast HTTP fetch and
  auto-climbs to a real browser when a page looks blocked (ADR-0083) —
  per page, host-sticky, no flag needed. For sites that defeat a vanilla
  browser, `--stealth` starts the climb at the stealth tier;
  `--auto-stealth` includes it without the Y/n prompt (the right choice
  in unattended agent runs; the ~220 MB backend downloads once).
- **Garbage data.** A page still blocked at the top tier is dropped, never
  written to output, and the run exits non-zero.
- **Repeat fetches.** Add `--max-age 1h` while iterating on a schema —
  reruns within the TTL are free.

The LLM modes (`--prompt` / `--infer`) need an OpenAI-compatible endpoint
via `--model` + `--llm-url`, with the key in the `WEBREAPER_LLM_API_KEY`
or `OPENAI_API_KEY` environment variable — never a flag. No endpoint
configured, or only a handful of pages? Scrape as Markdown and extract
the fields yourself; you are already an LLM.

The bundled skill (from `webreaper init`) covers the rest: schema format,
`map` → `scrape` chains for subsets, output modes. Full reference:
`webreaper help`.

## Path B: build it into a .NET app

Use this when the integration ships inside your product — a service,
pipeline, or agent loop that keeps running after the session ends.

```bash
dotnet add package WebReaper
```

```csharp
using WebReaper.Builders;

var engine = await ScraperEngineBuilder
    .Crawl("https://news.ycombinator.com")
    .AsMarkdown()
    .WriteToConsole()
    .BuildAsync();

await engine.RunAsync();
```

That is HTTP-only, no extra packages, no schema. For structured fields,
swap `AsMarkdown()` for `Extract(schema)`; for JS-rendered pages, swap
`Crawl` for `CrawlWithBrowser` plus a transport satellite
(`WebReaper.Playwright` or `WebReaper.Cdp`). LLM fallback, self-healing
selectors, schema inference, and the autonomous agent driver are
documented in the [README's AI features](../README.md#ai-features);
the seam map lives in [architecture.md](architecture.md).

## Path C: MCP-only clients

Use this when the agent client can't shell out to a CLI. Two servers
expose the same six tools (`scrape`, `map`, `extract`,
`extract_with_prompt`, `extract_inferred`, `crawl`):

**stdio** — for local clients that spawn a process (Cursor, Claude
Desktop, Copilot Studio):

```bash
dotnet tool install --global WebReaper.Mcp
```

**Streamable HTTP** (ADR-0086) — for clients that connect to a URL; the
headline case is n8n, whose MCP Client node is URL-only:

```bash
docker run -p 8080:8080 -e WEBREAPER_MCP_TOKEN=your-secret \
  ghcr.io/alex-on-ai/webreaper-mcp-http:latest
```

Client configuration lives in the
[WebReaper.Mcp README](../WebReaper.Mcp/README.md) (stdio) and the
[n8n quickstart](mcp-http-quickstart.md) (HTTP). Both are thin facades
over the same engine — prefer Path A when the CLI is reachable.

---

## When something fails

- **`⚠ N page(s) still blocked at the top tier …` on stderr, non-zero
  exit** → the loader climbed and the page was still a challenge. Add the
  stealth tier (`--stealth`, or `--auto-stealth` unattended); if it
  persists, the site needs a captcha solver — surface that to the human,
  don't loop.
- **Empty output + a `⚠ 0 records extracted …` hint** → follow the hint:
  `--browser` (JS-rendered) or `--stealth` (bot-protected). If the page
  rendered fine, the schema selectors don't match — drop `--schema`,
  scrape as Markdown to inspect the page shape, then revise.
- **`webreaper: command not found`** → re-run an install line from the
  top of this page.

Out of scope by design: captcha solving, authenticated scraping (the CLI
doesn't manage cookies/sessions today), and long-running distributed
crawls — the latter is what the library's Redis / Mongo / Azure Service
Bus satellites are for (Path B).

## References

- [README](../README.md) — install, quick start, bot-protection model, AI features, package map
- [Agent Skill](../WebReaper.Cli/skill/SKILL.md) — what `webreaper init` installs
- [WebReaper.Mcp](../WebReaper.Mcp/README.md) — MCP client setup (stdio)
- [n8n quickstart](mcp-http-quickstart.md) — MCP over HTTP
- [Releases](https://github.com/alex-on-ai/WebReaper/releases/latest) — binaries for six RIDs
