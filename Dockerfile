FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BeeMemoryBank.slnx .
COPY Directory.Build.props .
# Every NuGet version lives here (Central Package Management) and project files carry none.
# Without this COPY the restore inside the container fails with NU1010 for every package.
COPY Directory.Packages.props .
# VERSION is read by Directory.Build.props at build time to stamp the assembly version.
# Without this COPY the version property is silently skipped inside the container build.
COPY VERSION .
COPY libs/ libs/
COPY server/ server/
COPY tests/ tests/
COPY tools/ tools/

RUN dotnet publish server/BeeMemoryBank.Api/BeeMemoryBank.Api.csproj \
    -c Release -o /app/api

RUN dotnet publish server/BeeMemoryBank.Web/BeeMemoryBank.Web.csproj \
    -c Release -o /app/web

# The CLI is the break-glass path: `bmb init reset` wipes a node back to first-run Setup when
# nobody can sign in to the web UI any more (every superadmin account lost, or the Web layer
# broken). That is precisely the situation where you cannot install anything either, so it has to
# already be inside the image — `docker exec … dotnet /app/cli/BeeMemoryBank.Cli.dll init reset`.
RUN dotnet publish server/BeeMemoryBank.Cli/BeeMemoryBank.Cli.csproj \
    -c Release -o /app/cli

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/api ./api/
COPY --from=build /app/web ./web/
COPY --from=build /app/cli ./cli/
COPY docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh

EXPOSE 5300 5301

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:5300/health || exit 1

ENTRYPOINT ["./docker-entrypoint.sh"]
