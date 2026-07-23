FROM mcr.microsoft.com/dotnet/sdk:8.0.423 AS build
WORKDIR /src

COPY *.sln global.json ./
COPY MooreHotels.WebAPI/*.csproj MooreHotels.WebAPI/
COPY MooreHotels.Infrastructure/*.csproj MooreHotels.Infrastructure/
COPY MooreHotels.Application/*.csproj MooreHotels.Application/
COPY MooreHotels.Domain/*.csproj MooreHotels.Domain/
RUN dotnet restore MooreHotels.WebAPI/MooreHotels.WebAPI.csproj

COPY . .
RUN dotnet tool restore
RUN dotnet publish MooreHotels.WebAPI/MooreHotels.WebAPI.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false
RUN dotnet tool run dotnet-ef migrations bundle \
    --project MooreHotels.Infrastructure/MooreHotels.Infrastructure.csproj \
    --startup-project MooreHotels.WebAPI/MooreHotels.WebAPI.csproj \
    --configuration Release \
    --no-build \
    --output /app/migrate

FROM mcr.microsoft.com/dotnet/aspnet:8.0.29 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build --chown=app:app /app/publish .
COPY --from=build --chown=app:app /app/migrate ./migrate

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "MooreHotels.WebAPI.dll"]
