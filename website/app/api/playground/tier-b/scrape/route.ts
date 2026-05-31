import {
  SSE_HEADERS,
  captureEmail,
  checkRateLimits,
  checkTierBBudget,
  clientIp,
  safeHttpUrl,
  sseError,
  verifyTurnstile,
} from "@/lib/playground/gate";

// SSE must never be cached, and the handler runs as long as the climb streams.
export const dynamic = "force-dynamic";
export const maxDuration = 60;

// The Tier B Fly app is SEPARATE from the Tier A backend (its own dedicated org;
// see cloud/WebReaper.PlaygroundApi/fly.tierb.toml). NEXT_PUBLIC is deliberately
// absent: the browser never learns the backend URL.
const BACKEND_URL = process.env.PLAYGROUND_TIERB_BACKEND_URL ?? "http://localhost:5179";

/**
 * Tier B playground gate. GET (so the client EventSource can consume it):
 *   /api/playground/tier-b/scrape?url=<encoded>&cf=<turnstile>&email=<address>
 * The gated, metered tier: verify Turnstile, apply the per-IP / global rate
 * limits, capture the email (lead funnel), then meter the daily browser-time
 * budget before spending a browser Machine. On success it streams the Tier B
 * backend /tier-b/scrape/stream SSE through, injecting the shared secret.
 */
export async function GET(req: Request): Promise<Response> {
  const params = new URL(req.url).searchParams;
  const target = safeHttpUrl(params.get("url"));
  if (!target) return sseError("Enter a valid http(s) URL to scrape.");

  const ip = clientIp(req);

  const turnstile = await verifyTurnstile(params.get("cf"), ip);
  if (!turnstile.ok) return sseError(turnstile.reason);

  const limit = await checkRateLimits(ip);
  if (!limit.ok) return sseError(limit.reason);

  // Capture the lead before metering, so an at-capacity visitor is still on the
  // list when shown the "sign up" message.
  const email = await captureEmail(params.get("email"), ip);
  if (!email.ok) return sseError(email.reason);

  const budget = await checkTierBBudget();
  if (!budget.ok) return sseError(budget.reason);

  const upstreamUrl = `${BACKEND_URL}/tier-b/scrape/stream?url=${encodeURIComponent(target.href)}`;
  let upstream: Response;
  try {
    upstream = await fetch(upstreamUrl, {
      headers: backendHeaders(),
      // Propagate client disconnect so the backend kills the browser when the
      // EventSource closes.
      signal: req.signal,
    });
  } catch (err) {
    console.error("[playground] tier-b backend unreachable:", err);
    return sseError("The scrape service is unavailable right now. Please try again.");
  }

  if (!upstream.ok || !upstream.body) {
    return sseError("The scrape service returned an error. Please try again.");
  }

  return new Response(upstream.body, { status: 200, headers: SSE_HEADERS });
}

function backendHeaders(): HeadersInit {
  const secret = process.env.PLAYGROUND_TIERB_BACKEND_SECRET;
  return secret ? { "x-playground-secret": secret } : {};
}
