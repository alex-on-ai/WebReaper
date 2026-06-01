import Link from "next/link";
import {
  ArrowRight,
  Bot,
  Boxes,
  Brain,
  Check,
  Code2,
  FileJson,
  Network,
  Shield,
  Sparkles,
  Terminal as TerminalIcon,
  Workflow,
  X,
  Zap,
  GitBranch,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { CodeBlock } from "@/components/code/code-block";
import { CopyButton } from "@/components/code/copy-button";
import { HeroClimb } from "@/components/playground/hero-climb";
import { GitHubIcon } from "@/components/icons";
import { siteConfig } from "@/lib/site";

const pillars = [
  {
    icon: Zap,
    title: "Drop on PATH, run",
    body: "A single ~12 MB native binary. No Docker, no Postgres, no signup. Install and you're scraping in seconds.",
  },
  {
    icon: Sparkles,
    title: "AI-native by composition",
    body: "Markdown by default. Add schema extraction, an LLM fallback, self-healing selectors, or an autonomous agent with one .With… call.",
  },
  {
    icon: Boxes,
    title: "Bring any LLM",
    body: "OpenAI, Anthropic, Ollama, Azure OpenAI, llamafile: any IChatClient via Microsoft.Extensions.AI. Never locked in.",
  },
  {
    icon: Shield,
    title: "Bot-checks handled automatically",
    body: "Detects Cloudflare, DataDome, and PerimeterX and climbs from HTTP to a browser to stealth, per page and host-sticky. Blocked pages are dropped, never returned as data.",
  },
  {
    icon: Network,
    title: "Distributed when needed",
    body: "Swap the scheduler, tracker, and sink to Redis, MongoDB, SQLite, Azure Service Bus, or Cosmos. Same code, many workers.",
  },
  {
    icon: GitBranch,
    title: "MIT, not AGPL",
    body: "Embed it in commercial software, fork it, redistribute it. No copyleft, no service-source obligations, no license tax.",
  },
];

const features = [
  {
    eyebrow: "Markdown by default",
    title: "Any page to clean, LLM-ready Markdown",
    body: "No schema, no selectors. Point WebReaper at a URL and get back tidy Markdown you can pipe straight into a prompt or a vector store.",
    icon: Brain,
    lang: "csharp",
    code: `using WebReaper.Builders;

var engine = await ScraperEngineBuilder
    .Crawl("https://news.ycombinator.com")
    .AsMarkdown()
    .WriteToConsole()
    .BuildAsync();

await engine.RunAsync();`,
  },
  {
    eyebrow: "Typed extraction",
    title: "Structured data with compile-time schemas",
    body: "Declare fields once on a POCO. A Roslyn source generator emits a static schema and a reflection-free materializer that is AOT-clean, with no runtime guessing.",
    icon: FileJson,
    lang: "csharp",
    code: `[ScrapeSchema]
public partial class Article
{
    [ScrapeField("h1")] public string? Title { get; set; }
    [ScrapeField(".score", Type = SchemaFieldType.Integer)]
    public int Points { get; set; }
    [ScrapeField(".tag", IsList = true)]
    public List<string> Tags { get; set; } = new();
}

await ScraperEngineBuilder
    .Crawl("https://example.com/post")
    .Extract(Article.Schema)
    .BuildAsync();`,
  },
  {
    eyebrow: "Deterministic first, LLM as rescue",
    title: "Self-healing extraction that costs nothing when it works",
    body: "Cheap CSS selectors run first. If a field comes back empty, the LLM fills it and caches the fix. Stable pages cost zero LLM calls.",
    icon: Workflow,
    lang: "csharp",
    code: `using WebReaper.AI;

var engine = await ScraperEngineBuilder
    .Crawl("https://example.com")
    .Extract(Article.Schema)
    .WithLlmFallback(chatClient)   // OpenAI, Anthropic, Ollama…
    .WriteToJsonFile("articles.jsonl")
    .BuildAsync();`,
  },
];

const comparison = {
  columns: ["WebReaper", "Firecrawl", "Crawl4AI", "Crawlee"],
  rows: [
    { label: "Single self-contained binary", values: ["yes", "no", "no", "no"] },
    { label: "MIT licensed", values: ["yes", "no", "yes", "yes"] },
    { label: "LLM extraction + autonomous agent", values: ["yes", "yes", "partial", "no"] },
    { label: "Auto bot-check stealth", values: ["yes", "partial", "partial", "partial"] },
    { label: "Pluggable distributed backends", values: ["yes", "yes", "no", "yes"] },
    { label: "Runs natively in .NET / C#", values: ["yes", "no", "no", "no"] },
  ],
};

const useCases = [
  {
    icon: Brain,
    title: "LLM context pipelines",
    body: "Turn whole sites into clean Markdown to feed prompts and vector stores.",
    href: "/use-cases/llm-context-pipelines",
  },
  {
    icon: Network,
    title: "Price & change monitoring",
    body: "Schedule crawls, store to a database, fire only when a page actually changes.",
    href: "/use-cases",
  },
  {
    icon: Bot,
    title: "Autonomous research agents",
    body: "Give a goal; the agent decides which links to follow until it's met.",
    href: "/use-cases",
  },
  {
    icon: Shield,
    title: "Bot-protected catalogs",
    body: "Scrape Cloudflare and DataDome sites with auto-escalating stealth.",
    href: "/use-cases",
  },
];

const pricing = [
  {
    name: "Open Source",
    price: "Free",
    tagline: "The library, CLI, and Claude Code skill. MIT, self-hosted, forever.",
    cta: "Install now",
    href: "/docs/getting-started",
    featured: false,
  },
  {
    name: "Cloud",
    price: "Early access",
    tagline: "Hosted scheduled crawls, managed proxies and stealth, a team dashboard.",
    cta: "Join the waitlist",
    href: "/pricing",
    featured: true,
  },
  {
    name: "Enterprise",
    price: "Custom",
    tagline: "SSO, SLAs, on-prem, private satellites, and dedicated support.",
    cta: "Contact sales",
    href: "/pricing",
    featured: false,
  },
];

const faqs = [
  {
    q: "Is WebReaper really free?",
    a: "Yes. The library, the CLI, and the Claude Code skill are MIT licensed and free forever. You only pay if you later choose the optional hosted Cloud or Enterprise tiers.",
  },
  {
    q: "Do I have to use an LLM?",
    a: "No. WebReaper is deterministic by default: CSS/XPath selectors and clean Markdown need no model. The AI features are opt-in and bring-your-own LLM, so you only pay for tokens when you ask for them.",
  },
  {
    q: "How is it different from Firecrawl?",
    a: "Firecrawl is a hosted, AGPL-licensed cloud service. WebReaper is a local-first, MIT-licensed binary and .NET library. You run it yourself, embed it in commercial code, and bring any LLM.",
  },
  {
    q: "Can it handle JavaScript and bot protection?",
    a: "Yes. Swap the HTTP transport for Playwright or raw CDP for JS rendering, and pass --auto-stealth to escalate to a stealth Chromium backend on Cloudflare, DataDome, or PerimeterX challenges.",
  },
  {
    q: "Does it scale to large crawls?",
    a: "The crawl loop is parallel by design. Swap the scheduler, visited-link tracker, and result sink to Redis, MongoDB, SQLite, Azure Service Bus, or Cosmos and run many workers against shared state.",
  },
];

const container = "mx-auto max-w-7xl px-4 sm:px-6 lg:px-8";

function Mark({ value }: { value: string }) {
  if (value === "yes")
    return (
      <>
        <Check className="mx-auto h-5 w-5 text-accent" aria-hidden />
        <span className="sr-only">Supported</span>
      </>
    );
  if (value === "no")
    return (
      <>
        <X className="mx-auto h-5 w-5 text-muted-2" aria-hidden />
        <span className="sr-only">Not supported</span>
      </>
    );
  return <span className="mx-auto block text-sm text-muted-2">Partial</span>;
}

export default function Home() {
  // The live Tier B climb is gated: it is metered (a real browser through a
  // residential proxy) and email-captured. Enable with
  // NEXT_PUBLIC_PLAYGROUND_TIERB_LIVE=1 once the Tier B Fly app + edge env are
  // set; until then the hero stays a recorded demo.
  const tierBLive = process.env.NEXT_PUBLIC_PLAYGROUND_TIERB_LIVE === "1";
  return (
    <>
      {/* Hero */}
      <section className="relative overflow-hidden">
        <div className="pointer-events-none absolute inset-0 -z-10 bg-grid mask-fade-b opacity-60" />
        <div className="pointer-events-none absolute inset-x-0 top-0 -z-10 h-[600px] glow-accent" />
        <div className={`${container} pb-20 pt-20 sm:pb-28 sm:pt-28`}>
          <div className="mx-auto max-w-3xl text-center">
            <Link
              href="/blog/introducing-webreaper"
              className="inline-flex items-center gap-2 rounded-full border border-border bg-surface/60 px-3 py-1 text-xs text-muted backdrop-blur transition hover:border-accent/50 hover:text-foreground"
            >
              <span className="rounded-full bg-accent/15 px-2 py-0.5 font-medium text-accent">
                v{siteConfig.version}
              </span>
              AI-native scraping for .NET is here
              <ArrowRight className="h-3.5 w-3.5" />
            </Link>

            <h1 className="mt-6 text-balance text-4xl font-semibold tracking-tight sm:text-6xl">
              <span className="text-gradient">Scrape any site.</span>
              <br />
              <span className="text-accent-gradient">Feed your AI.</span>
            </h1>

            <p className="mx-auto mt-6 max-w-2xl text-pretty text-lg leading-relaxed text-muted">
              WebReaper is an AI-native web scraper for .NET. One ~12 MB binary
              turns any site, even bot-protected ones, into clean Markdown or
              structured data, climbing from HTTP to a browser to stealth to get
              through. Bring your own LLM when you need it. No Docker, no signup,
              MIT licensed.
            </p>

            <div className="mt-8 flex flex-col items-center justify-center gap-3 sm:flex-row">
              <Button href="/docs/getting-started" size="lg">
                Get started
                <ArrowRight className="h-4 w-4" />
              </Button>
              <Button href={siteConfig.links.github} variant="secondary" size="lg">
                <GitHubIcon className="h-4 w-4" />
                Star on GitHub
              </Button>
            </div>

            <div className="mx-auto mt-8 flex max-w-md items-center gap-3 rounded-lg border border-border bg-surface/60 px-4 py-3 font-mono text-sm backdrop-blur">
              <span className="select-none text-muted-2">$</span>
              <code className="truncate text-foreground">{siteConfig.install.brew}</code>
              <CopyButton value={siteConfig.install.brew} className="ml-auto shrink-0" />
            </div>
          </div>

          <HeroClimb live={tierBLive} className="mx-auto mt-16 max-w-3xl" />
        </div>
      </section>

      {/* Trust strip */}
      <section className="border-y border-border bg-background-subtle/50">
        <div className={`${container} py-10`}>
          <p className="text-center text-xs font-medium uppercase tracking-widest text-muted-2">
            Works with the tools you already use
          </p>
          <div className="mt-6 flex flex-wrap items-center justify-center gap-x-8 gap-y-3 text-sm font-medium text-muted">
            {[".NET", "OpenAI", "Anthropic", "Ollama", "Azure OpenAI", "Playwright", "Redis", "MongoDB"].map(
              (name) => (
                <span key={name} className="transition hover:text-foreground">
                  {name}
                </span>
              ),
            )}
          </div>
        </div>
      </section>

      {/* Pillars */}
      <section className={`${container} py-20 sm:py-28`}>
        <div className="reveal mx-auto max-w-2xl text-center">
          <h2 className="text-3xl font-semibold tracking-tight sm:text-4xl">
            Everything a modern scraper needs
          </h2>
          <p className="mt-4 text-lg text-muted">
            Batteries included, nothing locked in. Compose exactly the pipeline
            you want.
          </p>
        </div>
        <div className="reveal mt-14 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {pillars.map(({ icon: Icon, title, body }) => (
            <div
              key={title}
              className="surface-card group p-6 transition hover:border-accent/40"
            >
              <div className="flex h-11 w-11 items-center justify-center rounded-lg border border-border bg-accent/10 text-accent">
                <Icon className="h-5 w-5" />
              </div>
              <h3 className="mt-5 text-lg font-semibold tracking-tight">{title}</h3>
              <p className="mt-2 text-sm leading-relaxed text-muted">{body}</p>
            </div>
          ))}
        </div>
      </section>

      {/* AI feature deep-dive */}
      <section className="relative border-y border-border bg-background-subtle/40">
        <div className={`${container} space-y-20 py-20 sm:py-28`}>
          <div className="reveal mx-auto max-w-2xl text-center">
            <span className="inline-flex items-center gap-2 rounded-full border border-accent/30 bg-accent/10 px-3 py-1 text-xs font-medium text-accent">
              <Sparkles className="h-3.5 w-3.5" /> AI-native
            </span>
            <h2 className="mt-4 text-3xl font-semibold tracking-tight sm:text-4xl">
              Deterministic where you can, AI where you must
            </h2>
            <p className="mt-4 text-lg text-muted">
              Start with fast, free selectors. Reach for an LLM only when the
              page fights back.
            </p>
          </div>

          {features.map(({ eyebrow, title, body, icon: Icon, code, lang }, i) => (
            <div
              key={title}
              className="reveal grid items-center gap-8 lg:grid-cols-2 lg:gap-12"
            >
              <div className={i % 2 === 1 ? "lg:order-2" : ""}>
                <div className="flex items-center gap-2 text-sm font-medium text-accent">
                  <Icon className="h-4 w-4" />
                  {eyebrow}
                </div>
                <h3 className="mt-3 text-2xl font-semibold tracking-tight">
                  {title}
                </h3>
                <p className="mt-3 text-pretty leading-relaxed text-muted">
                  {body}
                </p>
              </div>
              <div className={i % 2 === 1 ? "lg:order-1" : ""}>
                <CodeBlock code={code} lang={lang} filename="Program.cs" />
              </div>
            </div>
          ))}
        </div>
      </section>

      {/* How it works (CLI) */}
      <section className={`${container} py-20 sm:py-28`}>
        <div className="reveal grid items-center gap-10 lg:grid-cols-2 lg:gap-16">
          <div>
            <span className="inline-flex items-center gap-2 rounded-full border border-border bg-surface px-3 py-1 text-xs font-medium text-muted">
              <TerminalIcon className="h-3.5 w-3.5" /> Command line
            </span>
            <h2 className="mt-4 text-3xl font-semibold tracking-tight sm:text-4xl">
              The whole toolkit, one command away
            </h2>
            <p className="mt-4 text-lg text-muted">
              Scrape a page, map a site, or crawl everything to JSON Lines. The
              CLI is Native-AOT, bot-check aware, and ships a Claude Code skill.
            </p>
            <ul className="mt-6 space-y-3 text-sm text-muted">
              {[
                "scrape: one page to Markdown or JSON",
                "map: discover the URLs on a site",
                "crawl: every on-domain page to JSON Lines",
                "init: wire the Claude Code skill",
              ].map((item) => (
                <li key={item} className="flex items-start gap-2.5">
                  <Check className="mt-0.5 h-4 w-4 shrink-0 text-accent" />
                  <span className="font-mono text-[13px]">{item}</span>
                </li>
              ))}
            </ul>
          </div>
          <CodeBlock
            filename="Terminal"
            lang="bash"
            code={`# One page as Markdown
webreaper scrape https://example.com

# Discover URLs on a site
webreaper map https://example.com --search /blog/ --max-urls 50

# Crawl a whole site to JSON Lines
webreaper crawl https://example.com > pages.jsonl

# Bot-protected? A plain scrape auto-climbs to a browser; --stealth starts at the top tier
webreaper scrape https://example.com --stealth`}
          />
        </div>
      </section>

      {/* Comparison */}
      <section className="border-y border-border bg-background-subtle/40">
        <div className={`${container} py-20 sm:py-28`}>
          <div className="mx-auto max-w-2xl text-center">
            <h2 className="text-3xl font-semibold tracking-tight sm:text-4xl">
              How WebReaper compares
            </h2>
            <p className="mt-4 text-lg text-muted">
              Local-first and MIT licensed, with the AI features people reach for
              the cloud to get.
            </p>
          </div>
          <div className="reveal mt-12 overflow-x-auto">
            <table className="w-full min-w-[640px] border-separate border-spacing-0 text-sm">
              <thead>
                <tr>
                  <th className="w-2/5 px-4 py-3 text-left font-medium text-muted" />
                  {comparison.columns.map((col, i) => (
                    <th
                      key={col}
                      className={`px-4 py-3 text-center font-semibold ${
                        i === 0 ? "text-accent" : "text-muted"
                      }`}
                    >
                      {col}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {comparison.rows.map((row) => (
                  <tr key={row.label} className="transition-colors hover:bg-surface-2/40">
                    <td className="border-t border-border px-4 py-3.5 text-left font-medium text-foreground">
                      {row.label}
                    </td>
                    {row.values.map((value, j) => (
                      <td
                        key={j}
                        className={`border-t border-border px-4 py-3.5 text-center ${
                          j === 0 ? "bg-accent/[0.04]" : ""
                        }`}
                      >
                        <Mark value={value} />
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </section>

      {/* Use cases */}
      <section className={`${container} py-20 sm:py-28`}>
        <div className="flex flex-col items-start justify-between gap-4 sm:flex-row sm:items-end">
          <div className="max-w-2xl">
            <h2 className="text-3xl font-semibold tracking-tight sm:text-4xl">
              Built for real work
            </h2>
            <p className="mt-4 text-lg text-muted">
              From LLM data pipelines to price monitoring and autonomous agents.
            </p>
          </div>
          <Link
            href="/use-cases"
            className="inline-flex items-center gap-1.5 text-sm font-medium text-accent hover:underline"
          >
            All use cases <ArrowRight className="h-4 w-4" />
          </Link>
        </div>
        <div className="reveal mt-12 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {useCases.map(({ icon: Icon, title, body, href }) => (
            <Link
              key={title}
              href={href}
              className="surface-card group flex flex-col p-6 transition hover:border-accent/40"
            >
              <Icon className="h-6 w-6 text-accent" />
              <h3 className="mt-4 font-semibold tracking-tight">{title}</h3>
              <p className="mt-2 flex-1 text-sm leading-relaxed text-muted">
                {body}
              </p>
              <span className="mt-4 inline-flex items-center gap-1 text-sm font-medium text-accent opacity-60 transition group-hover:opacity-100">
                Learn more <ArrowRight className="h-3.5 w-3.5" />
              </span>
            </Link>
          ))}
        </div>
      </section>

      {/* Pricing teaser */}
      <section className="border-t border-border bg-background-subtle/40">
        <div className={`${container} py-20 sm:py-28`}>
          <div className="mx-auto max-w-2xl text-center">
            <h2 className="text-3xl font-semibold tracking-tight sm:text-4xl">
              Free to run. Pay only to scale.
            </h2>
            <p className="mt-4 text-lg text-muted">
              The open-source core does everything locally. Hosted tiers add
              scheduling, managed infrastructure, and a team UI.
            </p>
          </div>
          <div className="reveal mt-12 grid gap-4 lg:grid-cols-3">
            {pricing.map((tier) => (
              <div
                key={tier.name}
                className={`surface-card flex flex-col p-6 ${
                  tier.featured ? "ring-1 ring-accent/40" : ""
                }`}
              >
                {tier.featured ? (
                  <span className="mb-3 inline-flex w-fit rounded-full bg-accent/15 px-2.5 py-0.5 text-xs font-medium text-accent">
                    Early access
                  </span>
                ) : null}
                <h3 className="text-lg font-semibold">{tier.name}</h3>
                <p className="mt-2 text-2xl font-semibold tracking-tight">
                  {tier.price}
                </p>
                <p className="mt-3 flex-1 text-sm leading-relaxed text-muted">
                  {tier.tagline}
                </p>
                <Button
                  href={tier.href}
                  variant={tier.featured ? "primary" : "secondary"}
                  className="mt-6 w-full"
                >
                  {tier.cta}
                </Button>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* FAQ */}
      <section className={`${container} py-20 sm:py-28`}>
        <div className="reveal mx-auto max-w-3xl">
          <h2 className="text-center text-3xl font-semibold tracking-tight sm:text-4xl">
            Frequently asked questions
          </h2>
          <div className="mt-10 divide-y divide-border border-y border-border">
            {faqs.map((faq) => (
              <details key={faq.q} className="group py-5">
                <summary className="flex cursor-pointer list-none items-center justify-between gap-4 text-left font-medium text-foreground">
                  {faq.q}
                  <span className="text-muted transition group-open:rotate-45">
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M12 5v14M5 12h14" /></svg>
                  </span>
                </summary>
                <p className="mt-3 text-pretty leading-relaxed text-muted">
                  {faq.a}
                </p>
              </details>
            ))}
          </div>
        </div>
      </section>

      {/* Final CTA */}
      <section className="relative overflow-hidden border-t border-border">
        <div className="pointer-events-none absolute inset-0 -z-10 glow-accent opacity-70" />
        <div className={`${container} py-24 text-center`}>
          <Code2 className="mx-auto h-10 w-10 text-accent" />
          <h2 className="mx-auto mt-6 max-w-2xl text-3xl font-semibold tracking-tight sm:text-4xl">
            Start scraping in 30 seconds
          </h2>
          <p className="mx-auto mt-4 max-w-xl text-lg text-muted">
            Install the binary, run one command, and pipe clean data into
            whatever comes next.
          </p>
          <div className="mx-auto mt-8 flex max-w-md flex-col items-stretch gap-3">
            <div className="flex items-center gap-3 rounded-lg border border-border bg-surface/60 px-4 py-3 font-mono text-sm backdrop-blur">
              <span className="select-none text-muted-2">$</span>
              <code className="truncate text-foreground">{siteConfig.install.brew}</code>
              <CopyButton value={siteConfig.install.brew} className="ml-auto shrink-0" />
            </div>
            <div className="flex flex-col gap-3 sm:flex-row sm:justify-center">
              <Button href="/docs/getting-started" size="lg">
                Read the docs <ArrowRight className="h-4 w-4" />
              </Button>
              <Button href={siteConfig.links.github} variant="secondary" size="lg">
                <GitHubIcon className="h-4 w-4" /> View on GitHub
              </Button>
            </div>
          </div>
        </div>
      </section>
    </>
  );
}
