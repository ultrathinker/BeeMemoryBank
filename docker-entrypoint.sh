#!/bin/sh
# Ensure data directory exists and is writable
mkdir -p /app/data/temp
mkdir -p /app/data/media
exec dotnet BeeMemoryBank.Api.dll "$@"
