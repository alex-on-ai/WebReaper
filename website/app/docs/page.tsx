import Link from "next/link";
import type { Metadata } from "next";
import { ArrowRight, BookOpen } from "lucide-react";
import { getDocsNav } from "@/lib/docs";

export const metadata: Metadata = {
  title: "Documentation",
  description:
    "Learn how to scrape, crawl, and extract structured data with WebReaper, from the CLI to the .NET library and the AI features.",
};

export default function DocsIndex() {
  const nav = getDocsNav();

  return (
    <div className="py-10 lg:py-12">
      <div className="flex items-center gap-2 text-sm font-medium text-accent">
        <BookOpen className="h-4 w-4" /> Documentation
      </div>
      <h1 className="mt-3 text-3xl font-extrabold tracking-tight sm:text-4xl">
        Everything you need to ship a scraper
      </h1>
      <p className="mt-3 max-w-2xl text-lg text-muted">
        Start with the 60-second quickstart, then go deeper on schema
        extraction, the AI features, browser rendering, and distributed crawls.
      </p>

      <Link
        href="/docs/getting-started"
        className="mt-6 inline-flex items-center gap-2 rounded-lg border-2 border-border-strong bg-accent px-5 py-2.5 text-sm font-semibold text-accent-foreground shadow-[3px_3px_0_var(--shadow-ink)] transition-all duration-150 hover:-translate-x-0.5 hover:-translate-y-0.5 hover:bg-accent-strong hover:shadow-[5px_5px_0_var(--shadow-ink)]"
      >
        Get started <ArrowRight className="h-4 w-4" />
      </Link>

      <div className="mt-12 grid gap-6 sm:grid-cols-2">
        {nav.map((group) => (
          <div key={group.category} className="surface-card p-6">
            <h2 className="text-sm font-semibold uppercase tracking-wider text-muted-2">
              {group.category}
            </h2>
            <ul className="mt-4 space-y-2.5">
              {group.items.map((item) => (
                <li key={item.url}>
                  <Link
                    href={item.url}
                    className="group flex items-center justify-between text-sm text-muted transition hover:text-foreground"
                  >
                    {item.title}
                    <ArrowRight className="h-3.5 w-3.5 opacity-0 transition group-hover:opacity-100" />
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>
    </div>
  );
}
