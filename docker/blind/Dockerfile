# Blind node image (BMB-54, plan §2/§9): the Api in blind role + the local console + the CLI,
# plus the restic binary backups run through. Build context is the REPO ROOT:
#   docker build -f docker/blind/Dockerfile -t bmb-blind:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BeeMemoryBank.slnx .
COPY Directory.Build.props .
# Every NuGet version lives here (Central Package Management) and project files carry none.
COPY Directory.Packages.props .
# VERSION is read by Directory.Build.props at build time to stamp the assembly version.
COPY VERSION .
COPY libs/ libs/
COPY server/ server/

RUN dotnet publish server/BeeMemoryBank.Api/BeeMemoryBank.Api.csproj \
    -c Release -o /app/api
RUN dotnet publish server/BeeMemoryBank.BlindConsole/BeeMemoryBank.BlindConsole.csproj \
    -c Release -o /app/console
# The CLI is the break-glass path for a node whose console password is lost (bmb blind init).
RUN dotnet publish server/BeeMemoryBank.Cli/BeeMemoryBank.Cli.csproj \
    -c Release -o /app/cli

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
        curl bzip2 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# restic, downloaded once at BUILD time and verified against the checksum published in the
# release's SHA256SUMS — a build that cannot prove the binary is the released one must fail,
# not continue with whatever the CDN handed over. Bump version AND both checksums together.
# TARGETARCH comes from BuildKit (amd64 on a PC/NAS, arm64 on a Raspberry Pi-class box).
ARG TARGETARCH=amd64
ARG RESTIC_VERSION=0.17.3
ARG RESTIC_SHA256_AMD64=5097faeda6aa13167aae6e36efdba636637f8741fed89bbf015678334632d4d3
ARG RESTIC_SHA256_ARM64=db27b803534d301cef30577468cf61cb2e242165b8cd6d8cd6efd7001be2e557
RUN set -eux; \
    case "${TARGETARCH}" in \
        amd64) sha="${RESTIC_SHA256_AMD64}" ;; \
        arm64) sha="${RESTIC_SHA256_ARM64}" ;; \
        *) echo "no pinned restic checksum for ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    file="restic_${RESTIC_VERSION}_linux_${TARGETARCH}"; \
    curl -fsSLO "https://github.com/restic/restic/releases/download/v${RESTIC_VERSION}/${file}.bz2"; \
    echo "${sha}  ${file}.bz2" | sha256sum -c -; \
    bunzip2 "${file}.bz2"; \
    mv "${file}" /usr/local/bin/restic; \
    chmod 0755 /usr/local/bin/restic; \
    restic version

COPY --from=build /app/api ./api/
COPY --from=build /app/console ./console/
COPY --from=build /app/cli ./cli/
COPY docker/blind/entrypoint.sh .
RUN chmod +x entrypoint.sh \
    && printf '#!/bin/sh\nexec dotnet /app/cli/bmb.dll "$@"\n' > /usr/local/bin/bmb \
    && chmod 0755 /usr/local/bin/bmb

# Defaults for every process in the container, `docker exec … bmb blind …` included: the data
# volume, the two ports the Api listens on (TLS on the published sync port, plain HTTP on a
# loopback port for local tools — see ApiServices.UseBlindHttps), and where the CLI and the console
# find it (the local one; the CLI's own default is a full node's 5300).
ENV BMB_DATA_PATH=/app/data \
    BMB_BLIND_HTTPS_PORT=5610 \
    BMB_BLIND_LOCAL_PORT=5612 \
    BMB_API_URL=http://127.0.0.1:5612

# 5610 — sync surface, published on the LAN. 5611 — the console, published on the HOST's
# loopback only (see compose.yaml; binding the container side to 127.0.0.1 would break the
# host-loopback publish, the same DNAT-shape as the main image's API port).
EXPOSE 5610 5611

# Both listeners, because either one alone is a half-working node: the TLS port is what the mesh
# and the pairing dial (curl -k: the certificate is self-signed and pinned by the peers, not by a
# CA), the loopback port is what the console and `bmb` use. 5610 answers HTTPS only — a plain
# request to it now fails, which is exactly what used to make this healthcheck the last thing
# still "working" on a node that could not be paired.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsk https://localhost:5610/health -o /dev/null \
     && curl -fs http://localhost:5612/health -o /dev/null || exit 1

ENTRYPOINT ["./entrypoint.sh"]
