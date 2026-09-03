#!/bin/sh
mkdir -p /app/data/temp /app/data/media

# Auto-generate BMB_INTERNAL_KEY if not set — protects API from unauthorized local processes
if [ -z "$BMB_INTERNAL_KEY" ]; then
    KEY_FILE="/app/data/.internal-key"
    if [ ! -f "$KEY_FILE" ]; then
        head -c 32 /dev/urandom | base64 | tr -d '\n' > "$KEY_FILE"
        chmod 600 "$KEY_FILE"
    fi
    export BMB_INTERNAL_KEY=$(cat "$KEY_FILE")
fi

# Start API in background (port 5300), bound to 0.0.0.0 *within this container's network
# namespace*. The container is the isolation boundary here, not the process bind address: what
# decides whether the raw API surface (/api/session/unlock, /api/session/status, /api/join,
# /api/init/reset, /mcp, ...) is reachable from outside is whether the host publishes this port,
# which is why the shipped docker-compose.yml no longer publishes it at all.
#
# Do NOT "harden" this to 127.0.0.1: Docker's published-port DNAT arrives on the container's
# bridge interface, not its loopback, so a loopback-only bind silently breaks every deployment
# that publishes this port on purpose — including the reverse-proxied one where Apache/Nginx
# path-filters to /mcp, /api/sync, /api/join and forwards to a host-loopback-bound mapping
# (`127.0.0.1:5004:5300`). Bind the port on the HOST side to control exposure.
ASPNETCORE_URLS=http://0.0.0.0:5300 \
    dotnet /app/api/BeeMemoryBank.Api.dll &

# Start Web as the main process — Docker monitors this (port 5301)
export ASPNETCORE_URLS=http://0.0.0.0:5301
export BMB_API_URL=http://localhost:5300
cd /app/web && exec dotnet BeeMemoryBank.Web.dll
