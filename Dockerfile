# This Dockerfile builds the API service only.
# The Web UI (BeeMemoryBank.Web) is a separate process that proxies to the API.
# For a full deployment with both services, see docs/deployment.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BeeMemoryBank.slnx .
COPY Directory.Build.props .
COPY libs/ libs/
COPY server/ server/
COPY tests/ tests/
COPY tools/ tools/

RUN dotnet restore server/BeeMemoryBank.Api/BeeMemoryBank.Api.csproj
RUN dotnet publish server/BeeMemoryBank.Api/BeeMemoryBank.Api.csproj \
    -c Release \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache curl

COPY --from=build /app/publish .
COPY docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["./docker-entrypoint.sh"]
