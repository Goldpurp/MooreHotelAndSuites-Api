FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

COPY *.sln global.json Directory.Build.props ./
COPY MooreHotels.WebAPI/*.csproj MooreHotels.WebAPI/packages.lock.json MooreHotels.WebAPI/
COPY MooreHotels.Infrastructure/*.csproj MooreHotels.Infrastructure/packages.lock.json MooreHotels.Infrastructure/
COPY MooreHotels.Application/*.csproj MooreHotels.Application/packages.lock.json MooreHotels.Application/
COPY MooreHotels.Domain/*.csproj MooreHotels.Domain/packages.lock.json MooreHotels.Domain/
RUN dotnet restore MooreHotels.WebAPI/MooreHotels.WebAPI.csproj --locked-mode

COPY . .
RUN dotnet tool restore
RUN dotnet publish MooreHotels.WebAPI/MooreHotels.WebAPI.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false \
    /p:DebugType=None \
    /p:DebugSymbols=false
RUN dotnet tool run dotnet-ef migrations bundle \
    --project MooreHotels.Infrastructure/MooreHotels.Infrastructure.csproj \
    --startup-project MooreHotels.Infrastructure/MooreHotels.Infrastructure.csproj \
    --configuration Release \
    --no-build \
    --output /app/migrate

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:1fe86375600b62e6566b465da9553eef0621f13c67f40fe764cd8dbb1dee1497 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get upgrade --yes \
    && apt-get install --yes --no-install-recommends postgresql-client \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build --chown=app:app /app/publish .
COPY certificates/supabase-ca.crt ./certificates/supabase-ca.crt
COPY --from=build --chown=app:app /app/migrate ./migrate
COPY --from=build --chown=app:app \
    /src/scripts/create-supabase-runtime-role.sh \
    /src/scripts/bind-production-database.sh \
    /src/scripts/database-connection.sh \
    /src/scripts/harden-supabase-data-api.sh \
    /src/scripts/predeploy-production.sh \
    /src/scripts/provision-runtime-database-role.sh \
    /src/scripts/validate-production-database.sh \
    /src/scripts/validate-runtime-database-role.sh \
    ./scripts/

USER app
EXPOSE 8080
# Strip deployment-only credentials before exec creates the API process.
# Clearing them inside .NET is too late for Linux's /proc/1/environ snapshot.
# External migration runs override this entrypoint with scripts/predeploy-production.sh.
ENTRYPOINT ["env", "-u", "MIGRATION_CONNECTION_STRING", "-u", "DATABASE_RUNTIME_PASSWORD", "dotnet", "MooreHotels.WebAPI.dll"]
