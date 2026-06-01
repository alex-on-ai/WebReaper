# Tier B PUBLIC playground image: the lean, no-stealth climb. Where
# Dockerfile.tierb bakes the CloakBrowser stealth fork for the secret-gated /
# future real-GPU path, this image ships ONLY the HTTP rung + a headed vanilla
# Chromium rung (Apache/BSD-only, ~216 MB smaller, no CloakBrowser binary and so
# no OEM/SaaS licensing surface on a public-serving app). The vanilla rung runs
# headed under Xvfb and routes through PLAYGROUND_RESIDENTIAL_PROXY, which is what
# clears Cloudflare / DataDome JS challenges at the vanilla rung; the ~37s stealth
# rung is intentionally absent so a hard-CF target cannot climb past one browser
# rung and exceed the playground edge's 60s maxDuration.
#
# Build context = the REPO ROOT (the app references the library projects):
#
#   docker build -f cloud/WebReaper.PlaygroundApi/Dockerfile.tierb.lean -t webreaper-playground-tierb .

# --- build the app (SDK tag matches global.json) ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore as its own layer: copy the shared build props + the csprojs only (the
# whole project graph: WebReaper -> Cdp -> Stealth.CloakBrowser -> the API), so a
# source-only change does not re-run restore. (Stealth.CloakBrowser is still a
# compile-time ref for the recipe code; only the binary is omitted at runtime.)
COPY global.json Directory.Build.props ./
COPY WebReaper/WebReaper.csproj WebReaper/
COPY WebReaper.Cdp/WebReaper.Cdp.csproj WebReaper.Cdp/
COPY WebReaper.Stealth.CloakBrowser/WebReaper.Stealth.CloakBrowser.csproj WebReaper.Stealth.CloakBrowser/
COPY cloud/WebReaper.PlaygroundApi/WebReaper.PlaygroundApi.csproj cloud/WebReaper.PlaygroundApi/
RUN dotnet restore cloud/WebReaper.PlaygroundApi/WebReaper.PlaygroundApi.csproj
# Sources, then publish (framework-dependent; the aspnet base carries the runtime).
COPY WebReaper/ WebReaper/
COPY WebReaper.Cdp/ WebReaper.Cdp/
COPY WebReaper.Stealth.CloakBrowser/ WebReaper.Stealth.CloakBrowser/
COPY cloud/WebReaper.PlaygroundApi/ cloud/WebReaper.PlaygroundApi/
RUN dotnet publish cloud/WebReaper.PlaygroundApi/WebReaper.PlaygroundApi.csproj \
    -c Release -o /app --no-restore

# --- runtime: Debian + ASP.NET 10 runtime + vanilla Chromium (no CloakBrowser) ---
# Debian (not the .NET aspnet image): .NET 10 ships Ubuntu-only base images, and
# Ubuntu's `chromium` apt package is a container-hostile snap stub. Debian's
# `chromium` is a real .deb that runs headless/headed in a container. The Noto +
# freefont + emoji fonts keep headed rendering sane across locales; xvfb provides
# the virtual display the headed vanilla rung needs. The ASP.NET Core 10 runtime
# is installed via the official script (distro-independent).
FROM debian:bookworm-slim AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        chromium fonts-liberation fonts-noto-core fonts-noto-color-emoji fonts-freefont-ttf \
        libicu72 ca-certificates curl nftables xvfb \
    && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm /tmp/dotnet-install.sh
WORKDIR /app
COPY --from=build /app ./
# The in-VM egress SSRF firewall (decision 4) + the entrypoint that applies it
# before the app starts (nftables is installed above).
COPY cloud/WebReaper.PlaygroundApi/egress-firewall.nft /app/egress-firewall.nft
COPY cloud/WebReaper.PlaygroundApi/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh
# The vanilla browser launches with --no-sandbox (the Firecracker VM is the trust
# boundary, decision 5), so the container runs as root. PLAYGROUND_CHROMIUM_PATH
# names the baked vanilla Chromium; PLAYGROUND_CLOAKBROWSER_PATH is deliberately
# UNSET so TierBScraper builds no stealth rung (HTTP + headed vanilla only).
# PLAYGROUND_HEADED=1 runs the vanilla rung headed under Xvfb (entrypoint starts
# it); pair with PLAYGROUND_RESIDENTIAL_PROXY (a Fly secret) for the residential
# exit IP that clears CF/DataDome at the vanilla rung.
ENV ASPNETCORE_URLS=http://+:8080 \
    PLAYGROUND_CHROMIUM_PATH=/usr/bin/chromium \
    PLAYGROUND_HEADED=1
EXPOSE 8080
# The entrypoint applies the egress firewall (per PLAYGROUND_EGRESS_FIREWALL),
# then execs the .NET host.
ENTRYPOINT ["/app/entrypoint.sh"]
