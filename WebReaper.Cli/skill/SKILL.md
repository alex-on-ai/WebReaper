---
name: webreaper
description: |
  Scrape, crawl, or extract structured data from one or more URLs via the
  `webreaper` CLI. Outputs clean Markdown by default; JSON when a schema is
  given, or schema-free JSON when an LLM endpoint is configured (extract by
  natural-language prompt, no CSS selectors). Maps a site's URLs in one call.
  Handles JS-rendered pages and bot-protected sites (Cloudflare, DataDome,
  PerimeterX) via auto-escalating stealth.

  Use this skill whenever the user asks to:
  - scrape, crawl, or extract from a URL or site
  - get clean Markdown of a webpage (for further processing, not a summary)
  - pull specific fields from one or many pages (via a CSS schema, or by a
    natural-language prompt when an LLM endpoint is configured)
  - enumerate / discover URLs on a site
  - read a JS-rendered single-page app
  - scrape a site that's blocking direct requests

  Trigger phrases include: "scrape <site>", "crawl <site>", "extract <data>
  from <url>", "what's on <site>", "what pages does <site> have", "give me
  the markdown of <url>", "convert <url> to markdown", "pull <field> from
  <url>", "save <article> as markdown", "build a scraper for <site>", "read
  <url> into context", "this site is blocking me", "Cloudflare-protected site".

  Prefer this over the built-in WebFetch whenever the user wants:
  - Clean Markdown output to work with downstream (not just a summary in chat)
  - Structured field extraction via schema
  - Multi-page or site-wide work
  - JS-rendered or bot-protected sites

  WebFetch is the right tool only for "read this single URL and tell me about
  it" — when the output is a conversational answer, not data. Anything that
  produces an artifact (file, structured record, multi-URL result) belongs here.
---

# WebReaper — scraping & extraction CLI

Three commands. Each is one shell call; output goes to stdout unless `--output`.

## What to run

| The user wants… | Run |
|---|---|
| The readable text of one page | `webreaper scrape <url>` |
| The readable text saved to a file | `webreaper scrape <url> --output page.md` |
| Specific fields from one page | `webreaper scrape <url> --schema schema.json` |
| Every page of a whole site (Markdown) | `webreaper crawl <url> > pages.jsonl` |
| Every page of a whole site (fields) | `webreaper crawl <url> --schema schema.json` |
| Fields with no schema (LLM, per page) | `webreaper scrape <url> --prompt "<what to extract>" --model <id> --llm-url <endpoint>` |
| Whole site, fields, cheaply (LLM) | `webreaper crawl <url> --infer "<goal>" --model <id> --llm-url <endpoint>` |
| One file per page (instead of JSONL) | add `--output-dir <dir>` to `scrape` / `crawl` |
| URLs on a site | `webreaper map <url>` |
| URLs matching a substring | `webreaper map <url> --search /blog/ --max-urls 50` |
| A JS-rendered page (SPA) | `webreaper scrape <url> --browser` |
| A bot-protected site | `webreaper scrape <url> --browser --auto-stealth` |
| Fields from every linked page | `webreaper scrape <index-url> --follow "<css selector>" --schema schema.json` |

## Whole-site crawl in one command

To cover an entire site, use `crawl`: it recursively follows every on-domain
link from the start URL, extracts each page, and streams JSON Lines (one object
per page). Markdown by default; `--schema` switches to field extraction.

```bash
# Every page, as Markdown, to a file.
webreaper crawl https://example.com > pages.jsonl

# Every page, specific fields, bounded.
webreaper crawl https://example.com --schema schema.json --max-pages 200
```

Flags: `--max-pages <n>` (default 1000), `--max-depth <n>` (hops from the start
URL; default unlimited), `--include-subdomains` (default: same host + `www`
only), `--no-sitemap` (skip sitemap seeding; recursive links only),
`--schema` / `--output` (as for `scrape`). On-domain only; off-domain links
are never followed. `crawl` starts at HTTP and auto-climbs to a browser on a
*blocked* page (when one is installed), so a bot-protected site is handled; it
does not render JS-only pages or use the stealth tier, so for a JS-rendered SPA
or a site that needs stealth, scrape the pages with `scrape --browser` /
`scrape --stealth` instead.

## AI extraction (no schema): `--prompt` and `--infer`

When you don't know the page structure, or pages vary, an LLM can extract
instead of CSS selectors. Both modes need an OpenAI-compatible endpoint:
`--model <id> --llm-url <url>` (e.g. `https://api.openai.com/v1`, or
`http://localhost:11434/v1` for a local Ollama), with the API key in the
`WEBREAPER_LLM_API_KEY` or `OPENAI_API_KEY` environment variable (never a flag).

- **`--prompt "<instruction>"`**: the LLM reads each page and returns JSON for
  whatever you describe. Robust on differently-shaped pages; one LLM call per
  page, so cost scales with page count.

  ```bash
  webreaper scrape https://site.com/team --prompt "each person's name, title, email" \
    --model gpt-4o-mini --llm-url https://api.openai.com/v1
  ```

- **`--infer ["<goal>"]`**: the LLM infers a schema once from the first page,
  then extracts the rest deterministically (about one LLM call for a whole
  crawl). Cheap when pages share a shape; can yield empty fields if that
  first-page schema doesn't match a later, differently-shaped page.

  ```bash
  webreaper crawl https://site.com --infer "product name and price" \
    --model gpt-4o-mini --llm-url https://api.openai.com/v1 --max-pages 200
  ```

`--prompt`, `--infer`, and `--schema` are mutually exclusive. `crawl --prompt`
asks before a large per-page run; pass `--yes` (or run non-interactively) to skip.

Choosing the extraction strategy:

- **Selectors known / structure stable** → `--schema` (deterministic, free, fastest).
- **Structure unknown or pages vary, endpoint configured** → `--prompt`.
- **Whole site, same shape, want it cheap** → `--infer`.
- **No endpoint configured, or only a handful of pages** → scrape as Markdown
  (no flag) and extract the fields yourself; you are already an LLM. The CLI's
  `--prompt` / `--infer` earn their keep on whole-site, in-process extraction
  with a configured model, where piping every page back to you is impractical.

## Common workflow: selective multi-page extraction

`crawl` covers the *whole* site. When the user wants only a *subset* (e.g. just
blog posts), chain `map` → filter → `scrape` loop instead, so you fetch only the
matching URLs:

```bash
# 1. Discover + filter URLs.
webreaper map https://example.com/blog --search /post/ --max-urls 20 > urls.txt

# 2. Scrape each. Cache so reruns are free.
while read -r url; do
  webreaper scrape "$url" --schema schema.json --max-age 1h
done < urls.txt
```

Rule of thumb: "everything on the site" → `crawl`; "the pages matching X" →
`map --search X` then `scrape` per URL.

## Browser & stealth — when sites resist

- **JS-rendered** (React/Vue/Angular SPA, content empty in `view-source:`):
  add `--browser`. The CLI auto-spawns managed Chromium; first use may
  prompt the user to run `webreaper browser install`.
- **Bot-protected** (Cloudflare/DataDome/PerimeterX challenge, HTTP 403/429/503,
  a challenge response header, or a challenge body marker): a plain scrape
  already auto-climbs HTTP to a browser on a detected block, so many sites need
  no flag at all. For a site that defeats a vanilla browser, add the stealth
  tier: `--stealth` starts the climb there, or `--auto-stealth` (or
  `WEBREAPER_AUTO_STEALTH=1`) includes it without the startup Y/n prompt, which
  is the right choice in agent / unattended contexts. The stealth install is
  ~220 MB, fetched once at startup when the tier is included.
- **Known protected up front**: `--stealth` (implies `--browser`) starts at the
  stealth tier; `--no-auto-stealth` caps the climb at the browser tier.

If a scrape still comes back empty even with `--stealth`, the site likely needs
a captcha-solver: surface this to the user; don't keep retrying.

## Caching

`--max-age <duration>` caches fetched pages for the duration (e.g. `30s`,
`5m`, `2h`, `1d`). Use whenever iterating on a schema or running a multi-step
agent that may revisit the same URLs — reruns are free within the TTL.

## Schema format

A schema is a tree of fields. Leaves have a CSS `selector` and a `type`
(`string`, `integer`, `float`, `boolean`, `datetime`). Containers nest;
`is_list: true` produces an array. `attr` reads an HTML attribute (e.g.
`href`) instead of the element's text.

```json
{
  "field": "root",
  "children": [
    { "field": "title", "selector": "h1", "type": "string" },
    {
      "field": "items",
      "selector": ".item",
      "is_list": true,
      "children": [
        { "field": "name",  "selector": ".name",  "type": "string" },
        { "field": "url",   "selector": "a", "attr": "href", "type": "string" },
        { "field": "price", "selector": ".price", "type": "float"  }
      ]
    }
  ]
}
```

## Output

- `scrape` without `--schema` → Markdown (one document per page).
- `scrape` with `--schema` → JSON (one record per page; multiple pages = JSON
  Lines, one object per line).
- `crawl` → JSON Lines, one object per swept page (Markdown record by default,
  schema fields with `--schema`, LLM fields with `--prompt` / `--infer`).
- `map` → one URL per line.
- `--output <path>` redirects to a file instead of stdout.
- `--output-dir <dir>` writes one file per page instead (`.md` in Markdown mode,
  `.json` otherwise; default dir `./webreaper-out`); `--open` reveals the folder.

All data output is on **stdout**; diagnostics and an occasional update hint go to
**stderr**, so piping or redirecting stdout always yields clean data. The update
hint only appears on an interactive terminal (never in a pipe or CI); disable it
with `WEBREAPER_NO_UPDATE_CHECK=1`.

## Errors

Exit code `0` on success, non-zero on failure. Errors print one human-readable
line to stderr. Common cases:

- **`webreaper: command not found`** → not installed. Instruct the user:
  - **macOS / Linux**: `brew install alex-on-ai/webreaper/webreaper`, or
    `curl -fsSL https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/scripts/install.sh | sh`
  - **Windows**: download the latest archive for `win-x64` (or `win-arm64`)
    from <https://github.com/alex-on-ai/WebReaper/releases/latest>, extract
    `webreaper.exe`, place on `%PATH%`.
- **`⚠ N page(s) still blocked at the top tier …` on stderr (non-zero exit)** →
  the loader climbed to its top tier and the page was still a challenge. Add the
  stealth tier with `--stealth` (or `--auto-stealth` for unattended runs); if it
  persists, the site needs a captcha-solver.
- **Empty output + a `⚠ 0 records extracted …` hint** → follow the hint: retry
  with `--browser` (JS-rendered) or `--stealth` (bot-protected). If the page
  rendered and was not blocked, the schema selector(s) probably don't match:
  drop `--schema`, fetch as Markdown to inspect the page shape, then revise.

## Not a replacement for

- A long-running distributed crawl with shared state across processes — use
  the WebReaper library directly (Redis / Mongo / Azure Service Bus satellites).
- Captcha-solving — surface the need to the user, don't loop.
- Authenticated scraping — the CLI doesn't manage cookies / sessions today.
