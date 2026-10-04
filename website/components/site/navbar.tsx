"use client";

import { useState } from "react";
import Link from "next/link";
import Image from "next/image";
import { usePathname } from "next/navigation";
import { Menu, Search, X } from "lucide-react";
import { siteConfig } from "@/lib/site";
import { GitHubIcon } from "@/components/icons";

/** The concept's sticky, ink-bordered header: wordmark, underline-on-hover nav,
 *  a compact search affordance, and the GitHub pill. Styling: .rl-* in globals. */
export function Navbar() {
  const pathname = usePathname();
  const [open, setOpen] = useState(false);

  return (
    <header className="rl-header">
      <div className="rl-shell rl-header-inner">
        <Link href="/" className="rl-wordmark" aria-label="WebReaper home">
          <Image
            src="/webreaper-mark.png"
            alt=""
            width={28}
            height={28}
            priority
            aria-hidden
          />
          WebReaper
        </Link>

        <nav className="rl-nav" aria-label="Primary">
          {siteConfig.nav.map((item) => (
            <Link
              key={item.href}
              href={item.href}
              className="rl-link"
              data-active={pathname.startsWith(item.href) ? "true" : undefined}
            >
              {item.title}
            </Link>
          ))}
          <button
            type="button"
            className="rl-iconbtn"
            aria-label="Search"
            onClick={() => window.dispatchEvent(new Event("webreaper:search"))}
          >
            <Search size={15} />
            <kbd>⌘K</kbd>
          </button>
          <a
            className="rl-gh"
            href={siteConfig.links.github}
            target="_blank"
            rel="noreferrer noopener"
          >
            <GitHubIcon className="h-4 w-4" />
            GitHub
          </a>
        </nav>

        <button
          type="button"
          className="rl-menu-btn"
          aria-label="Toggle menu"
          aria-expanded={open}
          aria-controls="rl-mobile-menu"
          onClick={() => setOpen((v) => !v)}
        >
          {open ? <X size={20} /> : <Menu size={20} />}
        </button>
      </div>

      {open ? (
        <div className="rl-mobile" id="rl-mobile-menu">
          {siteConfig.nav.map((item) => (
            <Link key={item.href} href={item.href} onClick={() => setOpen(false)}>
              {item.title}
            </Link>
          ))}
          <a
            href={siteConfig.links.github}
            target="_blank"
            rel="noreferrer noopener"
          >
            GitHub
          </a>
        </div>
      ) : null}
    </header>
  );
}
