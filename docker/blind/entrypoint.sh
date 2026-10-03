#!/bin/bash
# Blind node entrypoint (BMB-54): the blind node host on 5610, console on 5611. Both run as
# children of this script, and whichever exits first takes the container down with it, so the
# restart policy brings back BOTH — a live console in front of a dead sync listener (or the
# reverse) must never look like a running node.
set -e

mkdir -p /app/data/temp /app/data/media /app/data/blind

# Auto-generate BMB_INTERNAL_KEY if not set — shared by the Api, the console and any
# `docker exec … bmb` CLI call; lives in the data volume so it survives re-creation.
if [ -z "$BMB_INTERNAL_KEY" ]; then
    KEY_FILE="/app/data/.internal-key"
    if [ ! -f "$KEY_FILE" ]; then
        head -c 32 /dev/urandom | base64 | tr -d '\n' > "$KEY_FILE"
        chmod 600 "$KEY_FILE"
    fi
    export BMB_INTERNAL_KEY=$(cat "$KEY_FILE")
fi

# The host's two listeners are decided by the host itself (BlindNodeServices.UseBlindHttps) and not
# by ASPNETCORE_URLS: the sync port speaks HTTPS with the node's self-signed certificate, and a
# plain loopback port sits beside it for local tools. Both default here as well, so the image is
# consistent however it is started — a bare `docker run` gets the same surfaces compose does.
# The sync listener is on the container's bridge (host-side publishing decides exposure — see the
# main image's docker-entrypoint.sh for why a 127.0.0.1 bind here would be wrong); the local one is
# loopback-only inside the container. Keyless callers reach only the PublicSurface routes;
# everything else answers 404 without the key.
export BMB_BLIND_HTTPS_PORT="${BMB_BLIND_HTTPS_PORT:-5610}"
export BMB_BLIND_LOCAL_PORT="${BMB_BLIND_LOCAL_PORT:-5612}"

# What the pair code tells a PC to dial. Compose sets it from BMB_LAN_ADDR; a container started by
# hand is derived from the same variable, because a node without it cannot issue a code at all and
# says so only in the log.
if [ -z "$BMB_PUBLIC_ADDRESS" ] && [ -n "$BMB_LAN_ADDR" ]; then
    export BMB_PUBLIC_ADDRESS="https://${BMB_LAN_ADDR}:${BMB_BLIND_HTTPS_PORT}"
fi

# Where local tools reach the Api: the plain loopback port, never the TLS one (only the mesh holds
# that certificate's pin). Inherited by the console below and by `docker exec … bmb`.
export BMB_API_URL="${BMB_API_URL:-http://127.0.0.1:${BMB_BLIND_LOCAL_PORT}}"

dotnet /app/api/BeeMemoryBank.BlindNode.dll &
api=$!

# The console talks to the Api over the container's own loopback.
(
    cd /app/console
    ASPNETCORE_URLS=http://0.0.0.0:5611 \
        exec dotnet BeeMemoryBank.BlindConsole.dll
) &
console=$!

# docker stop sends SIGTERM to this script (PID 1); pass it on so both hosts shut down cleanly
# instead of being SIGKILLed after the stop timeout.
trap 'kill -TERM "$api" "$console" 2>/dev/null || true' TERM INT

set +e
wait -n "$api" "$console"
status=$?
kill -TERM "$api" "$console" 2>/dev/null
wait
exit "$status"
