"use client";

import { type FormEvent, useState } from "react";
import { ArrowRight, Globe } from "lucide-react";
import { ClimbDemo } from "./climb-demo";
import { TurnstileWidget } from "./turnstile";
import { WaitlistNudge } from "./waitlist-nudge";
import { CLIMB_SCRIPTS } from "@/lib/playground/climb-events";

// The hero terminal does both jobs with one chrome: a recorded climb plays on
// load, then a "Try it on a real site" CTA turns the SAME terminal's command
// line into an editable input. The user types any URL; the climb (HTTP -> headed
// Chromium through a residential proxy) streams back in place through the
// same-origin Tier B gate (/api/playground/tier-b/scrape). No email up front
// (run free); a waitlist nudge appears after the run.
const TURNSTILE_SITE_KEY = process.env.NEXT_PUBLIC_TURNSTILE_SITE_KEY;
const TIERB_ENDPOINT = "/api/playground/tier-b/scrape";

// Example sites that show the climb best (each challenges a plain HTTP fetch, so
// the climb escalates). Pure suggestions; the input accepts any URL.
const EXAMPLES = [
  { label: "rozetka.com.ua", url: "https://hard.rozetka.com.ua/ua/ups/c80108/" },
  { label: "leboncoin.fr", url: "https://www.leboncoin.fr" },
  { label: "indeed.com", url: "https://www.indeed.com" },
] as const;

/** Accept bare hostnames too: prepend https:// and validate the scheme. */
function normalizeUrl(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  const withScheme = /^https?:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
  try {
    const u = new URL(withScheme);
    return u.protocol === "http:" || u.protocol === "https:" ? u.href : null;
  } catch {
    return null;
  }
}

type Phase = "canned" | "editing" | "running";

/**
 * The landing-page hero climb. Recorded climb -> CTA -> editable command line ->
 * live run -> waitlist nudge, all in one terminal. The live path (CTA onward)
 * only appears when `live` is true (NEXT_PUBLIC_PLAYGROUND_TIERB_LIVE=1); until
 * then the hero is a pure recording.
 */
export function HeroClimb({ live, className }: { live: boolean; className?: string }) {
  const [phase, setPhase] = useState<Phase>("canned");
  const [cannedDone, setCannedDone] = useState(false);
  const [input, setInput] = useState("");
  const [run, setRun] = useState<{ url: string } | null>(null);
  const [runKey, setRunKey] = useState(0);
  const [runConcluded, setRunConcluded] = useState(false);
  // `token` is the fresh Turnstile token; `activeToken` is the snapshot the live
  // run streams with, so a widget reset cannot re-fire an in-flight run.
  const [token, setToken] = useState<string | null>(null);
  const [activeToken, setActiveToken] = useState<string | null>(null);

  const needsToken = Boolean(TURNSTILE_SITE_KEY);
  const normalized = normalizeUrl(input);
  const canRun = Boolean(normalized) && (!needsToken || Boolean(token));

  const submitUrl = (e: FormEvent) => {
    e.preventDefault();
    if (!normalized || (needsToken && !token)) return;
    setActiveToken(token);
    setRun({ url: normalized });
    setRunConcluded(false);
    setRunKey((n) => n + 1);
    setPhase("running");
  };

  const scrapeAnother = () => {
    setRun(null);
    setRunConcluded(false);
    setToken(null);
    setInput("");
    setPhase("editing");
  };

  // The editable command line, handed to the terminal as its prompt slot so the
  // input lives inside the same chrome (matching `❯ webreaper scrape <url>`).
  const editablePrompt = (
    <form onSubmit={submitUrl} className="flex flex-wrap items-center gap-x-2 gap-y-1.5">
      <label className="flex min-w-0 flex-1 items-center gap-2">
        <span className="shrink-0 text-zinc-200">webreaper scrape</span>
        <input
          autoFocus
          type="text"
          inputMode="url"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="https://your-site.com"
          aria-label="URL to scrape"
          className="min-w-0 flex-1 border-0 border-b border-white/15 bg-transparent pb-0.5 text-accent caret-accent outline-none transition placeholder:text-zinc-600 focus:border-accent/60"
        />
      </label>
      <button
        type="submit"
        disabled={!canRun}
        className="inline-flex shrink-0 items-center gap-1 rounded-md bg-accent/15 px-2.5 py-1 text-[11px] font-medium text-accent transition hover:bg-accent/25 disabled:cursor-not-allowed disabled:opacity-40"
      >
        run <ArrowRight className="h-3 w-3" />
      </button>
    </form>
  );

  return (
    <div className={className}>
      {phase === "canned" && (
        <ClimbDemo script={CLIMB_SCRIPTS[0]} onConclude={() => setCannedDone(true)} />
      )}
      {phase === "editing" && <ClimbDemo prompt={editablePrompt} />}
      {phase === "running" && run && (
        <ClimbDemo
          key={runKey}
          liveUrl={run.url}
          turnstileToken={activeToken ?? undefined}
          endpoint={TIERB_ENDPOINT}
          onConclude={() => setRunConcluded(true)}
        />
      )}

      {/* CTA: appears only after the recorded climb finishes (live mode only). */}
      {live && phase === "canned" && cannedDone && (
        <div className="mt-5 flex flex-col items-center gap-2">
          <button
            type="button"
            onClick={() => setPhase("editing")}
            className="group inline-flex items-center gap-2 rounded-lg border border-accent/30 bg-accent/10 px-4 py-2.5 text-sm font-medium text-accent transition hover:border-accent/50 hover:bg-accent/15"
          >
            <Globe className="h-4 w-4" />
            Try it on a real site
            <ArrowRight className="h-4 w-4 transition group-hover:translate-x-0.5" />
          </button>
          <p className="text-xs text-muted-2">
            That climb is a recording. Run a real one against any site you choose.
          </p>
        </div>
      )}

      {/* Editing helpers: example suggestions, optional Turnstile, timing heads-up. */}
      {phase === "editing" && (
        <div className="mt-4 flex flex-col items-center gap-3">
          <div className="flex flex-wrap items-center justify-center gap-x-2 gap-y-1 text-xs text-muted-2">
            <span>Try a bot-protected site:</span>
            {EXAMPLES.map((ex, i) => (
              <span key={ex.url} className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setInput(ex.url)}
                  className="text-muted underline-offset-2 transition hover:text-accent hover:underline"
                >
                  {ex.label}
                </button>
                {i < EXAMPLES.length - 1 ? <span aria-hidden>·</span> : null}
              </span>
            ))}
          </div>
          {TURNSTILE_SITE_KEY ? (
            <TurnstileWidget
              siteKey={TURNSTILE_SITE_KEY}
              onToken={setToken}
              resetNonce={runKey}
              className="flex justify-center"
            />
          ) : null}
          <p className="max-w-md text-center text-[11px] text-muted-2">
            A real browser climb through a residential proxy. Bot-checks can take up
            to a minute.
          </p>
        </div>
      )}

      {/* After a live run: the "ask after" waitlist nudge + scrape-another. */}
      {phase === "running" && runConcluded && (
        <div className="mt-5 flex flex-col items-center gap-3">
          <div className="w-full max-w-md rounded-lg border border-border bg-surface/60 p-4 text-center">
            <p className="text-sm font-medium text-foreground">
              Want WebReaper Cloud to run crawls like this for you?
            </p>
            <WaitlistNudge className="mt-3" />
          </div>
          <button
            type="button"
            onClick={scrapeAnother}
            className="text-xs text-muted underline-offset-2 transition hover:text-accent hover:underline"
          >
            Scrape another site
          </button>
        </div>
      )}
    </div>
  );
}
