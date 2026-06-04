# ADR-0086: the Streamable HTTP MCP server image, with a headless vanilla
# Chromium baked in so browser=true works out of the box. Leaner than the
# playground Tier-B image: headless (no Xvfb), no stealth fork, no egress
# firewall, no proxy. For an external browser pool set WEBREAPER_CDP_URL and
# the baked Chromium goes unused.
#
# Build context = the REPO ROOT (the host references the library projects):
#
#   docker build -f WebReaper.Mcp.AspNetCore/Dockerfile -t webreaper-mcp-http .

# --- build the app (SDK tag matches global.json) ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore as its own layer: shared build props + the csproj graph only
# (AspNetCore -> Mcp -> WebReaper / Cdp / AI / AI.Http), so a source-only change
# does not re-run restore.
COPY global.json Directory.Build.props ./
COPY WebReaper/WebReaper.csproj WebReaper/
COPY WebReaper.Cdp/WebReaper.Cdp.csproj WebReaper.Cdp/
COPY WebReaper.AI/WebReaper.AI.csproj WebReaper.AI/
COPY WebReaper.AI.Http/WebReaper.AI.Http.csproj WebReaper.AI.Http/
COPY WebReaper.Mcp/WebReaper.Mcp.csproj WebReaper.Mcp/
COPY WebReaper.Mcp.AspNetCore/WebReaper.Mcp.AspNetCore.csproj WebReaper.Mcp.AspNetCore/
RUN dotnet restore WebReaper.Mcp.AspNetCore/WebReaper.Mcp.AspNetCore.csproj
# Sources, then publish (framework-dependent; the runtime base carries ASP.NET).
COPY WebReaper/ WebReaper/
COPY WebReaper.Cdp/ WebReaper.Cdp/
COPY WebReaper.AI/ WebReaper.AI/
COPY WebReaper.AI.Http/ WebReaper.AI.Http/
COPY WebReaper.Mcp/ WebReaper.Mcp/
COPY WebReaper.Mcp.AspNetCore/ WebReaper.Mcp.AspNetCore/
RUN dotnet publish WebReaper.Mcp.AspNetCore/WebReaper.Mcp.AspNetCore.csproj \
    -c Release -o /app --no-restore

# --- runtime: Debian + ASP.NET 10 runtime + headless vanilla Chromium ---
# Debian (not the .NET aspnet image): .NET 10 ships Ubuntu-only base images, and
# Ubuntu's `chromium` apt package is a container-hostile snap stub. Debian's
# `chromium` is a real .deb that runs headless in a container. WebReaper.Cdp's
# PATH search finds /usr/bin/chromium, so CdpLaunchOptions() launches it with no
# extra config. The ASP.NET Core 10 runtime is installed via the official script.
FROM debian:bookworm-slim AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        chromium fonts-liberation fonts-noto-core fonts-noto-color-emoji fonts-freefont-ttf \
        libicu72 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm /tmp/dotnet-install.sh
WORKDIR /app
COPY --from=build /app ./

# Bind all interfaces inside the container. The McpHttpOptions bind guard then
# REQUIRES WEBREAPER_MCP_TOKEN at startup (a non-loopback bind without a token is
# refused), so the image is secure by default: `docker run` without a token
# fails actionably. The vanilla Chromium launches with the host's default flags.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s \
    CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "WebReaper.Mcp.AspNetCore.dll"]
