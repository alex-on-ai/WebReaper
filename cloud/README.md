# WebReaper Cloud playground backend

The Tier A live-scrape service: paste a URL, get clean Markdown streamed back
over Server-Sent Events. It is a thin ASP.NET minimal API over the WebReaper
library, deployed separately from the NuGet packages (like `website/`), and is
the seed of WebReaper Cloud's scrape endpoint.

Design: [docs/CLOUD-SCRAPE-PLAYGROUND-PLAN.md](../docs/CLOUD-SCRAPE-PLAYGROUND-PLAN.md)
and the Phase 1 slice [docs/CLOUD-PLAYGROUND-PHASE-1.md](../docs/CLOUD-PLAYGROUND-PHASE-1.md).

## Architecture

```
[playground component, /playground]  --EventSource-->  [Vercel edge route /api/playground/scrape]
                                                          verify Turnstile, per-IP + global rate limit
                                                          proxy SSE through (backend URL stays private,
                                                          inject the shared secret)
                                                              |
                                                              v
                                       [this app, on Fly]  GET /scrape/stream?url=...  -> SSE of ClimbEvent
                                                          require X-Playground-Secret (refuse direct traffic)
                                                          SSRF-guarded handler; concurrency cap; per-job timeout
```

## Run locally

The `#210` client defaults to `http://localhost:5179`, so run on that port:

```bash
ASPNETCORE_URLS=http://localhost:5179 dotnet run --project cloud/WebReaper.PlaygroundApi
# stream a scrape (no gate locally, since no secret is set):
curl -N "http://localhost:5179/scrape/stream?url=https://example.com"
```

With no env set the backend is open (dev mode). It is intentionally NOT in
`WebReaper.sln`, so it stays out of the library CI.

## Deploy (Fly)

Needs a Fly.io account + `flyctl`. Deploy from the **repo root** so the build
context includes the WebReaper library (Fly's context is always the project
root; the Dockerfile copies `WebReaper/`):

```bash
fly launch --no-deploy --config cloud/WebReaper.PlaygroundApi/fly.toml   # first time: set app name + region
fly secrets set PLAYGROUND_BACKEND_SECRET=$(openssl rand -hex 32) \
  --config cloud/WebReaper.PlaygroundApi/fly.toml
fly deploy --config cloud/WebReaper.PlaygroundApi/fly.toml
```

Then set the matching values in the Vercel project (see `website/.env.example`):
`PLAYGROUND_BACKEND_URL` (the Fly app URL), `PLAYGROUND_BACKEND_SECRET` (the same
secret), `TURNSTILE_SECRET_KEY` + `NEXT_PUBLIC_TURNSTILE_SITE_KEY`, and the
Upstash REST vars.

## Environment

| Side | Variable | Purpose |
|---|---|---|
| Fly (backend) | `PLAYGROUND_BACKEND_SECRET` | Shared secret; the backend rejects `/scrape/stream` without a matching `X-Playground-Secret`. Unset => open (dev). |
| Fly (backend) | `PLAYGROUND_ALLOWED_ORIGINS` | Comma-separated CORS allowlist. Unset => any-origin (dev). |
| Vercel (edge) | `PLAYGROUND_BACKEND_URL` | Private Fly URL the edge route proxies to. |
| Vercel (edge) | `PLAYGROUND_BACKEND_SECRET` | Same secret as Fly; injected on every proxied request. |
| Vercel (edge) | `TURNSTILE_SECRET_KEY` | Cloudflare Turnstile server secret. Unset => verification skipped (dev). |
| Vercel (edge) | `UPSTASH_REDIS_REST_URL` / `_TOKEN` | Rate-limit store. Unset => limiting disabled (dev). |

Every variable is a fail-soft seam: unset means "off, dev mode" with a loud
server warning, never a crash. An unconfigured production deploy is therefore
open, so set the secrets before announcing the URL.

## Activation note (client side)

The edge gate is live and self-contained, but it only takes effect once the
playground **client** routes through it. As of `#210` the component points its
`EventSource` straight at the backend (`NEXT_PUBLIC_PLAYGROUND_API`), which
bypasses the gate. To switch the public site onto the gated path, two one-line
client changes belong with that work (kept out of this change to avoid touching
`#210`'s files):

1. Point the `EventSource` at the same-origin route `/api/playground/scrape`
   instead of the public backend origin.
2. Append the Turnstile token as `&cf=<token>` (EventSource is GET-only and
   cannot send headers).

Until then the gate is dormant; the backend secret + CORS allowlist still let
you lock the Fly app down independently.

## Tier B (live browser + stealth climb)

Tier B is the second code path in this same app: `GET /tier-b/scrape/stream`
builds a real WebReaper engine so the ADR-0083 escalating loader climbs HTTP to a
vanilla browser to the CloakBrowser stealth rung, streaming the live climb over
SSE. It needs the browsers baked in, so it ships as a **separate image**
(`Dockerfile.tierb`) deployed as a **separate Fly app in a dedicated org**
(decision 4: a browser SSRF / escape must not reach other apps over Fly 6PN). The
lean Tier A app above is untouched.

### Deploy (owner actions)

```bash
# 1. A dedicated org, so the blast radius is isolated from `personal`.
fly orgs create webreaper-tierb           # once; pick your own name

# 2. Launch the app into that org (sets the app name + region; no deploy yet).
fly launch --no-deploy --org webreaper-tierb \
  --config cloud/WebReaper.PlaygroundApi/fly.tierb.toml

# 3. The shared gate secret (same value as the Tier A app / the Vercel edge).
fly secrets set PLAYGROUND_BACKEND_SECRET=$(openssl rand -hex 32) \
  --config cloud/WebReaper.PlaygroundApi/fly.tierb.toml

# 4. Bring it up with the firewall OFF first, to confirm the app + climb work
#    before adding the network layer (see "Egress firewall" below).
fly secrets set PLAYGROUND_EGRESS_FIREWALL=off \
  --config cloud/WebReaper.PlaygroundApi/fly.tierb.toml
fly deploy --config cloud/WebReaper.PlaygroundApi/fly.tierb.toml   # from the REPO ROOT
```

The image bakes Chromium (`/usr/bin/chromium`) and the CloakBrowser fork
(`/opt/cloakbrowser/chrome`) and names them via `PLAYGROUND_CHROMIUM_PATH` /
`PLAYGROUND_CLOAKBROWSER_PATH`, so no per-deploy browser config is needed. The
stealth rung is present only because the binary is baked; keep it reachable only
through the `X-Playground-Secret` gate (never the public edge route) until the
CloakHQ OEM license lands.

### Egress firewall (decision 4): validate, then enforce

The browser issues its own OS-level requests, bypassing the app-layer SSRF guard,
so `egress-firewall.nft` lifts the blocklist to the network layer (nftables,
applied by `entrypoint.sh` per `PLAYGROUND_EGRESS_FIREWALL`). It is **not a
verified control until validated on a live Machine**; three things can only be
checked there:

1. **CAP_NET_ADMIN.** `nft -f` needs it. With the firewall in `warn` mode, the
   Machine logs whether it applied; if it did not, the Machine likely lacks the
   capability (raise it with Fly support / a Machine config).
2. **DNS.** Allowed by port (53), not by host, so resolution should work
   regardless; confirm a climb still resolves names.
3. **NAT64.** If Fly reaches the IPv4 internet via `64:ff9b::/96`, outbound v4
   rides inside IPv6 and the `ip daddr` rules never match it. Confirm an internal
   v4 target is actually blocked (below); if not, re-express the internal ranges
   as their `64:ff9b::` embeddings.

Rollout:

```bash
# After the firewall-off deploy works, turn the firewall on in warn mode:
fly secrets set PLAYGROUND_EGRESS_FIREWALL=warn --config .../fly.tierb.toml
fly deploy --config .../fly.tierb.toml
fly logs --config .../fly.tierb.toml         # expect "egress firewall applied"

# Validate from a gated request (replace SECRET + the app URL):
#  - public scrape works (DNS + egress OK):
curl -N -H "X-Playground-Secret: $SECRET" \
  "https://<app>.fly.dev/tier-b/scrape/stream?url=https://example.com"
#  - an internal target is BLOCKED (expect an error event, not a result):
curl -N -H "X-Playground-Secret: $SECRET" \
  "https://<app>.fly.dev/tier-b/scrape/stream?url=http://169.254.169.254/latest/meta-data/"

# Once both hold, enforce (the Machine refuses to start without the firewall):
fly secrets set PLAYGROUND_EGRESS_FIREWALL=enforce --config .../fly.tierb.toml
fly deploy --config .../fly.tierb.toml
```

### Stealth verification (real IPs)

The full HTTP to vanilla to stealth climb and "beats hardened Cloudflare" cannot
be checked locally (no macOS CloakBrowser build, and emulation is too slow for the
45s job budget). On the deployed Machine, curl the gated stealth climb against a
known Cloudflare-challenge target and watch the events escalate to
`attempt(stealth)` and then `success` or an honest `exhausted`:

```bash
curl -N -H "X-Playground-Secret: $SECRET" \
  "https://<app>.fly.dev/tier-b/scrape/stream?url=https://<a-cloudflare-protected-site>"
```

CloakBrowser's own docs note headless can still be detected by the hardest sites;
if a target needs it, headed mode + a residential proxy is the next lever (a
future option, not wired here).

### Env (Tier B additions)

| Side | Variable | Purpose |
|---|---|---|
| Fly (Tier B) | `PLAYGROUND_EGRESS_FIREWALL` | `warn` (default) apply + continue on failure; `enforce` refuse to start without it; `off` skip (bring-up only). |
| Fly (Tier B, baked) | `PLAYGROUND_CHROMIUM_PATH` / `PLAYGROUND_CLOAKBROWSER_PATH` | The two baked browser binaries. Set in the image; override only to relocate them. |
