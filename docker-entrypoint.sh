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

# Start API in background, bound to loopback ONLY (port 5300). AgentAuthMiddleware's auth model
# assumes the MCP endpoint (and every other unauthenticated-looking route: /api/session/unlock,
# /api/session/status, /api/join, /api/init/reset, ...) is unreachable from outside this
# container/host — that assumption used to be false for the shipped docker-compose.yml, which
# published 5300 straight to the host with the API listening on 0.0.0.0 (H4). Both processes
# share this container's network namespace, so Web (below) still reaches the API over
# localhost exactly as before — only external reachability changes. See docs/deployment.md's
# "Reverse Proxy — What Is Exposed" for the equivalent from-source setup, and note that this
# compose file itself no longer publishes the API port at all (belt AND suspenders).
ASPNETCORE_URLS=http://127.0.0.1:5300 \
    dotnet /app/api/BeeMemoryBank.Api.dll &

# Start Web as the main process — Docker monitors this (port 5301)
export ASPNETCORE_URLS=http://0.0.0.0:5301
export BMB_API_URL=http://localhost:5300
cd /app/web && exec dotnet BeeMemoryBank.Web.dll
