import Link from "next/link";
import type { Metadata } from "next";
import { ArrowRight } from "lucide-react";
import { getUseCases } from "@/lib/use-cases";
import { Icon } from "@/components/icon";

export const metadata: Metadata = {
  title: "Use cases",
  description:
    "How teams use WebReaper: LLM context pipelines, price and change monitoring, autonomous agents, bot-protected scraping, distributed crawls, and structured datasets.",
};

const container = "mx-auto max-w-7xl px-4 sm:px-6 lg:px-8";

export default function UseCasesIndex() {
  const useCases = getUseCases();

  return (
    <div className="relative">
      <section className={`${container} py-16 sm:py-20`}>
        <div className="max-w-2xl">
          <h1 className="text-4xl font-extrabold tracking-tight sm:text-5xl">
            Built for real work
          </h1>
          <p className="mt-4 text-lg text-muted">
            From feeding LLMs to monitoring prices and running autonomous agents,
            WebReaper adapts to the job with the same composable pipeline.
          </p>
        </div>

        <div className="mt-14 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {useCases.map((uc) => (
            <Link
              key={uc.slug}
              href={uc.url}
              className="surface-card group flex flex-col p-6 transition hover:border-accent/40"
            >
              <div className="flex h-11 w-11 items-center justify-center rounded-lg border border-border bg-accent/10 text-accent">
                <Icon name={uc.icon} className="h-5 w-5" />
              </div>
              <h2 className="mt-5 text-lg font-semibold tracking-tight">
                {uc.title}
              </h2>
              <p className="mt-2 flex-1 text-sm leading-relaxed text-muted">
                {uc.description}
              </p>
              <span className="mt-4 inline-flex items-center gap-1 text-sm font-medium text-accent opacity-0 transition group-hover:opacity-100">
                Read more <ArrowRight className="h-3.5 w-3.5" />
              </span>
            </Link>
          ))}
        </div>
      </section>
    </div>
  );
}
