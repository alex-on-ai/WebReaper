import type { Metadata } from "next";
import { Schibsted_Grotesk, Fira_Code } from "next/font/google";
import { Analytics } from "@vercel/analytics/next";
import "./globals.css";
import { siteConfig } from "@/lib/site";
import { Navbar } from "@/components/site/navbar";
import { Footer } from "@/components/site/footer";
import { SearchCommand } from "@/components/search/search-command";
import { JsonLd } from "@/components/json-ld";
import { getSearchIndex } from "@/lib/search";

// "The Reaping Line" runs on one grotesk in many weights plus a mono for
// anything the reader might copy. Both are variable Google fonts, self-hosted
// by next/font (no CDN request, no layout shift).
const sans = Schibsted_Grotesk({
  variable: "--font-schibsted",
  subsets: ["latin"],
  display: "swap",
});
const mono = Fira_Code({
  variable: "--font-fira",
  subsets: ["latin"],
  display: "swap",
});

export const metadata: Metadata = {
  metadataBase: new URL(siteConfig.url),
  title: {
    default: `${siteConfig.name}: ${siteConfig.shortDescription}`,
    template: `%s | ${siteConfig.name}`,
  },
  description: siteConfig.description,
  keywords: [
    "web scraping",
    ".NET scraper",
    "C# web crawler",
    "AI web scraping",
    "LLM data extraction",
    "Markdown scraper",
    "Firecrawl alternative",
    "WebReaper",
  ],
  creator: "WebReaper",
  openGraph: {
    type: "website",
    locale: "en_US",
    url: siteConfig.url,
    siteName: siteConfig.name,
    title: `${siteConfig.name}: ${siteConfig.shortDescription}`,
    description: siteConfig.description,
  },
  twitter: {
    card: "summary_large_image",
    title: `${siteConfig.name}: ${siteConfig.shortDescription}`,
    description: siteConfig.description,
  },
  robots: { index: true, follow: true },
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" className={`${sans.variable} ${mono.variable}`}>
      <body className="flex min-h-dvh flex-col bg-background font-sans text-foreground antialiased">
        <a
          href="#main"
          className="sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-4 focus:z-[100] focus:rounded-lg focus:bg-accent focus:px-4 focus:py-2 focus:text-sm focus:font-medium focus:text-accent-foreground"
        >
          Skip to content
        </a>
        <JsonLd />
        <Navbar />
        <main id="main" className="flex-1">
          {children}
        </main>
        <Footer />
        <SearchCommand index={getSearchIndex()} />
        <Analytics />
      </body>
    </html>
  );
}
