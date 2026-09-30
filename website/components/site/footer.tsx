import Link from "next/link";
import { siteConfig } from "@/lib/site";

/** The concept's one-row footer: an ink top rule, the credit line, a few links,
 *  and the year pinned right. Styling: .rl-* in globals. */
export function Footer() {
  const year = new Date().getFullYear();
  return (
    <footer className="rl-footer">
      <div className="rl-shell rl-footer-inner">
        <span>
          <strong style={{ color: "var(--ink)" }}>WebReaper</strong> · MIT · built
          by{" "}
          <a href="https://highcraft.io" target="_blank" rel="noreferrer noopener">
            HighCraft.io
          </a>
        </span>
        <a href={siteConfig.links.github} target="_blank" rel="noreferrer noopener">
          GitHub
        </a>
        <a href={siteConfig.links.nuget} target="_blank" rel="noreferrer noopener">
          NuGet
        </a>
        <Link href="/docs">Docs</Link>
        <Link href="/pricing">Pricing</Link>
        <Link href="/privacy">Privacy</Link>
        <span className="rl-right">© {year} WebReaper</span>
      </div>
    </footer>
  );
}
