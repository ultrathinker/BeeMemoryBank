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

# Start API in background (port 5300)
ASPNETCORE_URLS=http://0.0.0.0:5300 \
    dotnet /app/api/BeeMemoryBank.Api.dll &

# Start Web as the main process — Docker monitors this (port 5301)
export ASPNETCORE_URLS=http://0.0.0.0:5301
export BMB_API_URL=http://localhost:5300
cd /app/web && exec dotnet BeeMemoryBank.Web.dll
