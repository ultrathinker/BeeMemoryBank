FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BeeMemoryBank.slnx .
COPY Directory.Build.props .
COPY libs/ libs/
COPY server/ server/
COPY tests/ tests/
COPY tools/ tools/

RUN dotnet publish server/BeeMemoryBank.Api/BeeMemoryBank.Api.csproj \
    -c Release -o /app/api

RUN dotnet publish server/BeeMemoryBank.Web/BeeMemoryBank.Web.csproj \
    -c Release -o /app/web

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/api ./api/
COPY --from=build /app/web ./web/
COPY docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh

EXPOSE 5300 5301

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:5300/health || exit 1

ENTRYPOINT ["./docker-entrypoint.sh"]
