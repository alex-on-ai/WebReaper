import type { Metadata } from "next";
import { Configurator } from "@/components/playground/configurator";
import { CopyText } from "@/components/playground/copy-text";
import { siteConfig } from "@/lib/site";
import "./reaping.css";

export const metadata: Metadata = {
  title: { absolute: "WebReaper: Scrape any site. Feed your AI." },
  description:
    "WebReaper is an AI-native web scraper for .NET. One ~12 MB binary turns any site into clean Markdown or structured data. Build the pipeline and the CLI command and the C# write themselves.",
};

const seams = [
  {
    swatch: "var(--transport)",
    name: "Transport",
    iface: "IPageLoadTransport",
    body: "How a URL becomes a document. HTTP lives in core; browsers are satellite parts.",
    parts: ["HTTP (core)", "WebReaper.Playwright", "WebReaper.Cdp", "Stealth.CloakBrowser"],
  },
  {
    swatch: "var(--extract)",
    name: "Extraction",
    iface: "IContentExtractor",
    body: "How a document becomes data. Deterministic fold by default; LLM adapters when you ask.",
    parts: ["Markdown (core)", "Schema fold (core)", "WebReaper.AI inferrer", "LLM fallback / self-heal"],
  },
  {
    swatch: "var(--sink)",
    name: "Destination",
    iface: "IScraperSink",
    body: "Where records land. Fan-out is concurrent; add as many sinks as you like.",
    parts: ["Console / CSV / JSONL", "WebReaper.Mongo", "WebReaper.Redis", "WebReaper.Cosmos"],
  },
  {
    swatch: "var(--ink)",
    name: "Crawl state",
    iface: "IScheduler · IVisitedLinkTracker",
    body: "Queue and dedup live behind seams too: swap in Redis or Service Bus and multiple workers share one crawl. Same code, distributed.",
    parts: ["in-memory / file (core)", "WebReaper.Sqlite", "WebReaper.Redis", "AzureServiceBus"],
  },
];

const agents = [
  {
    title: "Claude Code skill",
    body: 'One command writes the skill; the next session routes "scrape X" intents to the CLI automatically.',
    code: `$ webreaper init
Wrote WebReaper Agent Skill to
  .claude/skills/webreaper/SKILL.md`,
  },
  {
    title: "MCP servers",
    body: "Six tools: scrape, map, extract, extract_with_prompt, extract_inferred, crawl: over stdio (Cursor, Claude Desktop) or Streamable HTTP (n8n).",
    code: `$ docker run -p 8080:8080 \\
  -e WEBREAPER_MCP_TOKEN=secret \\
  ghcr.io/alex-on-ai/webreaper-mcp-http`,
  },
  {
    title: "Autonomous agent (library)",
    body: "A goal, a URL, any IChatClient. Decide, persist, execute, with durable resume across restarts.",
    code: `var r = await LlmAgent.RunAsync(
  "https://example.com",
  goal: "find the support email",
  chatClient);`,
  },
];

const installRows = [
  { label: "macOS / Linux", cmd: siteConfig.install.brew },
  { label: "any POSIX sh", cmd: siteConfig.install.curl },
  { label: ".NET library", cmd: siteConfig.install.nuget },
];

export default function Home() {
  return (
    <div className="rl">
      <div className="wrap">
        <section className="hero" aria-labelledby="h1">
          <h1 id="h1">
            Scrape any site.
            <br />
            <em>Feed your AI.</em>
          </h1>
          <p className="lede">
            WebReaper is an AI-native web scraper for .NET. One ~12 MB binary
            turns any site, even bot-protected ones, into clean Markdown or
            structured data.{" "}
            <strong>
              Build the pipeline below and the command and the code write
              themselves.
            </strong>
          </p>
          <div className="facts" aria-label="key facts">
            <span>MIT license</span>
            <span>~12 MB single binary</span>
            <span>.NET library</span>
            <span>MCP servers</span>
            <span>bring any LLM: or none</span>
          </div>

          <Configurator />
        </section>

        <section id="stations-doc" aria-labelledby="h-seams">
          <h2 id="h-seams">Every station is a seam.</h2>
          <p className="sub">
            The builder is sugar over public interfaces. Don&rsquo;t like a
            station? Implement one interface and clip your own part into the line:
            the other stations never notice. Fifteen NuGet packages of ready parts
            ship in lockstep.
          </p>
          <div className="seams">
            {seams.map((s) => (
              <div className="seam" key={s.name}>
                <h3>
                  <span className="swatch" style={{ background: s.swatch }} />
                  {s.name}
                </h3>
                <code className="iface">{s.iface}</code>
                <p>{s.body}</p>
                <ul>
                  {s.parts.map((p) => (
                    <li key={p}>{p}</li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </section>

        <section id="climb" aria-labelledby="h-climb">
          <h2 id="h-climb">The line defends itself.</h2>
          <p className="sub">
            Most scrapers make you guess when a site is blocking you. WebReaper
            detects the block and escalates on its own: per page, paying the climb
            once per host.
          </p>
          <div className="climb">
            <div className="ladder" role="list" aria-label="escalation ladder">
              <div className="rung r1" role="listitem">
                <b>1 · HTTP</b>
                <small>fast fetch, zero overhead</small>
                <span className="st">403 challenge</span>
              </div>
              <div className="rung r2" role="listitem">
                <b>2 · Browser</b>
                <small>headless Chromium render</small>
                <span className="st">still blocked</span>
              </div>
              <div className="rung r3" role="listitem">
                <b>3 · Stealth</b>
                <small>CloakBrowser fork</small>
                <span className="st">200 OK</span>
              </div>
            </div>
            <ul className="rules">
              <li>
                Climbs only when a page <em>actually looks blocked</em>: status,
                header, or body marker.
              </li>
              <li>
                First confirmed block lifts that host&rsquo;s floor: the rest of
                the crawl starts at the working tier.
              </li>
              <li>
                Still blocked at the top? The page is <strong>dropped</strong> and
                the run exits non-zero. Challenge pages never masquerade as data.
              </li>
              <li>
                Self-hosted. No cloud round-trip. Stealth (~220 MB) downloads only
                if you opt in.
              </li>
            </ul>
          </div>
        </section>

        <section id="agents" aria-labelledby="h-agents">
          <h2 id="h-agents">Clips straight into your agent.</h2>
          <p className="sub">Three interop surfaces, one engine underneath.</p>
          <div className="agents">
            {agents.map((a) => (
              <div className="agent" key={a.title}>
                <h3>{a.title}</h3>
                <p>{a.body}</p>
                <pre>{a.code}</pre>
              </div>
            ))}
          </div>
        </section>

        <section id="install" aria-labelledby="h-install" style={{ paddingTop: 0 }}>
          <div className="install">
            <h2 id="h-install">Bolt it to your bench.</h2>
            <div className="rows">
              {installRows.map((row) => (
                <div className="irow" key={row.label}>
                  <b>{row.label}</b>
                  <code>{row.cmd}</code>
                  <CopyText text={row.cmd} />
                </div>
              ))}
            </div>
            <p className="fine">
              Windows binaries on{" "}
              <a
                href={`${siteConfig.links.github}/releases/latest`}
                target="_blank"
                rel="noreferrer noopener"
              >
                GitHub Releases
              </a>{" "}
              · six platforms · macOS builds are Apple-notarized · no Docker, no
              signup, no metering.
            </p>
          </div>
        </section>
      </div>
    </div>
  );
}
