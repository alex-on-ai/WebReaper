"use client";

import { useState } from "react";

/**
 * The Reaping Line configurator, a faithful React port of the concept
 * (concepts/06-pipeline). Assemble a scrape from four stations and the exact
 * CLI command + equivalent C# builder chain write themselves. Every option maps
 * to a real flag or builder call (verified against the shipped binary); a
 * whole-site crawl has no start-tier flag because the CLI auto-climbs per page
 * (ADR-0083). Styling lives in app/reaping.css under the `.rl` scope.
 */

type Scope = "page" | "site";
type Transport = "http" | "browser" | "stealth";
type Extract = "md" | "schema" | "infer" | "prompt";
type Sink = "stdout" | "file" | "mongo";

type State = {
  scope: Scope;
  transport: Transport;
  extract: Extract;
  sink: Sink;
};

const URLS: Record<Scope, string> = {
  page: "https://example.com/post",
  site: "https://example.com",
};

const esc = (s: string) =>
  s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
const stripTags = (html: string) =>
  html
    .replace(/<[^>]+>/g, "")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&amp;/g, "&");

/** The exact CLI command. Mongo is a library sink, so the CLI shows the closest
 *  equivalent (stdout you can pipe). */
function cliText(s: State): string {
  const sink = s.sink === "mongo" ? "stdout" : s.sink;
  const u = URLS[s.scope];
  const parts = ["webreaper", s.scope === "page" ? "scrape" : "crawl", u];
  if (s.scope === "page" && s.transport === "browser") parts.push("--browser");
  if (s.scope === "page" && s.transport === "stealth") parts.push("--stealth");
  if (s.extract === "schema") parts.push("--schema", "schema.json");
  if (s.extract === "infer")
    parts.push(
      "--infer",
      '"title, author, date"',
      "--model",
      "gpt-4o-mini",
      "--llm-url",
      "https://api.openai.com/v1",
    );
  if (s.extract === "prompt")
    parts.push(
      "--prompt",
      '"title and author"',
      "--model",
      "gpt-4o-mini",
      "--llm-url",
      "https://api.openai.com/v1",
    );
  if (s.scope === "site") parts.push("--max-pages", "500");
  let cmd = parts.join(" ");
  if (sink === "file")
    cmd +=
      s.scope === "site"
        ? " --output-dir ./out"
        : " --output page" + (s.extract === "md" ? ".md" : ".json");
  if (sink === "stdout" && s.scope === "site") cmd += " > pages.jsonl";
  return cmd;
}

function cliHTML(cmd: string): string {
  let inq = false;
  return cmd
    .split(" ")
    .map((w, i) => {
      if (i === 0 || i === 1) return `<span class="t-cmd">${esc(w)}</span>`;
      const opens = w.startsWith('"');
      const closes = w.endsWith('"');
      if (opens || inq) {
        if (opens && !closes) inq = true;
        if (closes) inq = false;
        return `<span class="t-str">${esc(w)}</span>`;
      }
      if (w.startsWith("--")) return `<span class="t-flag">${esc(w)}</span>`;
      return esc(w);
    })
    .join(" ");
}

function csHTML(s: State): string {
  const L: string[] = [];
  const browser = s.transport !== "http";
  L.push('<span class="t-kw">using</span> WebReaper.Builders;');
  if (s.transport === "browser")
    L.push('<span class="t-kw">using</span> WebReaper.Playwright;');
  if (s.transport === "stealth")
    L.push('<span class="t-kw">using</span> WebReaper.Stealth.CloakBrowser;');
  if (s.extract === "infer" || s.extract === "prompt")
    L.push('<span class="t-kw">using</span> WebReaper.AI;');
  if (s.sink === "mongo")
    L.push('<span class="t-kw">using</span> WebReaper.Mongo;');
  L.push("");
  L.push(
    '<span class="t-kw">await using var</span> engine = <span class="t-kw">await</span> <span class="t-ty">ScraperEngineBuilder</span>',
  );
  L.push(
    `    .<span class="t-me">${browser ? "CrawlWithBrowser" : "Crawl"}</span>(<span class="t-str">"${URLS[s.scope]}"</span>)`,
  );
  if (s.extract === "md") L.push('    .<span class="t-me">AsMarkdown</span>()');
  if (s.extract === "schema") {
    L.push('    .<span class="t-me">Extract</span>(<span class="t-kw">new</span>()');
    L.push("    {");
    L.push(
      '        <span class="t-kw">new</span>(<span class="t-str">"title"</span>, <span class="t-str">"h1"</span>),',
    );
    L.push(
      '        <span class="t-kw">new</span>(<span class="t-str">"author"</span>, <span class="t-str">".byline a"</span>)',
    );
    L.push("    })");
  }
  if (s.extract === "infer") {
    L.push(
      '    .<span class="t-me">ExtractInferred</span>(<span class="t-str">"title, author, date"</span>)',
    );
    L.push(
      '    .<span class="t-me">WithLlmSchemaInferrer</span>(chatClient)  <span class="t-com">// any IChatClient</span>',
    );
  }
  if (s.extract === "prompt")
    L.push(
      '    .<span class="t-me">ExtractWithPrompt</span>(chatClient, <span class="t-str">"title and author"</span>)',
    );
  if (s.scope === "site") {
    L.push(
      '    .<span class="t-me">Sweep</span>()                <span class="t-com">// recursive, on-domain</span>',
    );
    L.push('    .<span class="t-me">PageCrawlLimit</span>(<span class="t-str">500</span>)');
  }
  if (s.transport === "browser")
    L.push('    .<span class="t-me">WithPlaywrightPageLoader</span>()');
  if (s.transport === "stealth")
    L.push(
      '    .<span class="t-me">WithCloakBrowser</span>()      <span class="t-com">// auto-downloads on first use</span>',
    );
  if (s.sink === "stdout") L.push('    .<span class="t-me">WriteToConsole</span>()');
  if (s.sink === "file")
    L.push(
      `    .<span class="t-me">WriteToJsonFile</span>(<span class="t-str">"${s.scope === "site" ? "pages.json" : "page.json"}"</span>)`,
    );
  if (s.sink === "mongo")
    L.push(
      '    .<span class="t-me">WriteToMongoDb</span>(conn, <span class="t-str">"scraper"</span>, <span class="t-str">"records"</span>)',
    );
  L.push('    .<span class="t-me">BuildAsync</span>();');
  L.push("");
  L.push('<span class="t-kw">await</span> engine.<span class="t-me">RunAsync</span>();');
  return L.join("\n");
}

function notesHTML(s: State): string {
  const n: string[] = [];
  if (s.transport === "http")
    n.push(
      "<strong>Auto-climb is on by default:</strong> a page that looks blocked escalates HTTP &#8594; browser on its own, no flag.",
    );
  if (s.scope === "site" && s.transport !== "http")
    n.push(
      "<strong>A crawl has no start-tier flag:</strong> every page starts at HTTP and auto-climbs on a block; the first confirmed block lifts that host's floor for the rest of the run (ADR-0083)." +
        (s.transport === "stealth"
          ? " Let the climb include the stealth rung unattended with <code>WEBREAPER_AUTO_STEALTH=1</code>."
          : " The library can still pin a browser transport: see the C# pane."),
    );
  if (s.scope === "page" && s.transport === "stealth")
    n.push(
      "<strong>--stealth</strong> starts the climb at the stealth rung, skipping the vanilla browser. The ~220 MB backend downloads only on opt-in.",
    );
  if (s.scope === "page" && s.transport === "browser")
    n.push(
      "<strong>--browser</strong> starts the climb at the browser rung. Managed Chromium via <code>webreaper browser install</code>, or BYO with <code>--browser-cdp-url</code>.",
    );
  if (s.extract === "infer")
    n.push(
      "<strong>~1 LLM call per site:</strong> the schema is inferred once, then every page extracts deterministically. Key via <code>WEBREAPER_LLM_API_KEY</code>, never a flag.",
    );
  if (s.extract === "prompt")
    n.push(
      "<strong>LLM on every page.</strong> On a whole-site crawl the CLI asks before a large run (<code>--yes</code> skips). Key via <code>WEBREAPER_LLM_API_KEY</code>.",
    );
  if (s.extract === "schema")
    n.push(
      "Selectors in a JSON file for the CLI; inline (or source-generated from a <code>[ScrapeSchema]</code> class) in the library.",
    );
  if (s.extract === "md")
    n.push(
      "Markdown is the default: no schema, no LLM, no cost. The envelope is <code>{title, markdown, url}</code> per page.",
    );
  if (s.sink === "mongo")
    n.push(
      "<strong>MongoDB is a library sink</strong> (<code>WebReaper.Mongo</code>); the CLI pane shows the closest CLI equivalent: stdout you can pipe.",
    );
  return n.join(" ");
}

function shapeText(s: State): string {
  if (s.extract === "md")
    return s.scope === "site"
      ? "{title, markdown, url} × n pages (JSON Lines)"
      : "{title, markdown, url}";
  return s.scope === "site"
    ? "{your fields…} × n pages (JSON Lines)"
    : "{your fields…} (JSON)";
}

const STATIONS = [
  {
    key: "scope" as const,
    label: "SCOPE",
    swatch: "var(--ink)",
    opts: [
      { v: "page", name: "One page", sub: "webreaper scrape" },
      { v: "site", name: "Whole site", sub: "webreaper crawl → JSON Lines" },
    ],
  },
  {
    key: "transport" as const,
    label: "TRANSPORT",
    swatch: "var(--transport)",
    opts: [
      { v: "http", name: "HTTP", sub: "fast default; auto-climbs on block" },
      { v: "browser", name: "Browser", sub: "JS-rendered pages (Chromium)" },
      { v: "stealth", name: "Stealth", sub: "start at the CloakBrowser tier" },
    ],
  },
  {
    key: "extract" as const,
    label: "EXTRACTION",
    swatch: "var(--extract)",
    opts: [
      { v: "md", name: "Markdown", sub: "LLM-ready, no schema, $0" },
      { v: "schema", name: "CSS schema", sub: "deterministic fields, $0" },
      { v: "infer", name: "Inferred schema", sub: "LLM once per site", llm: "LLM ×1" },
      { v: "prompt", name: "Prompt", sub: "LLM on every page", llm: "LLM ×n" },
    ],
  },
  {
    key: "sink" as const,
    label: "DESTINATION",
    swatch: "var(--sink)",
    opts: [
      { v: "stdout", name: "stdout", sub: "pipe it anywhere" },
      { v: "file", name: "File", sub: "--output / shell redirect" },
      { v: "mongo", name: "MongoDB", sub: "library sink (satellite pkg)" },
    ],
  },
];

const NODES: [string, number, string][] = [
  ["SOURCE", 8, "var(--ink)"],
  ["TRANSPORT", 33, "var(--transport)"],
  ["EXTRACT", 58, "var(--extract)"],
  ["SINK", 86, "var(--sink)"],
];

function CopyBtn({ text }: { text: string }) {
  const [ok, setOk] = useState(false);
  return (
    <button
      type="button"
      className={`copy${ok ? " ok" : ""}`}
      onClick={() => {
        navigator.clipboard.writeText(text).then(
          () => {
            setOk(true);
            setTimeout(() => setOk(false), 1200);
          },
          () => {},
        );
      }}
    >
      {ok ? "copied" : "copy"}
    </button>
  );
}

export function Configurator() {
  const [state, setState] = useState<State>({
    scope: "page",
    transport: "http",
    extract: "md",
    sink: "stdout",
  });

  const cli = cliText(state);
  const cs = csHTML(state);

  return (
    <div className="rig" id="rig">
      <div className="rig-head">
        <span className="dots" aria-hidden>
          <i />
          <i />
          <i />
        </span>
        <strong>REAPING LINE: configurator</strong>
        <span className="hint">every option is a real flag or builder call</span>
      </div>

      <div className="stations" id="stations">
        {STATIONS.map((st) => (
          <fieldset className="station" key={st.key}>
            <label>
              <span className="swatch" style={{ background: st.swatch }} />
              {st.label}
            </label>
            <div className="opts">
              {st.opts.map((o) => {
                const active = state[st.key] === o.v;
                return (
                  <label
                    key={o.v}
                    className={`opt${active ? " on" : ""}`}
                  >
                    <input
                      type="radio"
                      name={st.key}
                      value={o.v}
                      checked={active}
                      onChange={() =>
                        setState((prev) => ({ ...prev, [st.key]: o.v }))
                      }
                    />
                    <span>
                      {o.name}
                      <small>{o.sub}</small>
                    </span>
                    {"llm" in o && o.llm ? (
                      <span className="llm">{o.llm}</span>
                    ) : null}
                  </label>
                );
              })}
            </div>
          </fieldset>
        ))}
      </div>

      <div className="belt" aria-hidden>
        <svg viewBox="0 0 1140 120" preserveAspectRatio="none">
          <line x1="0" y1="60" x2="1140" y2="60" stroke="var(--wire)" strokeWidth="2" />
          {NODES.map(([n, x, c]) => (
            <g key={n}>
              <circle cx={x * 11.4} cy="60" r="9" fill={c} opacity="0.16" />
              <circle
                cx={x * 11.4}
                cy="60"
                r="6"
                fill="var(--panel)"
                stroke={c}
                strokeWidth="2.5"
              />
              <text className="node-label" x={x * 11.4} y="94" textAnchor="middle">
                {n}
              </text>
            </g>
          ))}
        </svg>
        <div className="puck" style={{ "--delay": "0s" } as React.CSSProperties} />
        <div
          className="puck"
          style={{ "--delay": "-1.7s", "--rest": "55%" } as React.CSSProperties}
        />
        <div
          className="puck md"
          style={{ "--delay": "-3.4s", "--rest": "82%" } as React.CSSProperties}
        />
      </div>

      <div className="panes">
        <div className="pane">
          <div className="pane-head">
            CLI <span className="tag">one binary, no runtime</span>
            <CopyBtn text={cli} />
          </div>
          <pre aria-live="polite" dangerouslySetInnerHTML={{ __html: cliHTML(cli) }} />
          <div className="note" dangerouslySetInnerHTML={{ __html: notesHTML(state) }} />
        </div>
        <div className="pane">
          <div className="pane-head">
            C#: same line, as a library
            <CopyBtn text={stripTags(cs)} />
          </div>
          <pre dangerouslySetInnerHTML={{ __html: cs }} />
          <div className="note">
            Output shape for this config:{" "}
            <code className="mono">{shapeText(state)}</code>
          </div>
        </div>
      </div>
    </div>
  );
}
