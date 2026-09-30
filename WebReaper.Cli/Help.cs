namespace WebReaper.Cli;

// ADR-0043: the hand-rolled --help formatter. Reads exactly the way
// firecrawl's CLI help reads — one paragraph per command, a flat list
// of flags, no syntax-tree dump.
internal static class Help
{
    public static readonly string Top = @"
webreaper — declarative .NET web scraper / crawler, AI-native.

Usage:
  webreaper <command> [args] [flags]

Commands:
  scrape <url>          Fetch a URL; output Markdown by default,
                        or JSON if --schema is given.
  crawl <url>           Crawl a whole site recursively (every on-domain
                        page); stream JSON Lines. Markdown by default,
                        or JSON if --schema is given.
  map <url>             Discover URLs (sitemap.xml + root-page links).
  browser <sub>         Manage the managed Chromium for --browser scrapes
                        (install/path/list).
  stealth <sub>         Manage stealth Chromium forks (CloakBrowser, etc.)
                        for protected sites (install/path/list).
  init                  Write the WebReaper Agent Skill into your
                        coding-agent configuration.
  version               Print the version.
  help                  Print this message.

Flags (per-command):

  scrape:
    --schema <path>     JSON schema file (switches output to JSON).
    --prompt <text>     AI: extract per a natural-language instruction
                        (schema-free, one LLM call). Needs --model + --llm-url.
                        Mutually exclusive with --schema / --infer.
    --infer [<goal>]    AI: infer a schema once from an optional goal, then
                        extract deterministically (cheaper across many pages).
    --model <id>        LLM model id for --prompt / --infer.
    --llm-url <url>     OpenAI-compatible endpoint (e.g.
                        https://api.openai.com/v1 or http://localhost:11434/v1).
    --output <path>     Write to a file instead of stdout.
    --output-dir [dir]  Write one file per page to <dir> (default
                        ./webreaper-out); .md in Markdown mode, else .json.
    --open              Reveal the output folder when done (best-effort).
    --max-age <dur>     Cache fetched pages for this long (30s/5m/2h/1d
                        or a TimeSpan).
    --browser           Use the headless-browser transport. Layered
                        auto-spawn (ADR-0055): BYO via --browser-cdp-url,
                        else managed Chromium from `webreaper browser install`.
                        Starts the climb at the browser rung; with no flag a
                        scrape auto-climbs HTTP to browser on a block (ADR-0083).
    --browser-cdp-url   Connect to an existing CDP endpoint (e.g.
                        http://localhost:9222) for stealth backends
                        and BYO browser farms.
    --follow <selector> Follow links matching this CSS selector before
                        extraction (the Crawl chain's first step).
    --stealth           Start the climb at the stealth rung, skipping the
                        vanilla browser. Implies --browser.
    --auto-stealth      Include the stealth rung without the Y/n prompt
                        (CI / unattended). Env WEBREAPER_AUTO_STEALTH=1.
    --no-auto-stealth   Cap the climb at the browser rung; never include
                        the stealth rung (ADR-0056 escape hatch).

  crawl:
    --schema <path>     JSON schema file (switches output to JSON).
    --prompt <text>     AI: extract per a natural-language instruction
                        (schema-free, one LLM call per page). Confirms before
                        a large crawl; --yes skips. Needs --model + --llm-url.
    --infer [<goal>]    AI: infer a schema once, then extract deterministically
                        across the sweep (cheap; about one LLM call total).
    --model <id>        LLM model id for --prompt / --infer.
    --llm-url <url>     OpenAI-compatible endpoint.
    --yes               Skip the --prompt cost-confirmation prompt.
    --output <path>     Write to a file instead of stdout.
    --output-dir [dir]  Write one file per page to <dir> (default
                        ./webreaper-out); .md in Markdown mode, else .json.
    --open              Reveal the output folder when done (best-effort).
    --max-pages <n>     Cap the pages crawled (default 1000).
    --max-depth <n>     Cap the hop distance from the start URL
                        (default: unlimited).
    --include-subdomains  Follow subdomains of the start host too
                          (default: same host + www only).
    --no-sitemap        Don't seed the crawl from the site's sitemap;
                        recursive link-following only.

  browser:
    install [--revision N]   Download managed Chromium to ~/.webreaper/.
    path    [--revision N]   Print cached binary path.
    list                     List installed cached versions.

  stealth:
    install [<backend>] [--version V] [--yes]
                             Download a stealth Chromium fork (CloakBrowser etc.).
                             Interactive picker by default; --yes for unattended.
                             Reuses an existing install instead of downloading.
    path    <backend> [--version V]
                             Print the binary path. CloakBrowser is found via
                             CLOAKBROWSER_BINARY_PATH, ~/.webreaper/stealth/, or
                             the cloakbrowser npm/pip cache (~/.cloakbrowser/).
    list                     List curated stealth backends and install status.

  map:
    --search <text>     Substring filter on the returned URLs
                        (case-insensitive).
    --max-urls <n>      Cap the result count (default 1000).
    --allow-offsite     Keep URLs whose host differs from <url>.
    --no-sitemap        Skip robots.txt / sitemap.xml; root-page links
                        only.
    --no-root-page      Skip root-page link extraction; sitemap only.
    --output <path>     Write to a file instead of stdout.

  init:
    --force             Overwrite an existing SKILL.md.
    --dir <path>        Target directory (default .claude/skills/webreaper).

Environment:
  WEBREAPER_NO_UPDATE_CHECK=1   Disable the once-a-day update check. By default
                                scrape/crawl/map print an upgrade hint to stderr
                                (interactive terminals only) when a newer
                                release exists; never in CI or pipes.
  WEBREAPER_LLM_MODEL           Default for --model.
  WEBREAPER_LLM_BASE_URL        Default for --llm-url.
  WEBREAPER_LLM_API_KEY         Bearer token for the LLM endpoint (or
  (or OPENAI_API_KEY)           OPENAI_API_KEY). Read from env only, never a flag.

Examples:
  webreaper scrape https://example.com
  webreaper scrape https://example.com --schema schema.json
  webreaper crawl https://example.com > pages.jsonl
  webreaper crawl https://example.com --schema schema.json --max-pages 200
  webreaper map https://example.com --search /blog/
  webreaper scrape https://example.com --prompt ""title and author"" --model gpt-4o-mini --llm-url https://api.openai.com/v1
  webreaper crawl https://example.com --infer ""all C-level execs"" --model gpt-4o-mini --llm-url https://api.openai.com/v1
  webreaper init
".TrimStart();
}
