# syntax=docker/dockerfile:1.7

# ─────────────────────────────────────────────────────────────────────────────────────
# Restore stage
#
# Project files are copied before source so `dotnet restore` sits in its own cached layer.
# Editing a .cs file then leaves the restore layer intact; copying everything up front
# would re-download every package on every code change.
# ─────────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS restore
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ShoppingList.Api/ShoppingList.Api.csproj src/ShoppingList.Api/
COPY tests/ShoppingList.Tests.Unit/ShoppingList.Tests.Unit.csproj tests/ShoppingList.Tests.Unit/
COPY tests/ShoppingList.Tests.Integration/ShoppingList.Tests.Integration.csproj tests/ShoppingList.Tests.Integration/
COPY ShoppingList.sln ./

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore ShoppingList.sln

# ─────────────────────────────────────────────────────────────────────────────────────
# Build & publish
# ─────────────────────────────────────────────────────────────────────────────────────
FROM restore AS build
WORKDIR /src
COPY . .

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/ShoppingList.Api/ShoppingList.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        /p:UseAppHost=false

# ─────────────────────────────────────────────────────────────────────────────────────
# Test stage
#
# Not part of the runtime chain — invoked explicitly with `--target test`. Keeping tests in
# the Dockerfile lets CI run them in exactly the environment the image is built in, without
# the runtime image inheriting the SDK or the test dependencies.
# ─────────────────────────────────────────────────────────────────────────────────────
FROM build AS test
WORKDIR /src
CMD ["dotnet", "test", "ShoppingList.sln", "--no-restore", "--verbosity", "normal"]

# ─────────────────────────────────────────────────────────────────────────────────────
# Runtime
#
# aspnet, not sdk: the final image carries no compiler, no NuGet cache and no source. That
# is roughly a 700 MB difference and, more importantly, a much smaller attack surface.
# ─────────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# wget backs the container healthcheck; the base image ships no HTTP client.
#
# libgssapi-krb5-2 is here for signal, not function. Npgsql probes for GSSAPI on connect and,
# when the library is absent, prints "Cannot load library libgssapi_krb5.so.2" before carrying
# on perfectly well. A startup log that opens with a load error trains people to ignore
# startup logs — 300 KB is a fair price for output that means what it says.
RUN apt-get update \
    && apt-get install --no-install-recommends -y wget libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build --chown=app:app /app/publish .

# Runs as non-root. Since .NET 8 the official runtime images ship a non-root `app` user
# (UID 1654) and expose it as $APP_UID, so creating one would fail on a group that already
# exists — the correct code here is to use what the base image provides rather than to
# duplicate it. Switching after COPY keeps the copy running as root, so file ownership is
# set explicitly by --chown rather than depending on the current user.
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1 \
    DOTNET_TieredPMStubs=0

EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=5s --start-period=25s --retries=3 \
    CMD wget --spider -q http://localhost:8080/health/live || exit 1

# Started with `--migrate-only` by the migrator service: same image, same assemblies, one
# argument. A separate migration image would drift from the application it migrates.
ENTRYPOINT ["dotnet", "ShoppingList.Api.dll"]
