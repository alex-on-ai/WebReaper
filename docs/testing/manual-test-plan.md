# WebReaper manual test plan

Companion to the automated suites. Covers what resists cheap automation: the
install matrix, real anti-bot sites, MCP-in-client, the example projects, and
the per-RID AOT binaries. Walk this before a release.

The automated tiers below already cover extraction, crawl/follow, retry,
storage adapters, browser transports, AI, MCP, and scale — see
[§ Automated tiers](#automated-tiers) for how to run each.

---

## Automated tiers

Every automated test carries a `[Trait("Category", …)]`. Run a tier with
`--filter "Category=<name>"`.

| Category | What | Needs | In gate? |
|---|---|---|---|
| `LocalServer` | Extraction, crawl, retry, SQLite adapter — vs the in-process Kestrel site | nothing | **yes** |
| `Cli` | `scrape`/`map`/`version`/exit codes — real CLI subprocess | nothing | **yes** |
| `Container` | Redis + Mongo sinks/schedulers round-trip | Docker | on-demand |
| `Browser` | Playwright + CDP render JS (`/spa`) | a browser | on-demand |
| `Llm` | schema inference + `UseAi`, both providers (Anthropic.SDK + M.E.AI.OpenAI, single graph @ abstractions 10.3.0) | `ANTHROPIC_API_KEY` and/or `OPENAI_API_KEY` | on-demand |
| `Mcp` | scrape/map/extract over stdio | nothing | on-demand |
| `Perf` | 500-page scale (exact count) | nothing | on-demand |
| `LiveSite` | real-internet crawl (alexpavlov.dev) | network | no (flaky) |

```bash
cd /path/to/WebReaper
dotnet build

# Gate (deterministic, fast)
dotnet test WebReaper.Tests/WebReaper.IntegrationTests --filter "Category=LocalServer"
dotnet test WebReaper.Tests/WebReaper.Cli.Tests       --filter "Category=Cli"

# On-demand
docker info >/dev/null            # Container tier needs a running Docker
dotnet test WebReaper.Tests/WebReaper.IntegrationTests --filter "Category=Container"
dotnet test WebReaper.Tests/WebReaper.IntegrationTests --filter "Category=Browser"
export ANTHROPIC_API_KEY=...      # and/or OPENAI_API_KEY; unset key → that provider's test vacuously passes
dotnet test WebReaper.Tests/WebReaper.IntegrationTests --filter "Category=Llm"
dotnet test WebReaper.Tests/WebReaper.IntegrationTests --filter "Category=Mcp"
dotnet test WebReaper.Tests/WebReaper.IntegrationTests --filter "Category=Perf"

# Throughput numbers (not asserted) — local synthetic ceiling
dotnet run -c Release --project WebReaper.Tests/WebReaper.Perf 500
# Network-bound, any real site: <url> <follow-css> [field=css ...]
dotnet run -c Release --project WebReaper.Tests/WebReaper.Perf \
  "https://books.toscrape.com/catalogue/page-1.html" \
  "article.product_pod h3 a" "title=.product_main h1" "price=.price_color"

# Dockerized install.sh smoke (network + Docker)
scripts/test-install.sh
```

> **Browser tier note:** the Playwright browser must match the package's
> revision. Without `pwsh`, install via the bundled node CLI:
> `node WebReaper.Tests/WebReaper.IntegrationTests/bin/Debug/net10.0/.playwright/package/cli.js install chromium`

---

## 1. Installation matrix (manual)

- [ ] **Homebrew (macOS arm64):** `brew install alex-on-ai/webreaper/webreaper` → `webreaper version`
- [ ] **Homebrew (macOS x64):** same, on Intel
- [ ] **install.sh (Linux x64):** clean box, `curl … | sh` → installs, `webreaper version`
- [ ] **install.sh (Linux arm64):** same
- [ ] **install.sh (macOS):** clean box
- [ ] **install.sh conflict (exit 6):** a `webreaper` already on PATH elsewhere → warns; `--force` overrides
- [ ] **install.sh same/older (exit 7):** re-run same version → refuses; `--force`/`--upgrade` behave
- [ ] **install.sh pinned:** `WEBREAPER_VERSION=v10.0.0 … | sh` installs that exact tag
- [ ] **install.sh checksum mismatch (exit 5):** tamper a downloaded asset → aborts (covered loosely by `scripts/test-install.sh`)
- [ ] **GitHub Releases binary (Windows x64):** download, run `webreaper.exe version`
- [ ] **GitHub Releases binary (Windows arm64):** same
- [ ] **macOS Gatekeeper (ADR-0071):** downloaded binary runs with no Gatekeeper warning (codesigned + notarized)
- [ ] **NuGet library:** `dotnet add package WebReaper` in a fresh console app, fluent builder compiles + runs
- [ ] **Confirm NOT a dotnet tool:** `dotnet tool install --global WebReaper.Cli` is NOT advertised (the CLI ships as an AOT binary, not a tool package)
- [ ] **PATH + smoke:** `webreaper version`, `webreaper help` work post-install on each OS

## 2. Browser / stealth provisioning (downloads + prompts)

- [ ] `webreaper browser install` → downloads managed Chromium to `~/.webreaper/`
- [ ] `webreaper browser path` / `browser list` report it
- [ ] `webreaper stealth install` → interactive picker; `stealth install cloakbrowser --yes` unattended
- [ ] `webreaper stealth path cloakbrowser` / `stealth list`
- [ ] `webreaper scrape <js-site> --browser` auto-spawns managed Chromium
- [ ] `webreaper scrape <site> --browser-cdp-url http://localhost:9222` connects to a BYO endpoint

## 3. Real anti-bot (CloakBrowser, ADR-0054) — manual only

- [ ] Cloudflare Turnstile page → `--stealth` passes
- [ ] reCAPTCHA v3 page → passes
- [ ] DataDome page → headed + residential proxy (partial per ADR-0054); record outcome
- [ ] Bot-check escalation: `scrape --browser` on a challenged page → Y/n prompt; `--auto-stealth` bypasses; `--no-auto-stealth` warns only

## 4. Real-world scrape spot-checks

- [ ] `webreaper scrape https://books.toscrape.com` (static) → markdown
- [ ] `webreaper scrape https://quotes.toscrape.com/js --browser` (SPA) → rendered content
- [ ] `webreaper scrape <site> --schema schema.json` → structured JSON
- [ ] `webreaper map https://<site-with-sitemap>` → discovered URLs; `--search` filters
- [ ] one real e-commerce or news article end-to-end

## 5. MCP in real clients

- [ ] **Claude Desktop:** add the `webreaper` MCP server config; restart; the 3 tools appear
- [ ] **Cursor:** same
- [ ] `scrape`, `map`, `extract` each return sane output through the client
- [ ] `extract` / `scrape` with `browser: true` renders JS (Chromium on the host)

## 6. Example projects (`Examples/`)

Run each; confirm it executes without error:

- [ ] `WebReaper.ConsoleApplication`
- [ ] `WebReaper.SchemaInferenceShowcase`
- [ ] `WebReaper.AiNativeShowcase`
- [ ] `WebReaper.ScraperWorkerService`
- [ ] `WebReaper.DistributedScraperWorkerService` (Redis/Azure SB queue)
- [ ] `WebReaper.AzureFuncs`
- [ ] `BrownsfashionScraper`

## 7. AOT binary per RID

For each published RID (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`):

- [ ] download the release artifact
- [ ] `webreaper version`, `help`, `browser list`, `stealth list`
- [ ] `webreaper scrape https://example.com` (real HTTP scrape)
