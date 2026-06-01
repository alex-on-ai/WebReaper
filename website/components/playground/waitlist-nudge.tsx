"use client";

import { type FormEvent, useState } from "react";
import { ArrowRight, Check, Loader2 } from "lucide-react";

type Status = "idle" | "loading" | "done" | "error";

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * Post-run "ask after" waitlist capture for the hero climb. Submits to the same
 * /api/checkout seam the pricing table uses (createCheckout -> waitlist until
 * Stripe is wired), tagged with the `cloud` plan so leads land in one place.
 * Self-contained so the hero owns no waitlist state.
 */
export function WaitlistNudge({ className }: { className?: string }) {
  const [email, setEmail] = useState("");
  const [status, setStatus] = useState<Status>("idle");

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!EMAIL_RE.test(email.trim()) || status === "loading") return;
    setStatus("loading");
    try {
      const res = await fetch("/api/checkout", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ plan: "cloud", email: email.trim() }),
      });
      if (!res.ok) throw new Error();
      setStatus("done");
    } catch {
      setStatus("error");
    }
  };

  if (status === "done") {
    return (
      <p
        className={`flex items-center justify-center gap-2 text-sm text-accent ${className ?? ""}`}
      >
        <Check className="h-4 w-4" />
        You&apos;re on the list. We&apos;ll email you when Cloud opens up.
      </p>
    );
  }

  return (
    <form onSubmit={submit} className={`flex items-center gap-2 ${className ?? ""}`}>
      <input
        type="email"
        required
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        placeholder="you@company.com"
        aria-label="Email for the WebReaper Cloud waitlist"
        className="min-w-0 flex-1 rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground outline-none transition placeholder:text-muted-2 focus:border-accent/60"
      />
      <button
        type="submit"
        disabled={status === "loading"}
        className="inline-flex shrink-0 items-center gap-1.5 rounded-lg bg-accent px-3.5 py-2 text-sm font-medium text-accent-foreground transition hover:bg-accent-strong disabled:cursor-not-allowed disabled:opacity-50"
      >
        {status === "loading" ? (
          <Loader2 className="h-4 w-4 animate-spin" />
        ) : (
          <>
            Join <ArrowRight className="h-4 w-4" />
          </>
        )}
      </button>
      {status === "error" ? (
        <span className="sr-only" role="alert">
          Something went wrong. Please try again.
        </span>
      ) : null}
    </form>
  );
}
