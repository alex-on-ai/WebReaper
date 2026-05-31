#!/bin/sh
# Tier B container entrypoint: apply the in-VM egress SSRF firewall (decision 4)
# before starting the app, then hand off to the .NET host.
#
# PLAYGROUND_EGRESS_FIREWALL:
#   warn    (default) apply the firewall; if it cannot apply (e.g. the Machine
#           lacks CAP_NET_ADMIN), log loudly and start anyway.
#   enforce apply the firewall; refuse to start if it cannot.
#   off     skip the firewall entirely (initial deploy bring-up only).
# See cloud/README.md for the validate-then-enforce rollout.
set -e

MODE="${PLAYGROUND_EGRESS_FIREWALL:-warn}"
if [ "$MODE" != "off" ]; then
    if nft -f /app/egress-firewall.nft 2>/tmp/nft.err; then
        echo "[entrypoint] egress firewall applied (PLAYGROUND_EGRESS_FIREWALL=$MODE)"
    else
        echo "[entrypoint] WARNING: egress firewall did NOT apply: $(cat /tmp/nft.err 2>/dev/null)"
        echo "[entrypoint] this Machine likely lacks CAP_NET_ADMIN; see cloud/README.md"
        if [ "$MODE" = "enforce" ]; then
            echo "[entrypoint] PLAYGROUND_EGRESS_FIREWALL=enforce: refusing to start without the firewall."
            exit 1
        fi
    fi
fi

# Headed mode (CloakBrowser's recipe for the hardest bot-checks): run the browsers
# under a virtual X display so they launch headed (no --headless) on this screenless
# VM. Only when PLAYGROUND_HEADED is set; the app reads the same flag to drop
# --headless from the browser launches.
if [ "${PLAYGROUND_HEADED:-}" = "1" ] || [ "${PLAYGROUND_HEADED:-}" = "true" ]; then
    Xvfb :99 -screen 0 1920x1080x24 -nolisten tcp >/tmp/xvfb.log 2>&1 &
    export DISPLAY=:99
    echo "[entrypoint] started Xvfb on :99 (headed mode)"
fi

exec dotnet WebReaper.PlaygroundApi.dll
