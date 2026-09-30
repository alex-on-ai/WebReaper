import Link from "next/link";
import type { Metadata } from "next";
import { getPosts, formatDate } from "@/lib/blog";

export const metadata: Metadata = {
  title: "Blog",
  description:
    "Engineering notes, guides, and announcements from the WebReaper project.",
};

const container = "mx-auto max-w-4xl px-4 sm:px-6 lg:px-8";

export default function BlogIndex() {
  const posts = getPosts();
  const [featured, ...rest] = posts;

  return (
    <div className="relative">
      <section className={`${container} py-16 sm:py-20`}>
        <header className="max-w-2xl">
          <h1 className="text-4xl font-extrabold tracking-tight sm:text-5xl">
            Blog
          </h1>
          <p className="mt-4 text-lg text-muted">
            Engineering notes, deep dives, and announcements.
          </p>
        </header>

        {featured ? (
          <Link
            href={featured.url}
            className="surface-card group mt-12 block p-8 transition hover:border-accent/40"
          >
            <div className="flex items-center gap-3 text-xs text-muted-2">
              <span>{formatDate(featured.date)}</span>
              <span>·</span>
              <span>{featured.metadata.readingTime} min read</span>
            </div>
            <h2 className="mt-3 text-2xl font-semibold tracking-tight group-hover:text-accent">
              {featured.title}
            </h2>
            <p className="mt-2 max-w-2xl text-muted">{featured.description}</p>
          </Link>
        ) : null}

        <div className="mt-6 grid gap-4 sm:grid-cols-2">
          {rest.map((post) => (
            <Link
              key={post.slug}
              href={post.url}
              className="surface-card group flex flex-col p-6 transition hover:border-accent/40"
            >
              <div className="flex items-center gap-3 text-xs text-muted-2">
                <span>{formatDate(post.date)}</span>
                <span>·</span>
                <span>{post.metadata.readingTime} min read</span>
              </div>
              <h2 className="mt-3 text-lg font-semibold tracking-tight group-hover:text-accent">
                {post.title}
              </h2>
              <p className="mt-2 flex-1 text-sm leading-relaxed text-muted">
                {post.description}
              </p>
            </Link>
          ))}
        </div>
      </section>
    </div>
  );
}
