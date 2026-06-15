#!/usr/bin/env bash
#
# BeeMemoryBank host-side self-update.
#
# Triggered by the systemd path-unit (bmb-update.path) when the API writes
# `update.request` into the data volume after the admin clicks "Apply update".
# This runs OUTSIDE the container, so it survives `docker compose up -d --build`
# tearing the container down — the very process that requested the update.
#
# Safety: single-flight via flock; the trigger + in-progress markers are ALWAYS
# removed on exit (trap) so a failed git/build can't wedge the path-unit; the
# outcome is written to `update.result` for the admin UI to read back.

set -uo pipefail

# Derive the repo root from this script's own location (scripts/ -> repo root) so the
# file carries no host-specific absolute path. Override the data dir via BMB_DATA_DIR.
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE="deploy/hetzner/docker-compose.yml"
UPDATES="${BMB_DATA_DIR:-/var/lib/beememorybank}/updates"
REQ="$UPDATES/update.request"
INPROG="$UPDATES/update.inprogress"
RESULT="$UPDATES/update.result"
LOG="$UPDATES/update.log"
LOCK="$UPDATES/.update.lock"

mkdir -p "$UPDATES"

ts() { date -u +%FT%TZ; }

write_result() {
    # $1=status $2=exitCode $3=message
    printf '{"status":"%s","exitCode":%s,"message":"%s","finishedAt":"%s"}\n' \
        "$1" "$2" "$3" "$(ts)" > "$RESULT"
}

# Single-flight: bail if another update is already running.
exec 9>"$LOCK"
if ! flock -n 9; then
    echo "$(ts) another update already running, skipping" >> "$LOG"
    exit 0
fi

# Always clear the trigger + in-progress marker, whatever happens below.
# (The result file is intentionally NOT removed — the UI reads it.)
trap 'rm -f "$REQ" "$INPROG"' EXIT

: > "$INPROG"
echo "$(ts) update requested" >> "$LOG"

cd "$REPO" || {
    write_result "error" 1 "repo not found at $REPO"
    echo "$(ts) ERROR repo not found" >> "$LOG"
    exit 1
}

# This unit runs as root but the repo is owned by a regular user, so without this
# git aborts with "fatal: detected dubious ownership in repository".
git config --global --add safe.directory "$REPO" 2>/dev/null || true

echo "$(ts) git fetch + reset --hard origin/master" >> "$LOG"
if ! git fetch origin >> "$LOG" 2>&1 || ! git reset --hard origin/master >> "$LOG" 2>&1; then
    write_result "error" 2 "git update failed"
    echo "$(ts) ERROR git update failed" >> "$LOG"
    exit 2
fi

# Strip whitespace AND any " / \ so the value is always safe to embed in update.result JSON.
NEWVER="$(tr -d '[:space:]"\\' < VERSION 2>/dev/null || echo unknown)"

echo "$(ts) docker compose up -d --build" >> "$LOG"
if docker compose -f "$COMPOSE" up -d --build >> "$LOG" 2>&1; then
    write_result "success" 0 "updated to v${NEWVER}"
    echo "$(ts) OK updated to v${NEWVER}" >> "$LOG"
else
    write_result "error" 3 "docker compose build/up failed"
    echo "$(ts) ERROR docker compose failed" >> "$LOG"
    exit 3
fi
