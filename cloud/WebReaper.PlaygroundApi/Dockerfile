# Tier A playground backend (Phase 1). The API references the WebReaper library
# project (..\..\WebReaper), so the Docker build CONTEXT must be the REPO ROOT,
# not this directory. The build context is the working directory you run
# `fly deploy` from, so deploy FROM the repo root:
#
#   fly deploy --config cloud/WebReaper.PlaygroundApi/fly.toml
#
# To build by hand, also pass the root as context:
#   docker build -f cloud/WebReaper.PlaygroundApi/Dockerfile -t webreaper-playground .

# SDK tag matches global.json (10.0.100, rollForward latestMinor).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore as its own layer: copy the shared build props + the csprojs only, so
# a source-only change does not re-run restore. WebReaper.csproj has no project
# references, so the API + the library are the whole graph.
COPY global.json Directory.Build.props ./
COPY WebReaper/WebReaper.csproj WebReaper/
COPY cloud/WebReaper.PlaygroundApi/WebReaper.PlaygroundApi.csproj cloud/WebReaper.PlaygroundApi/
RUN dotnet restore cloud/WebReaper.PlaygroundApi/WebReaper.PlaygroundApi.csproj

# Now the sources, and publish (framework-dependent; the aspnet base carries the
# runtime). Not AOT: this is a web host referencing the library, not the CLI.
COPY WebReaper/ WebReaper/
COPY cloud/WebReaper.PlaygroundApi/ cloud/WebReaper.PlaygroundApi/
RUN dotnet publish cloud/WebReaper.PlaygroundApi/WebReaper.PlaygroundApi.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Listen on 8080 (fly.toml internal_port) and run as the non-root user the
# aspnet image ships (APP_UID, since .NET 8).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "WebReaper.PlaygroundApi.dll"]
