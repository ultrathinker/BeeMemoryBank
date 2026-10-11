#!/usr/bin/env bash
# The Docker gate of the encrypted database (docs/design/step-b-spec.md 15.5, "Docker smoke"): nine steps against a real image, one container at a
# time, every step failing loudly at the first deviation. It is run by the owner or the orchestrator, never by a track agent.
#
#   scripts/sealed-gate.sh
#
# Needs: docker with the compose plugin, curl and bash. The images are built BEFORE, outside this script, under the same limits (the legacy
# builder honours them):
#   DOCKER_BUILDKIT=0 docker build --memory=3g --memory-swap=3g --cpuset-cpus=0-1 -t bmb-gate:local .
#   BMB_LAN_ADDR=127.0.0.1 DOCKER_BUILDKIT=0 docker compose -f docker/blind/compose.yaml build      # the image "bmb-blind", for step 9 only
# This script never builds and never passes --build: a build under the shared budget is a decision of the gate owner (spec 15.5).
#
# Limits (PRODUCT-RULES; the owner's rule of 10.10.2026): the full node runs with cpus 2 and mem_limit 3g, one container at a time; in step 9, the one
# step that needs two programs alive together, the full node gets cpus 1 / 1792m and the blind node cpus 1 / 1280m (the same total). The override files
# that say so are written to the scratch folder, never to the repository. Nothing here raises a limit: a command the limit kills (exit 137) is a finding.
#
# What it removes: nothing. Containers are stopped and stay (named in the last line), the scratch folder and the data folders stay for the gate owner to
# look at and to delete. It touches no file of the repository.
#
# Environment (all optional):
#   GATE_IMAGE      the full-node image, default bmb-gate:local
#   GATE_SCRATCH    the scratch folder, default a new mktemp -d (its data folders are created inside)
#   GATE_PORT       host loopback port of the Api, default 15300;  GATE_WEB_PORT the Web port, default 15301
#   GATE_PASSWORD   the master password of the canary vault, default a fixed strong one
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
image="${GATE_IMAGE:-bmb-gate:local}"
port="${GATE_PORT:-15300}"
web_port="${GATE_WEB_PORT:-15301}"
password="${GATE_PASSWORD:-Gate-Pass-12345}"
scratch="${GATE_SCRATCH:-$(mktemp -d)}"
mkdir -p "$scratch"
api="http://127.0.0.1:$port"
project=bmbgate
canaries=("BMBGATE-CANARY-7c41e9a0-note-one" "BMBGATE-CANARY-3f8d22b6-note-two" "BMBGATE-CANARY-91ab05d7-folder")
internal_key="$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')"
auto_key_file="$scratch/bmb-auto-unlock.key"
data1="$scratch/data1"
data2="$scratch/data2"
admin_id=""

step=0
say() { printf '\n== step %s: %s\n' "$step" "$*"; }
fail() { printf '\n!! step %s FAILED: %s\n' "$step" "$*" >&2; exit 1; }
on_exit() {
    rc=$?
    if [ "$rc" -ne 0 ]; then
        echo "--- last log lines of the full node ---" >&2
        compose logs --tail 60 bmb >&2 2>&1 || true
    fi
    compose stop -t 60 >/dev/null 2>&1 || true
    echo "gate end (exit $rc). Left for the gate owner, nothing removed: scratch folder $scratch; containers of project $project (docker ps -a) and bmb-blind."
}
trap on_exit EXIT

# ---- the override files: limits, the Api port on the host's loopback, the internal key, and (a second file) the key file as a secret ------------------

cat > "$scratch/override.yml" <<EOF
services:
  bmb:
    image: $image
    pull_policy: never
    cpus: 2
    mem_limit: 3g
    memswap_limit: 3g
    ports:
      - "127.0.0.1:$port:5300"
    environment:
      - BMB_INTERNAL_KEY=$internal_key
EOF

cat > "$scratch/override-secret.yml" <<EOF
services:
  bmb:
    environment:
      - BMB_AUTO_UNLOCK_KEY_FILE=/run/secrets/bmb-auto-unlock-key
    secrets:
      - source: bmb_auto_unlock_key
        target: bmb-auto-unlock-key
secrets:
  bmb_auto_unlock_key:
    file: $auto_key_file
EOF

cat > "$scratch/override-step9-full.yml" <<EOF
services:
  bmb:
    cpus: 1
    mem_limit: 1792m
    memswap_limit: 1792m
EOF

cat > "$scratch/override-step9-blind.yml" <<EOF
services:
  blind:
    pull_policy: never
    cpus: 1
    mem_limit: 1280m
    memswap_limit: 1280m
EOF

# The scan (a script of its own so that no quoting gets between it and grep): every file of a folder, for each canary as bytes and as UTF-16.
cat > "$scratch/scan.sh" <<'EOF'
#!/bin/bash
# usage: scan.sh <folder> <canary>...   prints the files that hold a canary, one per line, sorted
dir="$1"; shift
for c in "$@"; do
    grep -rlaF -- "$c" "$dir" 2>/dev/null || true
    wide="$(printf '%s' "$c" | sed 's/./&\\x00/g')"
    grep -rlaP -- "$wide" "$dir" 2>/dev/null || true
done | sort -u
EOF
chmod +x "$scratch/scan.sh"

# ---- helpers ------------------------------------------------------------------------------------------------------------------------------------

use_secret=0
data_dir="$data1"
compose() {
    local files=(-f "$here/docker-compose.yml" -f "$scratch/override.yml")
    [ "$use_secret" = 1 ] && files+=(-f "$scratch/override-secret.yml")
    [ -n "${EXTRA_OVERRIDE:-}" ] && files+=(-f "$EXTRA_OVERRIDE")
    BMB_DATA_PATH="$data_dir" BMB_WEB_PORT="$web_port" docker compose -p "$project" "${files[@]}" "$@"
}

# curl against the Api with the internal key and the superadmin identity of step 1. Sets $code and $body (the answer's text).
call() {  # method path [json]
    local method="$1" path="$2" json="${3:-}"
    local args=(-sS --max-time 60 -o "$scratch/body" -w '%{http_code}' -X "$method" "$api$path"
        -H "X-Internal-Key: $internal_key" -H "X-User-Id: ${admin_id:-1}" -H "X-User-Role: superadmin" -H 'Content-Type: application/json')
    [ -n "$json" ] && args+=(--data "$json")
    code="$(curl "${args[@]}" || true)"
    body="$(cat "$scratch/body" 2>/dev/null || true)"
}

expect() {  # expected-code method path [json]   - the answer must be that status
    local want="$1"; shift
    call "$@"
    [ "$code" = "$want" ] || fail "$2 $3 answered $code, expected $want: $body"
}

wait_health() {
    local i c
    for i in $(seq 1 60); do
        c="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$api/health" || true)"
        [ "$c" = 200 ] && return 0
        sleep 3
    done
    fail "the node did not answer /health in 3 minutes"
}

field() {  # name  - a string or number field of the last body, from the flat JSON the status routes write
    printf '%s' "$body" | sed -n "s/.*\"$1\":\"\\{0,1\\}\\([^\",}]*\\).*/\\1/p" | head -n 1
}

wait_state() {  # want [seconds]
    local want="$1" limit="${2:-90}" i
    for i in $(seq 1 "$limit"); do
        call GET /api/vault/status
        [ "$(field state)" = "$want" ] && return 0
        sleep 1
    done
    fail "the node did not reach the state $want (last: $body)"
}

restart_node() { compose restart bmb >/dev/null; wait_health; }
start_node() { compose up -d --no-build bmb >/dev/null; wait_health; }
stop_node() { compose stop -t 120 bmb >/dev/null; }

scan() {  # folder  - prints the files that hold a canary; returns 0 always
    docker run --rm --cpus 1 --memory 512m -v "$1:/scan:ro" -v "$scratch/scan.sh:/scan.sh:ro" --entrypoint bash "$image" /scan.sh /scan "${canaries[@]}"
}

unlock() { expect 200 POST /api/session/unlock "{\"password\":\"$password\"}"; }

# ---- 1. start, initialise a vault through the setup API, add the canary notes ---------------------------------------------------------------------

step=1; say "start, set up a vault, add the canary notes"
mkdir -p "$data1"
printf '%s' "$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')" > "$auto_key_file"
chmod 0400 "$auto_key_file"
start_node
expect 200 POST /api/init/standalone "{\"adminUsername\":\"admin\",\"displayName\":\"Gate node\",\"password\":\"$password\"}"
expect 200 POST /api/session/login "{\"username\":\"admin\",\"password\":\"$password\"}"
admin_id="$(printf '%s' "$body" | sed -n 's/.*"userId":\([0-9]*\).*/\1/p')"
[ -n "$admin_id" ] || fail "no user id in the login answer: $body"
for i in 0 1 2; do
    expect 200 POST /api/articles "{\"title\":\"${canaries[$i]}\",\"treePath\":\"/Gate\",\"content\":\"body of ${canaries[$i]}\"}"
done
expect 200 GET /api/articles
printf '%s' "$body" | grep -q "${canaries[0]}" || fail "the canary note is not listed: $body"
echo "control: a plain vault shows its canaries to a raw scan"
stop_node
hits="$(scan "$data1")"
[ -n "$hits" ] || fail "the control scan of the PLAIN data folder found no canary: the scan itself is broken"
start_node

# ---- 2. activate (Step A) with a single node -------------------------------------------------------------------------------------------------------

step=2; say "activate the new sync format (single node)"
unlock
expect 200 POST /api/sync/activation "{\"password\":\"$password\"}"

# ---- 3. encrypt, wait for done -------------------------------------------------------------------------------------------------------------------------

step=3; say "POST /api/vault/seal, wait for done"
call GET /api/vault/seal/preflight
echo "preflight: $body"
expect 202 POST /api/vault/seal "{\"password\":\"$password\"}"
phase=""
for i in $(seq 1 300); do
    call GET /api/vault/seal/progress
    phase="$(field phase)"
    [ "$phase" = done ] && break
    [ "$phase" = failed ] && fail "the migration failed: $body"
    sleep 3
done
[ "$phase" = done ] || fail "the migration did not finish in 15 minutes (last: $body)"
echo "migration: $body"

# ---- 4. restart: closed, 503 on content --------------------------------------------------------------------------------------------------------------

step=4; say "restart: /health 200, sealed and Closed, a content route answers 503 vault_sealed"
restart_node
call GET /api/vault/status
[ "$(field mode)" = sealed ] || fail "the status does not say sealed: $body"
[ "$(field state)" = Closed ] || fail "the node is not Closed after a restart: $body"
call GET /api/articles
[ "$code" = 503 ] || fail "GET /api/articles answered $code while closed, expected 503: $body"
printf '%s' "$body" | grep -q vault_sealed || fail "the 503 does not say vault_sealed: $body"

# ---- 5. unlock with the master password ------------------------------------------------------------------------------------------------------------

step=5; say "unlock with the master password: the content is readable"
unlock
expect 200 GET /api/articles
printf '%s' "$body" | grep -q "${canaries[0]}" || fail "the canary note is not readable after the unlock: $body"

# ---- 6. stop, scan the whole data folder, start -----------------------------------------------------------------------------------------------------

step=6; say "stop, scan the whole data volume for the canaries (none), start again"
stop_node
hits="$(scan "$data1")"
[ -z "$hits" ] || fail "an encrypted node's data folder still shows a canary in: $hits"
start_node

# ---- 7. the server checkbox ------------------------------------------------------------------------------------------------------------------------------

step=7; say "Start unlocked after a restart: enable with the two confirmations, restart (Open), disable, restart (Closed), enable with a key file from outside the volume"
unlock
expect 400 POST /api/keys/server-auto-unlock/enable "{\"password\":\"$password\"}"
expect 200 POST /api/keys/server-auto-unlock/enable "{\"password\":\"$password\",\"confirmation\":\"START UNLOCKED\"}"
restart_node
wait_state Open 90
expect 200 POST /api/keys/server-auto-unlock/disable "{\"password\":\"$password\"}"
restart_node
call GET /api/vault/status
[ "$(field state)" = Closed ] || fail "the node came up $(field state) after the checkbox was turned off: $body"
# Again, with the secret as a Docker secret mounted from outside the volume (BMB_AUTO_UNLOCK_KEY_FILE): the container is recreated, the volume stays.
use_secret=1
start_node
unlock
expect 200 POST /api/keys/server-auto-unlock/enable "{\"password\":\"$password\",\"confirmation\":\"START UNLOCKED\"}"
restart_node
wait_state Open 90

# ---- 8. file-level copy with the excludes, restore into a fresh directory, the node comes up Closed ----------------------------------------------

step=8; say "stop, copy the volume with the excludes into a fresh directory, start with the secret still mounted: Closed; unlock; bmb vault verify says ok; the checkbox is off"
stop_node
mkdir -p "$data2"
docker run --rm --cpus 1 --memory 512m -v "$data1:/from:ro" -v "$data2:/to" --entrypoint bash "$image" -c \
    "cd /from && tar --exclude 'server-auto-unlock.*' --exclude os-auto-unlock.dat -cf - . | tar -C /to -xf -"
[ ! -e "$data2/server-auto-unlock.enabled" ] || fail "the excludes did not keep the enable record out of the copy"
data_dir="$data2"
start_node
call GET /api/vault/status
[ "$(field state)" = Closed ] || fail "a restored copy came up $(field state) with the key file mounted, expected Closed (stale slot): $body"
unlock
expect 200 GET /api/articles
printf '%s' "$body" | grep -q "${canaries[0]}" || fail "the canary note is not readable in the restored copy: $body"
expect 200 GET /api/keys/server-auto-unlock/status
printf '%s' "$body" | grep -Eq '"enabled":\s*false' || fail "the checkbox is not off in the restored copy: $body"
stop_node
printf '%s\n' "$password" | compose run --rm --no-deps -T --entrypoint dotnet bmb /app/cli/bmb.dll vault verify --data /app/data --password-stdin | tee "$scratch/verify.out"
[ "$(tail -n 1 "$scratch/verify.out" | tr -d '\r ')" = ok ] || fail "bmb vault verify did not say ok: $(cat "$scratch/verify.out")"
use_secret=0

# ---- 9. the blind container --------------------------------------------------------------------------------------------------------------------------------

step=9; say "the blind container: pair with the canary full node, reseed in format 2, back up, verify, restore the latest snapshot, scan, restore a new node"
# The one step that needs two programs alive together. Nothing else of the gate runs; the total is the same as for one container.
docker image inspect bmb-blind >/dev/null 2>&1 || fail "the image bmb-blind is not built (see the header of this script)"
data_dir="$data1"
EXTRA_OVERRIDE="$scratch/override-step9-full.yml"
start_node
unlock
BMB_LAN_ADDR=127.0.0.1 docker compose -f "$here/docker/blind/compose.yaml" -f "$scratch/override-step9-blind.yml" up -d --no-build
for i in $(seq 1 60); do
    [ "$(docker inspect -f '{{.State.Running}}' bmb-blind 2>/dev/null)" = true ] && docker exec bmb-blind bmb blind status >/dev/null 2>&1 && break
    sleep 3
done
printf 'console_password=%s\nrestic_password=%s\n' "$password" "$password" | docker exec -i bmb-blind bmb blind init --repo-folder /backups/restic
pair_json="$(docker exec bmb-blind bmb blind pair-code)"
pair_code="$(printf '%s' "$pair_json" | sed -n 's/.*"code":"\([^"]*\)".*/\1/p')"
[ -n "$pair_code" ] || fail "no pair code from the blind node: $pair_json"
expect 200 POST /api/blind-nodes "{\"code\":\"$pair_code\"}"   # adds the node and seeds it in format 2
docker exec bmb-blind bmb blind backup now
for i in $(seq 1 120); do
    jobs="$(docker exec bmb-blind bmb blind jobs 2>&1 || true)"
    printf '%s' "$jobs" | grep -qi running || break
    sleep 5
done
docker exec bmb-blind bmb blind backup verify --full
restore_dir="$scratch/blind-restore"
mkdir -p "$restore_dir"
# The latest snapshot of the restic repository, restored by the same restic into a throw-away folder of the blind container (a name of its own; nothing
# is removed), streamed out to the host and scanned there.
docker exec bmb-blind bash -c "t=/tmp/restore-gate-\$\$ && RESTIC_PASSWORD='$password' restic -r /backups/restic restore latest --target \$t >/dev/null && tar -C \$t -cf - ." | tar -C "$restore_dir" -xf -
hits="$(scan "$restore_dir")"
[ -z "$hits" ] || fail "a restored blind backup shows a canary in: $hits"
echo "blind backup: no canary in any restored file"
# The backup folder a new node restores from (docs/design/step-a-spec.md 12): the operational database copy, baselines/current/, and the recovery set that
# lies beside the repository.
folder="$scratch/blind-folder"
mkdir -p "$folder/baselines"
staged="$(find "$restore_dir" -path '*/blind/stage/beememorybank.db' -type f | head -n 1)"
[ -n "$staged" ] || fail "the restored backup holds no database copy"
cp "$staged" "$folder/beememorybank.db"
current="$(find "$restore_dir" -path '*/baselines/current' -type d | head -n 1)"
[ -n "$current" ] || fail "the restored backup holds no baselines/current"
cp -r "$current" "$folder/baselines/current"
docker cp bmb-blind:/backups/restic.recovery-set.json "$folder/restic.recovery-set.json"
# Both programs stop; a new node is restored from that folder, alone in the container, with the folder mounted read-only.
docker stop -t 60 bmb-blind >/dev/null
stop_node
cat > "$scratch/override-step9-restore.yml" <<EOF2
services:
  bmb:
    volumes:
      - "$folder:/restore:ro"
EOF2
EXTRA_OVERRIDE="$scratch/override-step9-restore.yml"
data_dir="$scratch/data3"
mkdir -p "$data_dir"
start_node
expect 202 POST /api/restore/backup "{\"path\":\"/restore\",\"password\":\"$password\",\"adminUsername\":\"admin\",\"displayName\":\"Restored node\"}"
for i in $(seq 1 120); do
    call GET /api/restore/progress
    case "$body" in *'"phase":"done"'*|*'"done":true'*|*complete*) break ;; esac
    case "$body" in *error*|*failed*) fail "the restore from the blind backup folder failed: $body" ;; esac
    sleep 5
done
echo "restore from the blind backup folder: $body"

echo
echo "sealed-gate: all nine steps passed"
