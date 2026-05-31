"use client";

import { type FormEvent, useState } from "react";
import { ArrowRight, Globe } from "lucide-react";
import { ClimbDemo } from "./climb-demo";
import { TurnstileWidget } from "./turnstile";
import { CLIMB_SCRIPTS } from "@/lib/playground/climb-events";

// The hero terminal does both jobs with one chrome: a recorded climb plays on
// load, and a "Try it on a real site" CTA swaps the same terminal to a live
// Tier B run (HTTP -> headed Chromium through a residential proxy), streamed
// through the same-origin gate (/api/playground/tier-b/scrape) which captures
// the email, rate-limits, verifies Turnstile, and proxies the private backend.
const TURNSTILE_SITE_KEY = process.env.NEXT_PUBLIC_TURNSTILE_SITE_KEY;
const TIERB_ENDPOINT = "/api/playground/tier-b/scrape";

// Curated, not free-text: the backend's in-VM egress firewall is not yet
// enforced, so the server-side host allowlist is the SSRF guard, and these three
// reliably clear Cloudflare / DataDome at the browser rung in the live demo.
const TARGETS = [
  { label: "Rozetka", note: "Cloudflare", url: "https://hard.rozetka.com.ua/ua/ups/c80108/" },
  { label: "Leboncoin", note: "DataDome", url: "https://www.leboncoin.fr" },
  { label: "Indeed", note: "Cloudflare", url: "https://www.indeed.com" },
] as const;

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * The landing-page hero climb. Plays the canned `CLIMB_SCRIPTS[0]` until the
 * visitor opts into a live run; then the SAME `<ClimbDemo>` terminal re-renders
 * in live mode against the chosen target. The live affordance only appears when
 * `live` is true (NEXT_PUBLIC_PLAYGROUND_TIERB_LIVE=1), so it stays a pure
 * recorded demo until the Tier B backend is switched on.
 */
export function HeroClimb({ live, className }: { live: boolean; className?: string }) {
  const [revealed, setRevealed] = useState(false);
  const [target, setTarget] = useState<string>(TARGETS[0].url);
  const [email, setEmail] = useState("");
  // `token` is the fresh widget token; `activeToken` is the snapshot the mounted
  // ClimbDemo streams with, so resetting the widget cannot re-fire the run.
  const [token, setToken] = useState<string | null>(null);
  const [activeToken, setActiveToken] = useState<string | null>(null);
  const [run, setRun] = useState<{ url: string; email: string } | null>(null);
  const [runKey, setRunKey] = useState(0);

  const needsToken = Boolean(TURNSTILE_SITE_KEY);
  const emailOk = EMAIL_RE.test(email.trim());
  const canSubmit = emailOk && (!needsToken || Boolean(token));

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!canSubmit) return;
    setActiveToken(token);
    setRun({ url: target, email: email.trim() });
    setRunKey((n) => n + 1);
  };

  return (
    <div className={className}>
      {/* One terminal: the recorded climb until a live run is submitted, then the
          live SSE stream. Both share the exact same chrome and reducer. */}
      {run ? (
        <ClimbDemo
          key={runKey}
          liveUrl={run.url}
          email={run.email}
          turnstileToken={activeToken ?? undefined}
          endpoint={TIERB_ENDPOINT}
        />
      ) : (
        <ClimbDemo script={CLIMB_SCRIPTS[0]} />
      )}

      {live && !revealed && (
        <div className="mt-5 flex flex-col items-center gap-2">
          <button
            type="button"
            onClick={() => setRevealed(true)}
            className="group inline-flex items-center gap-2 rounded-lg border border-accent/30 bg-accent/10 px-4 py-2.5 text-sm font-medium text-accent transition hover:border-accent/50 hover:bg-accent/15"
          >
            <Globe className="h-4 w-4" />
            Try it on a real site
            <ArrowRight className="h-4 w-4 transition group-hover:translate-x-0.5" />
          </button>
          <p className="text-xs text-muted-2">
            That climb is a recording. Run a real one against a live bot-protected site.
          </p>
        </div>
      )}

      {live && revealed && (
        <div className="mt-5 animate-[reveal-in_0.3s_ease-out]">
          {/* Curated targets: each blocks a plain HTTP fetch, so the climb escalates. */}
          <div className="mx-auto mb-3 flex max-w-xl flex-wrap justify-center gap-2">
            {TARGETS.map((t) => (
              <button
                key={t.url}
                type="button"
                onClick={() => setTarget(t.url)}
                aria-pressed={target === t.url}
                className={`inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-sm transition ${
                  target === t.url
                    ? "border-accent/50 bg-accent/10 text-foreground"
                    : "border-border bg-surface/60 text-muted hover:text-foreground"
                }`}
              >
                {t.label}
                <span className="font-mono text-[10px] text-muted-2">{t.note}</span>
              </button>
            ))}
          </div>

          <form onSubmit={submit} className="mx-auto flex max-w-xl items-center gap-2">
            <input
              type="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="you@company.com"
              aria-label="Email"
              className="min-w-0 flex-1 rounded-lg border border-border bg-surface/60 px-4 py-2.5 text-sm text-foreground outline-none transition placeholder:text-muted-2 focus:border-accent/50"
            />
            <button
              type="submit"
              disabled={!canSubmit}
              className="inline-flex shrink-0 items-center gap-1.5 rounded-lg bg-accent px-4 py-2.5 text-sm font-medium text-accent-foreground transition hover:bg-accent-strong disabled:cursor-not-allowed disabled:opacity-50"
            >
              Run the climb
              <ArrowRight className="h-4 w-4" />
            </button>
          </form>

          <p className="mx-auto mt-2 max-w-xl text-center text-xs text-muted-2">
            A real browser climb through a residential proxy. Your email joins the
            WebReaper Cloud waitlist.
          </p>

          {TURNSTILE_SITE_KEY ? (
            <TurnstileWidget
              siteKey={TURNSTILE_SITE_KEY}
              onToken={setToken}
              resetNonce={runKey}
              className="mt-4 flex justify-center"
            />
          ) : null}
        </div>
      )}
    </div>
  );
}
