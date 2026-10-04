import { highlight } from "@/lib/highlight";
import { cn } from "@/lib/utils";
import { CopyButton } from "./copy-button";

/** Async server component: Shiki-highlighted, always-dark code block. */
export async function CodeBlock({
  code,
  lang = "text",
  filename,
  className,
}: {
  code: string;
  lang?: string;
  filename?: string;
  className?: string;
}) {
  const html = await highlight(code, lang);

  return (
    <div
      className={cn(
        "group relative overflow-hidden rounded-xl border border-[#2b2735] bg-[#16131f] shadow-[4px_4px_0_var(--shadow-ink)]",
        className,
      )}
    >
      {filename ? (
        <div className="flex items-center justify-between border-b border-white/10 px-4 py-2.5">
          <div className="flex items-center gap-2">
            <span className="flex gap-1.5">
              <span className="h-2.5 w-2.5 rounded-full bg-white/15" />
              <span className="h-2.5 w-2.5 rounded-full bg-white/15" />
              <span className="h-2.5 w-2.5 rounded-full bg-white/15" />
            </span>
            <span className="ml-1 font-mono text-xs text-zinc-400">
              {filename}
            </span>
          </div>
        </div>
      ) : null}
      <div className="relative">
        <div
          className="overflow-x-auto p-4 text-[13px] leading-relaxed [&_code]:font-mono [&_pre]:!m-0 [&_pre]:!bg-transparent"
          dangerouslySetInnerHTML={{ __html: html }}
        />
        <CopyButton
          value={code}
          className="absolute right-3 top-3 opacity-0 transition group-hover:opacity-100 focus-visible:opacity-100"
        />
      </div>
    </div>
  );
}
