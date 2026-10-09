# syntax=docker/dockerfile:1.7
# The line above picks a Dockerfile frontend that knows `ADD --checksum` (below) on any BuildKit, also an older Docker's.

# Full node image: the Api, the Web front, the CLI (the break-glass path) and the embedding model.
# Build context is the REPO ROOT:
#   docker build -t bmb-full:dev .
# Released images are built by .github/workflows/docker-publish.yml (ghcr.io/ultrathinker/beememorybank), for linux/amd64 and
# linux/arm64. Multi-arch like docker/blind/Dockerfile: BuildKit's TARGETARCH picks the runtime identifier, and the build stage
# runs on the BUILD platform (the .NET SDK cross-publishes), so an arm64 image is not compiled under emulation.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY BeeMemoryBank.slnx .
COPY Directory.Build.props .
# Every NuGet version lives here (Central Package Management) and project files carry none.
# Without this COPY the restore inside the container fails with NU1010 for every package.
COPY Directory.Packages.props .
# VERSION is read by Directory.Build.props at build time to stamp the assembly version.
# Without this COPY the version property is silently skipped inside the container build.
COPY VERSION .
# Only libs/ and server/: the Api, the Web front and the CLI reference nothing in tests/ or tools/.
COPY libs/ libs/
COPY server/ server/

# A RID-specific, framework-dependent publish: only the native assets of the target platform are copied (a plain publish
# copies every platform's ONNX Runtime and SQLite, ~250 MB per program), and no apphost, since the entrypoint runs
# `dotnet x.dll`.
# The CLI is the break-glass path: `bmb init reset` wipes a node back to first-run Setup when nobody can sign in to the web UI
# any more (every superadmin account lost, or the Web layer broken). That is precisely the situation where you cannot install
# anything either, so it has to already be inside the image — `docker exec … dotnet /app/cli/bmb.dll init reset`. It is
# published without a model of its own: it uses the Api's (CliServiceProvider.UseSiblingApiModelIfNotBundled).
RUN set -eux; \
    case "${TARGETARCH}" in \
        amd64) rid=linux-x64 ;; \
        arm64) rid=linux-arm64 ;; \
        *) echo "no runtime identifier for ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    dotnet publish server/BeeMemoryBank.Api/BeeMemoryBank.Api.csproj \
        -c Release -r "${rid}" --no-self-contained -p:UseAppHost=false -o /app/api; \
    dotnet publish server/BeeMemoryBank.Web/BeeMemoryBank.Web.csproj \
        -c Release -r "${rid}" --no-self-contained -p:UseAppHost=false -o /app/web; \
    dotnet publish server/BeeMemoryBank.Cli/BeeMemoryBank.Cli.csproj \
        -c Release -r "${rid}" --no-self-contained -p:UseAppHost=false -p:BmbBundleModel=false -o /app/cli

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/api ./api/
COPY --from=build /app/web ./web/
COPY --from=build /app/cli ./cli/

# The embedding model (search by meaning, concept tags), multilingual-e5-small as exported to ONNX by Xenova (MIT).
# Downloaded by BuildKit at BUILD time and verified against EmbeddingModelWiring.BundledModelSha256: a build that cannot prove
# the file is the expected model fails, instead of shipping an image whose node rejects its own model and runs without search
# by meaning. After the COPY of api/ on purpose: a model a developer's tree happened to hold is replaced by the verified one.
# The node looks for it next to the Api (a model.onnx in the data volume still comes first, as a repair copy). Change the
# hash here and in the code together; Core.Tests (DockerImageModelPinTests) fails when they differ. The URL names a commit of
# the Hugging Face repository (not `main`), so a later change of the repository cannot alter or break this build; at that
# commit the file's SHA-256 is the one above (checked 2026-10-07 from the repository's X-Linked-ETag).
ADD --chmod=0644 --checksum=sha256:f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193 \
    https://huggingface.co/Xenova/multilingual-e5-small/resolve/761b726dd34fb83930e26aab4e9ac3899aa1fa78/onnx/model_quantized.onnx /app/api/model.onnx

COPY docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh

# The notices of the image library (SkiaSharp and the native libraries it carries) and of the database engine (SQLite3 Multiple Ciphers,
# MIT): their licences ask for them to travel with the binaries.
COPY THIRD-PARTY-NOTICES.txt ./

# The data folder of every process in the container, `docker exec ... bmb` included. The compose files set it too; without it a bare
# `docker run` split the node in two: the Api works in /app/data (the entrypoint runs it from /app) and the Web front in /app/web/data
# (it runs from /app/web), so the web login keys went to a folder that is not the volume and were lost with the container.
ENV BMB_DATA_PATH=/app/data

# Only the Web port. The Api port 5300 (/mcp, sync, an endpoint that checks the master password) is deliberately not
# EXPOSEd, so `docker run -P` and the port dialogs of NAS and container GUIs do not offer it. Publish it on purpose, on the
# host's loopback, when an assistant on this computer needs it (docker-compose.yml shows how).
EXPOSE 5301

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:5300/health || exit 1

ENTRYPOINT ["./docker-entrypoint.sh"]
