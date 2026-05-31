"use client";

import { useEffect, useReducer, useState } from "react";
import {
  AppWindow,
  ArrowUp,
  Ban,
  Check,
  Ghost,
  Globe,
  Loader2,
  RotateCw,
} from "lucide-react";
import {
  type ClimbEvent,
  type ClimbScript,
  type TierName,
  playScript,
  TIER_LABEL,
  TIER_ORDER,
} from "@/lib/playground/climb-events";
import { cn } from "@/lib/utils";

type TierStatus = "idle" | "active" | "blocked" | "success" | "exhausted";
type TierState = { status: TierStatus; pill?: string; reason?: string };

type State = {
  tiers: Record<TierName, TierState>;
  climbingTo: TierName | null;
  result: { title: string; markdown: string } | null;
  outcome: "running" | "won" | "lost" | "error";
  lostReason?: string;
  error?: string;
};

const TIER_ICON: Record<TierName, typeof Globe> = {
  http: Globe,
  browser: AppWindow,
  stealth: Ghost,
};

function initialState(): State {
  return {
    tiers: {
      http: { status: "idle" },
      browser: { status: "idle" },
      stealth: { status: "idle" },
    },
    climbingTo: null,
    result: null,
    outcome: "running",
  };
}

/**
 * The fold from a `ClimbEvent` to UI state. This is the only consumer of the
 * event stream, so the canned script and a future live SSE stream drive the
 * exact same reducer.
 */
function reduce(state: State, event: ClimbEvent): State {
  switch (event.kind) {
    case "request":
      return state;
    case "attempt":
      return {
        ...state,
        climbingTo: null,
        tiers: { ...state.tiers, [event.tier]: { status: "active", pill: "loading" } },
      };
    case "blocked":
      return {
        ...state,
        tiers: {
          ...state.tiers,
          [event.tier]: {
            status: "blocked",
            pill: event.status ? `${event.status} blocked` : "challenged",
            reason: event.reason,
          },
        },
      };
    case "escalate":
      return { ...state, climbingTo: event.to };
    case "success":
      return {
        ...state,
        tiers: { ...state.tiers, [event.tier]: { status: "success", pill: `${event.status} OK` } },
      };
    case "result":
      return { ...state, result: { title: event.title, markdown: event.markdown }, outcome: "won" };
    case "exhausted":
      return {
        ...state,
        outcome: "lost",
        lostReason: event.reason,
        tiers: {
          ...state.tiers,
          [event.tier]: { status: "exhausted", pill: "blocked", reason: event.reason },
        },
      };
    case "error":
      return { ...state, outcome: "error", error: event.message };
    default:
      return state;
  }
}

function rootReduce(state: State, action: ClimbEvent | { kind: "reset" }): State {
  if (action.kind === "reset") return initialState();
  return reduce(state, action);
}

function urlOf(script: ClimbScript): string {
  const req = script.events.find((e) => e.event.kind === "request");
  return req && req.event.kind === "request" ? req.event.url : "";
}

// The same-origin gate (website/app/api/playground/scrape): it verifies
// Turnstile, rate-limits, then proxies the private backend's SSE through, so the
// browser never talks to the backend directly.
const PLAYGROUND_ENDPOINT = "/api/playground/scrape";

/**
 * Drives the climb view from one of two sources, both folding into the same
 * reducer: a recorded `script` (the canned hero / demos) or a live SSE stream
 * from the gate (`liveUrl`). Exactly one is expected. `turnstileToken` is
 * forwarded to the gate as `cf` when Turnstile is configured. `endpoint` selects
 * the gate route (defaults to the Tier A scrape gate; the Tier B climb passes its
 * own route), and `email` is forwarded as the Tier B capture-gate param.
 */
export function ClimbDemo({
  script,
  liveUrl,
  turnstileToken,
  email,
  endpoint = PLAYGROUND_ENDPOINT,
  className,
}: {
  script?: ClimbScript;
  liveUrl?: string;
  turnstileToken?: string;
  email?: string;
  endpoint?: string;
  className?: string;
}) {
  const [state, dispatch] = useReducer(rootReduce, undefined, initialState);
  // Bumping runId re-fires the effect, which resets then replays / re-streams.
  const [runId, setRunId] = useState(0);
  const url = liveUrl ?? (script ? urlOf(script) : "");

  useEffect(() => {
    dispatch({ kind: "reset" });

    // Live mode: drive the reducer from the backend SSE stream.
    if (liveUrl) {
      const params = new URLSearchParams({ url: liveUrl });
      if (turnstileToken) params.set("cf", turnstileToken);
      if (email) params.set("email", email);
      const source = new EventSource(`${endpoint}?${params.toString()}`);
      source.onmessage = (e) => {
        let event: ClimbEvent;
        try {
          event = JSON.parse(e.data) as ClimbEvent;
        } catch {
          return;
        }
        dispatch(event);
        // Close on a terminal event; otherwise EventSource auto-reconnects when
        // the server closes the stream, which would re-run the scrape.
        if (event.kind === "result" || event.kind === "exhausted" || event.kind === "error") {
          source.close();
        }
      };
      source.onerror = () => {
        source.close();
        dispatch({ kind: "error", message: "Lost connection to the scrape service." });
      };
      return () => source.close();
    }

    // Canned mode: play the recorded script.
    if (!script) return;
    const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (reduceMotion) {
      // Jump straight to the final state: dispatch every event with no delay.
      const t = setTimeout(() => script.events.forEach(({ event }) => dispatch(event)), 0);
      return () => clearTimeout(t);
    }
    return playScript(script.events, dispatch);
  }, [script, liveUrl, turnstileToken, email, endpoint, runId]);

  const replay = () => setRunId((n) => n + 1);

  return (
    <div
      className={cn(
        "overflow-hidden rounded-xl border border-white/10 bg-[#0a0e13] shadow-2xl shadow-black/50",
        className,
      )}
    >
      <div className="flex items-center gap-3 border-b border-white/10 px-4 py-3">
        <span className="flex gap-1.5">
          <span className="h-3 w-3 rounded-full bg-[#ff5f57]" />
          <span className="h-3 w-3 rounded-full bg-[#febc2e]" />
          <span className="h-3 w-3 rounded-full bg-[#28c840]" />
        </span>
        <span className="mx-auto font-mono text-xs text-zinc-500">webreaper</span>
        <button
          type="button"
          onClick={replay}
          className="flex items-center gap-1.5 rounded-md px-2 py-1 font-mono text-[11px] text-zinc-500 transition hover:text-zinc-200"
          aria-label="Replay the climb"
        >
          <RotateCw className="h-3 w-3" />
          replay
        </button>
      </div>

      <div className="p-4 font-mono text-[13px] leading-relaxed sm:p-5">
        <div className="flex items-start gap-2">
          <span className="select-none text-accent">❯</span>
          <span className="break-all text-zinc-200">
            webreaper scrape {url}
          </span>
        </div>

        <ol className="mt-4 space-y-0" aria-live="polite">
          {TIER_ORDER.map((tier, i) => (
            <TierRow
              key={tier}
              tier={tier}
              state={state.tiers[tier]}
              isLast={i === TIER_ORDER.length - 1}
              nextActive={
                i < TIER_ORDER.length - 1 &&
                state.tiers[TIER_ORDER[i + 1]].status !== "idle"
              }
              climbingHere={state.climbingTo === tier}
              concluded={state.outcome !== "running"}
            />
          ))}
        </ol>

        <ResultPanel
          outcome={state.outcome}
          result={state.result}
          lostReason={state.lostReason}
          error={state.error}
        />
      </div>
    </div>
  );
}

function TierRow({
  tier,
  state,
  isLast,
  nextActive,
  climbingHere,
  concluded,
}: {
  tier: TierName;
  state: TierState;
  isLast: boolean;
  nextActive: boolean;
  climbingHere: boolean;
  concluded: boolean;
}) {
  const Icon = TIER_ICON[tier];
  const reached = state.status !== "idle";
  const danger = state.status === "blocked" || state.status === "exhausted";

  return (
    <li className="relative flex gap-3 pb-5 last:pb-0">
      {/* Left rail: the node + the connector to the next rung. */}
      <div className="flex flex-col items-center">
        <span
          className={cn(
            "flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border transition-colors duration-500",
            state.status === "idle" && "border-white/10 text-zinc-600",
            state.status === "active" && "border-accent/40 text-accent",
            state.status === "success" && "border-accent/50 bg-accent/10 text-accent",
            danger && "border-red-500/30 bg-red-500/5 text-red-400",
          )}
        >
          <Icon className="h-4 w-4" />
        </span>
        {!isLast && (
          <span
            className={cn(
              "mt-1 w-px flex-1 transition-colors duration-500",
              nextActive || climbingHere ? "bg-accent/50" : "bg-white/10",
            )}
          />
        )}
      </div>

      {/* Body: label, status pill, reason, escalation hint. */}
      <div className="min-w-0 flex-1 pt-1">
        <div className="flex items-center justify-between gap-2">
          <span className={cn("transition-colors", reached ? "text-zinc-200" : "text-zinc-600")}>
            {TIER_LABEL[tier]}
          </span>
          <StatusPill status={state.status} pill={state.pill} concluded={concluded} />
        </div>
        {state.reason && (
          <p className="mt-0.5 text-[12px] text-zinc-500">{state.reason}</p>
        )}
        {climbingHere && state.status === "idle" && (
          <p className="mt-0.5 flex items-center gap-1 text-[12px] text-accent">
            <ArrowUp className="h-3 w-3" />
            escalating…
          </p>
        )}
      </div>
    </li>
  );
}

function StatusPill({ status, pill, concluded }: { status: TierStatus; pill?: string; concluded?: boolean }) {
  if (status === "idle") {
    // Once a lower rung wins (or the whole climb concludes), an unreached rung
    // was never needed, rather than still pending.
    return (
      <span className="font-mono text-[11px] text-zinc-600">
        {concluded ? "not needed" : "queued"}
      </span>
    );
  }
  const base = "inline-flex items-center gap-1 rounded-md px-2 py-0.5 font-mono text-[11px]";
  if (status === "active") {
    return (
      <span className={cn(base, "bg-accent/10 text-accent")}>
        <Loader2 className="h-3 w-3 animate-spin" />
        {pill ?? "loading"}
      </span>
    );
  }
  if (status === "success") {
    return (
      <span className={cn(base, "bg-accent/10 text-accent")}>
        <Check className="h-3 w-3" />
        {pill}
      </span>
    );
  }
  // blocked | exhausted
  return (
    <span className={cn(base, "bg-red-500/10 text-red-300")}>
      <Ban className="h-3 w-3" />
      {pill}
    </span>
  );
}

function ResultPanel({
  outcome,
  result,
  lostReason,
  error,
}: {
  outcome: State["outcome"];
  result: State["result"];
  lostReason?: string;
  error?: string;
}) {
  if (outcome === "running") return null;

  if (outcome === "error") {
    return (
      <div className="mt-4 rounded-lg border border-red-500/20 bg-red-500/5 p-4">
        <p className="text-[13px] text-zinc-300">{error ?? "Something went wrong."}</p>
      </div>
    );
  }

  if (outcome === "lost") {
    return (
      <div className="mt-4 rounded-lg border border-red-500/20 bg-red-500/5 p-4">
        <p className="text-[13px] text-zinc-300">
          {lostReason ?? "Still blocked at the top tier."}
        </p>
        <p className="mt-1 text-[12px] text-zinc-500">
          WebReaper reports the block instead of returning challenge-page garbage. A
          captcha-solver tier is on the roadmap.
        </p>
      </div>
    );
  }

  return (
    <div className="mt-4 overflow-hidden rounded-lg border border-white/10 bg-black/30">
      <div className="flex items-center justify-between border-b border-white/10 px-3 py-2">
        <span className="font-mono text-[11px] text-accent">✓ extracted</span>
        <span className="font-mono text-[11px] text-zinc-500">Markdown · 1 page</span>
      </div>
      <pre className="overflow-x-auto p-3 text-[12px] leading-relaxed text-zinc-300">
        {result?.markdown}
      </pre>
    </div>
  );
}
