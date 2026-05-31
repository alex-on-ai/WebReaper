/**
 * Server-side gate for the Tier A playground (Phase 1, step 4). The route
 * handler at app/api/playground/scrape composes these: verify the Cloudflare
 * Turnstile token, enforce per-IP + global rate limits, then proxy the backend
 * SSE through (so the Fly URL stays private and gating is centralized).
 *
 * Everything here is a fail-soft seam, matching lib/billing.ts: with no env
 * configured the gate is OPEN (so local dev and unconfigured previews still
 * work), and a loud server warning makes an unconfigured PRODUCTION deploy
 * obvious. Set TURNSTILE_SECRET_KEY + the Upstash vars to arm it.
 */

// Headers for a Server-Sent Events response. no-transform + x-accel-buffering
// stop intermediary proxies from buffering the stream.
export const SSE_HEADERS: Record<string, string> = {
  "content-type": "text/event-stream; charset=utf-8",
  "cache-control": "no-cache, no-transform",
  "x-accel-buffering": "no",
};

/** Best-effort client IP. request.ip was removed in Next 16; read the headers. */
export function clientIp(req: Request): string {
  const forwarded = req.headers.get("x-forwarded-for");
  if (forwarded) return forwarded.split(",")[0]!.trim();
  return req.headers.get("x-real-ip")?.trim() || "0.0.0.0";
}

// The error event the gate emits on rejection. Mirrors the playground client's
// ClimbEvent "error" arm (added client-side in the #210 live-source work); kept
// as a local shape so this route compiles independently of that branch.
type GateErrorEvent = { kind: "error"; message: string };

/**
 * A gate rejection as a complete SSE response. Returns 200 with a single
 * {kind:"error"} event (not a 4xx) so the message reaches the client reducer;
 * EventSource surfaces a 4xx only as a generic onerror. The client closes on a
 * terminal event, so the stream end does not trigger an auto-reconnect loop.
 */
export function sseError(message: string): Response {
  const event: GateErrorEvent = { kind: "error", message };
  return new Response(`data: ${JSON.stringify(event)}\n\n`, {
    status: 200,
    headers: SSE_HEADERS,
  });
}

type GateVerdict = { ok: true } | { ok: false; reason: string };

const TURNSTILE_VERIFY_URL =
  "https://challenges.cloudflare.com/turnstile/v0/siteverify";

/**
 * Verify a Turnstile token server-side. Unconfigured (no secret) => allow, with
 * a warning. Configured but the token is missing or rejected => block. A verify
 * call that throws => block (a broken verifier must not open the gate).
 */
export async function verifyTurnstile(
  token: string | null,
  ip: string,
): Promise<GateVerdict> {
  const secret = process.env.TURNSTILE_SECRET_KEY;
  if (!secret) {
    console.warn(
      "[playground] TURNSTILE_SECRET_KEY unset; skipping verification (dev mode).",
    );
    return { ok: true };
  }
  if (!token) return { ok: false, reason: "Please complete the challenge." };

  try {
    const body = new URLSearchParams({ secret, response: token, remoteip: ip });
    const res = await fetch(TURNSTILE_VERIFY_URL, {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body,
    });
    const data = (await res.json()) as { success?: boolean };
    return data.success
      ? { ok: true }
      : { ok: false, reason: "Verification failed. Refresh and try again." };
  } catch (err) {
    console.error("[playground] Turnstile verify error:", err);
    return { ok: false, reason: "Verification is temporarily unavailable." };
  }
}

function intEnv(name: string, fallback: number): number {
  const raw = process.env[name];
  const n = raw ? Number.parseInt(raw, 10) : Number.NaN;
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

type Window = { key: string; max: number; ttlSec: number; global: boolean };

// Fixed-window counters. Defaults: 5/min and 25/day per IP, 5000/day globally.
function windowsFor(ip: string): Window[] {
  const now = Date.now();
  const day = Math.floor(now / 86_400_000);
  const minute = Math.floor(now / 60_000);
  return [
    {
      key: `pg:global:d:${day}`,
      max: intEnv("PLAYGROUND_GLOBAL_PER_DAY", 5000),
      ttlSec: 86_400,
      global: true,
    },
    {
      key: `pg:ip:${ip}:m:${minute}`,
      max: intEnv("PLAYGROUND_IP_PER_MIN", 5),
      ttlSec: 60,
      global: false,
    },
    {
      key: `pg:ip:${ip}:d:${day}`,
      max: intEnv("PLAYGROUND_IP_PER_DAY", 25),
      ttlSec: 86_400,
      global: false,
    },
  ];
}

/**
 * Per-IP + global rate limit, backed by Upstash Redis REST (atomic INCR+EXPIRE
 * via /multi-exec). Unconfigured => allow, with a warning. A store outage =>
 * allow (fail open): cost/abuse protection must not take the demo down on a
 * Redis blip, and SSRF + the backend secret are separate, harder guards.
 */
export async function checkRateLimits(ip: string): Promise<GateVerdict> {
  const url = process.env.UPSTASH_REDIS_REST_URL;
  const token = process.env.UPSTASH_REDIS_REST_TOKEN;
  if (!url || !token) {
    console.warn(
      "[playground] Upstash not configured; rate limiting disabled (dev mode).",
    );
    return { ok: true };
  }

  for (const w of windowsFor(ip)) {
    let count: number;
    try {
      count = await incrWithExpire(url, token, w.key, w.ttlSec);
    } catch (err) {
      console.error("[playground] rate-limit store error:", err);
      return { ok: true };
    }
    if (count > w.max) {
      return {
        ok: false,
        reason: w.global
          ? "The live demo is at capacity right now. Try again later, or sign up for Cloud."
          : "You have hit the demo limit. Try again later, or run WebReaper locally.",
      };
    }
  }
  return { ok: true };
}

type RedisResult = { result?: unknown; error?: string };

// Run a pipeline of Redis commands atomically via the Upstash REST /multi-exec
// endpoint. Throws on a transport / HTTP error; callers treat that as fail-open.
async function redisMultiExec(
  baseUrl: string,
  token: string,
  commands: string[][],
): Promise<RedisResult[]> {
  const res = await fetch(`${baseUrl}/multi-exec`, {
    method: "POST",
    headers: {
      authorization: `Bearer ${token}`,
      "content-type": "application/json",
    },
    body: JSON.stringify(commands),
  });
  if (!res.ok) throw new Error(`Upstash HTTP ${res.status}`);
  return (await res.json()) as RedisResult[];
}

function firstCount(results: RedisResult[], op: string): number {
  const r = results[0];
  if (!r || typeof r.result !== "number") {
    throw new Error(typeof r?.error === "string" ? r.error : `unexpected ${op} result`);
  }
  return r.result;
}

async function incrWithExpire(
  baseUrl: string,
  token: string,
  key: string,
  ttlSec: number,
): Promise<number> {
  const results = await redisMultiExec(baseUrl, token, [
    ["INCR", key],
    ["EXPIRE", key, String(ttlSec)],
  ]);
  return firstCount(results, "INCR");
}

async function incrByWithExpire(
  baseUrl: string,
  token: string,
  key: string,
  amount: number,
  ttlSec: number,
): Promise<number> {
  const results = await redisMultiExec(baseUrl, token, [
    ["INCRBY", key, String(amount)],
    ["EXPIRE", key, String(ttlSec)],
  ]);
  return firstCount(results, "INCRBY");
}

/**
 * Light pre-check shared by both tiers: reject obvious junk and non-http(s)
 * schemes before opening an upstream connection. The backend's SSRF-guarded
 * handler (and, for Tier B, the in-VM egress firewall) are the authoritative
 * address policy.
 */
export function safeHttpUrl(raw: string | null): URL | null {
  if (!raw) return null;
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    return null;
  }
  return url.protocol === "http:" || url.protocol === "https:" ? url : null;
}

// Pragmatic email shape check for the gate: one @, a dot in the domain, no
// whitespace. Not RFC-perfect; it only has to reject junk.
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * Tier B email-capture gate (the 2026-05-30 decision: the live climb is gated by
 * an email that feeds the Stripe-ready waitlist). Validates the address and
 * records it best-effort to the lead store, then allows. A missing or malformed
 * email is the only block; a store outage never blocks, since the lead is already
 * in hand.
 */
export async function captureEmail(email: string | null, ip: string): Promise<GateVerdict> {
  const value = email?.trim().toLowerCase() ?? "";
  if (!value || value.length > 254 || !EMAIL_RE.test(value)) {
    return { ok: false, reason: "Enter your email to run the full browser climb." };
  }
  await recordLead(value, ip);
  return { ok: true };
}

// Best-effort lead capture into a deduplicated Upstash set. Unconfigured => log
// the lead so it is recoverable (a missing store must not silently drop the
// funnel, matching lib/billing.ts's waitlist behaviour). Never throws.
async function recordLead(email: string, ip: string): Promise<void> {
  const url = process.env.UPSTASH_REDIS_REST_URL;
  const token = process.env.UPSTASH_REDIS_REST_TOKEN;
  if (!url || !token) {
    console.warn(
      `[playground] lead captured but Upstash is unset, so it was NOT stored. ` +
        `Recover from this log: email=${email} ip=${ip}.`,
    );
    return;
  }
  try {
    await redisMultiExec(url, token, [["SADD", "pg:playground:leads", email]]);
  } catch (err) {
    console.error("[playground] lead store error (the run is still allowed):", err);
  }
}

// The per-job browser-time ceiling the backend enforces (its ~45s kill). The
// budget meters every Tier B run at this worst case.
const TIERB_JOB_SECONDS = 45;

/**
 * Tier B daily browser-time budget kill switch (the cost ceiling, decision 6).
 * Meters each run at its 45s worst case into a daily counter; once it passes
 * PLAYGROUND_TIERB_DAILY_BROWSER_SECONDS (default 3 browser-hours) the playground
 * shows "at capacity, sign up" instead of spending another browser Machine.
 * Unconfigured / store outage => allow: a cost ceiling must not take the demo
 * down on a Redis blip, and the backend's own per-job kill plus Fly scale-to-zero
 * are the harder cost guards.
 */
export async function checkTierBBudget(): Promise<GateVerdict> {
  const url = process.env.UPSTASH_REDIS_REST_URL;
  const token = process.env.UPSTASH_REDIS_REST_TOKEN;
  if (!url || !token) {
    console.warn("[playground] Upstash not configured; Tier B budget disabled (dev mode).");
    return { ok: true };
  }

  const cap = intEnv("PLAYGROUND_TIERB_DAILY_BROWSER_SECONDS", 10_800);
  const day = Math.floor(Date.now() / 86_400_000);
  try {
    const spent = await incrByWithExpire(url, token, `pg:tierb:secs:d:${day}`, TIERB_JOB_SECONDS, 86_400);
    if (spent > cap) {
      return {
        ok: false,
        reason: "The live browser demo is at capacity for today. Sign up for Cloud to keep going.",
      };
    }
  } catch (err) {
    console.error("[playground] Tier B budget store error:", err);
    return { ok: true };
  }
  return { ok: true };
}
