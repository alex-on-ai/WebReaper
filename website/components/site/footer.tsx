import Link from "next/link";
import { siteConfig } from "@/lib/site";
import { Logo } from "./logo";
import { GitHubIcon } from "@/components/icons";

export function Footer() {
  return (
    <footer className="relative border-t border-border bg-background-subtle">
      <div className="mx-auto max-w-7xl px-4 py-14 sm:px-6 lg:px-8">
        <div className="grid grid-cols-2 gap-10 md:grid-cols-6">
          <div className="col-span-2">
            <Logo />
            <p className="mt-4 max-w-xs text-sm leading-relaxed text-muted">
              {siteConfig.shortDescription}. A single binary, MIT licensed, with
              an LLM layer when you need it.
            </p>
            <a
              href={siteConfig.links.github}
              target="_blank"
              rel="noreferrer noopener"
              className="mt-5 inline-flex h-9 items-center gap-2 rounded-lg border border-border px-3 text-sm text-muted transition hover:border-border-strong hover:text-foreground"
            >
              <GitHubIcon className="h-4 w-4" />
              Star on GitHub
            </a>
          </div>

          {siteConfig.footerNav.map((group) => (
            <div key={group.title}>
              <h3 className="text-xs font-semibold uppercase tracking-wider text-muted-2">
                {group.title}
              </h3>
              <ul className="mt-4 space-y-3">
                {group.links.map((link) => {
                  const isExternal = /^(https?:|mailto:)/.test(link.href);
                  return (
                    <li key={link.href}>
                      {isExternal ? (
                        <a
                          href={link.href}
                          target={link.href.startsWith("mailto:") ? undefined : "_blank"}
                          rel="noreferrer noopener"
                          className="text-sm text-muted transition hover:text-foreground"
                        >
                          {link.title}
                        </a>
                      ) : (
                        <Link
                          href={link.href}
                          className="text-sm text-muted transition hover:text-foreground"
                        >
                          {link.title}
                        </Link>
                      )}
                    </li>
                  );
                })}
              </ul>
            </div>
          ))}
        </div>

        <div className="mt-12 flex flex-col items-center justify-between gap-4 border-t border-border pt-8 sm:flex-row">
          <p className="text-sm text-muted-2">
            © {new Date().getFullYear()} WebReaper. MIT licensed.
          </p>
          <p className="text-sm text-muted-2">
            Built by{" "}
            <a
              href="https://highcraft.io"
              target="_blank"
              rel="noopener"
              className="font-medium text-muted transition hover:text-foreground"
            >
              HighCraft.io
            </a>{" "}
            · Built with WebReaper, Next.js, and Flowbite.
          </p>
        </div>
      </div>
    </footer>
  );
}
