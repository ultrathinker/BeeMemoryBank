#!/bin/bash
# Full node entrypoint: the Api on 5300 and the Web front on 5301. Both run as children of this script, and whichever exits
# first takes the container down with it, so the restart policy brings back BOTH; `docker stop` reaches both of them (the
# same shape as docker/blind/entrypoint.sh).
set -e

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
# path-filters to /mcp, /api/sync, /api/join, /api/join/abort and forwards to a host-loopback-bound mapping
# (`127.0.0.1:5004:5300`). Bind the port on the HOST side to control exposure.
ASPNETCORE_URLS=http://0.0.0.0:5300 \
    dotnet /app/api/BeeMemoryBank.Api.dll &
api=$!

# The Web front (port 5301), talking to the Api over the container's own loopback.
(
    cd /app/web
    ASPNETCORE_URLS=http://0.0.0.0:5301 BMB_API_URL=http://localhost:5300 \
        exec dotnet BeeMemoryBank.Web.dll
) &
web=$!

# docker stop sends SIGTERM to this script (PID 1); pass it on so both hosts shut down cleanly (the Api closes its database)
# instead of being SIGKILLed after the stop timeout.
stopping=
trap 'stopping=1; kill -TERM "$api" "$web" 2>/dev/null || true' TERM INT

set +e
wait -n "$api" "$web"
status=$?
kill -TERM "$api" "$web" 2>/dev/null
if [ -n "$stopping" ]; then
    # Stopped from outside: the stop is clean when both hosts shut down cleanly.
    wait "$api"; api_status=$?
    wait "$web"; web_status=$?
    [ "$api_status" -eq 0 ] && [ "$web_status" -eq 0 ] && exit 0
    exit 143
fi
wait
exit "$status"
