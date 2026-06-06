export const siteConfig = {
  name: "WebReaper",
  shortDescription: "AI-native web scraping for .NET",
  description:
    "WebReaper is an AI-native web scraper for .NET. A single ~12 MB binary that turns any site into clean Markdown or structured data, with an LLM layer when you need it. No Docker, no signup, MIT licensed.",
  url: process.env.NEXT_PUBLIC_SITE_URL ?? "https://webreaper.ai",
  ogImage: "/og/default.png",
  version: "11.0.0",
  install: {
    brew: "brew install alex-on-ai/webreaper/webreaper",
    curl: "curl -fsSL https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/scripts/install.sh | sh",
    nuget: "dotnet add package WebReaper",
  },
  links: {
    github: "https://github.com/alex-on-ai/WebReaper",
    nuget: "https://www.nuget.org/packages/WebReaper",
    discussions: "https://github.com/alex-on-ai/WebReaper/discussions",
    issues: "https://github.com/alex-on-ai/WebReaper/issues",
    license: "https://github.com/alex-on-ai/WebReaper/blob/master/LICENSE.txt",
  },
  contactEmail: "business@highcraft.io",
  nav: [
    { title: "Docs", href: "/docs" },
    { title: "Use cases", href: "/use-cases" },
    { title: "Pricing", href: "/pricing" },
    { title: "Blog", href: "/blog" },
  ],
  footerNav: [
    {
      title: "Product",
      links: [
        { title: "Documentation", href: "/docs" },
        { title: "Use cases", href: "/use-cases" },
        { title: "Pricing", href: "/pricing" },
        { title: "Changelog", href: "/blog" },
      ],
    },
    {
      title: "Resources",
      links: [
        { title: "Getting started", href: "/docs/getting-started" },
        { title: "CLI reference", href: "/docs/cli" },
        { title: "AI features", href: "/docs/ai" },
        { title: "Blog", href: "/blog" },
      ],
    },
    {
      title: "Open source",
      links: [
        { title: "GitHub", href: "https://github.com/alex-on-ai/WebReaper" },
        { title: "NuGet", href: "https://www.nuget.org/packages/WebReaper" },
        { title: "Discussions", href: "https://github.com/alex-on-ai/WebReaper/discussions" },
        { title: "Report an issue", href: "https://github.com/alex-on-ai/WebReaper/issues" },
      ],
    },
    {
      title: "Company",
      links: [
        { title: "Privacy", href: "/privacy" },
        { title: "Terms", href: "/terms" },
        { title: "Contact", href: "mailto:business@highcraft.io" },
      ],
    },
  ],
} as const;

export type SiteConfig = typeof siteConfig;
