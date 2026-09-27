#!/bin/bash
# Blind node entrypoint (BMB-54): Api in blind role on 5610, console on 5611. Both run as
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

# The Api listens on the container's bridge (host-side publishing decides exposure — see the
# main image's docker-entrypoint.sh for why a 127.0.0.1 bind here would be wrong). Keyless
# callers reach only the PublicSurface routes; everything else answers 404 without the key.
BMB_ROLE=blind \
ASPNETCORE_URLS=http://0.0.0.0:5610 \
    dotnet /app/api/BeeMemoryBank.Api.dll &
api=$!

# The console talks to the Api over the container's own loopback.
(
    cd /app/console
    BMB_API_URL=http://127.0.0.1:5610 \
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
