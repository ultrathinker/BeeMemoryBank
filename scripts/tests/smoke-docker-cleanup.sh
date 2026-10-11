#!/usr/bin/env bash
# Cleanup contract of scripts/smoke-docker.sh (BMB-227): by default the smoke container is removed when
# the run ends, on success, failure, interruption, and startup/cleanup faults alike (after the failure's
# last log lines are printed), and BMB_SMOKE_KEEP=1 keeps it, named in the last line. Runs the script
# against an image already on this PC (no build) and checks what is left behind. Leftovers are found by
# diffing the bmb-smoke-* container list around each run, not by parsing the script's output (a successful
# default run names no container). A smoke run started by someone else in the same minutes would be
# counted as a leftover; nothing else about the check assumes exclusivity. The test removes every
# container it started, also when it fails.
#
#   scripts/tests/smoke-docker-cleanup.sh <image-ref> [expected-version]
set -euo pipefail

img="${1:?usage: $0 <image-ref> [expected-version]}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
want="${2:-$(tr -d '[:space:]' < "$root/VERSION")}"
script="$root/scripts/smoke-docker.sh"

fail() { echo "FAIL: $*" >&2; exit 1; }

smoke_ids() { docker ps -aq --filter name=bmb-smoke- | sort; }

ids_to_names() {   # ids_to_names <ids...> -> container names, without the leading '/'
    local id
    for id in "$@"; do
        docker inspect -f '{{.Name}}' "$id" | sed 's#^/##'
    done
}

record_new_containers() {   # record_new_containers <before-ids> <after-ids>
    local before="$1" after="$2" id
    for id in $(comm -13 <(printf '%s' "$before") <(printf '%s' "$after")); do
        mine="$mine $(ids_to_names "$id")"
    done
}

mine=""   # every container this test started, for the way out

cleanup() {   # never leave anything behind, not even on a failed assertion
    local n
    for n in $mine; do
        docker rm -f "$n" >/dev/null 2>&1 || true
    done
}
trap cleanup EXIT

rc_smoke=0
out=""
run_smoke() {   # run_smoke <version> [keep]; fills $out, $rc_smoke; appends new containers to $mine
    local v="$1" mode="${2:-}" rc=0 before after id
    before="$(smoke_ids)"
    # MSYS_NO_PATHCONV: Git Bash would rewrite the script's in-container paths (/app/...) into Windows
    # paths before docker.exe sees them; on Linux CI the variable is simply ignored.
    if [ "$mode" = keep ]; then
        out="$(MSYS_NO_PATHCONV=1 BASH_ENV="$root/scripts/tests/smoke-docker-fault-env.sh" \
            BMB_DOCKER_AGENT_LABEL=fix227b BMB_SMOKE_KEEP=1 bash "$script" "$img" full "$v" 2>&1)" || rc=$?
    else
        out="$(MSYS_NO_PATHCONV=1 BASH_ENV="$root/scripts/tests/smoke-docker-fault-env.sh" \
            BMB_DOCKER_AGENT_LABEL=fix227b bash "$script" "$img" full "$v" 2>&1)" || rc=$?
    fi
    rc_smoke=$rc
    after="$(smoke_ids)"
    record_new_containers "$before" "$after"
}

leftover() {   # leftover <names...> -> prints the names that are still containers
    local n
    for n in "$@"; do
        docker inspect -f '{{.State.Status}}' "$n" >/dev/null 2>&1 && echo "$n"
    done
    return 0
}

echo "== 1. success run, no BMB_SMOKE_KEEP (image $img, expected version $want)"
run_smoke "$want"
[ "$rc_smoke" -eq 0 ] || fail "the smoke itself failed (rc=$rc_smoke): $out"
left="$(leftover $mine)"
[ -z "$left" ] || fail "a successful run left container(s) behind: $left"
printf '%s\n' "$out" | grep -q "last log lines" && fail "a successful run printed failure logs"

echo "== 2. forced failure (wrong expected version), no BMB_SMOKE_KEEP"
run_smoke "0.0.0-smoke227-wrong"
[ "$rc_smoke" -ne 0 ] || fail "the smoke passed with a wrong expected version"
left="$(leftover $mine)"
[ -z "$left" ] || fail "a failed run left container(s) behind: $left"
printf '%s\n' "$out" | grep -q "last log lines" || fail "a failed run printed no last log lines"

echo "== 3. success run with BMB_SMOKE_KEEP=1"
run_smoke "$want" keep
[ "$rc_smoke" -eq 0 ] || fail "the smoke failed under BMB_SMOKE_KEEP=1 (rc=$rc_smoke): $out"
kept="$(leftover $mine)"
[ -n "$kept" ] || fail "BMB_SMOKE_KEEP=1 did not keep the container"
[ "$(printf '%s\n' $kept | wc -l)" = 1 ] || fail "expected exactly one kept container, got: $kept"
last="$(printf '%s\n' "$out" | tail -n 1)"
case "$last" in
    *"$(printf '%s\n' $kept)"*) ;;
    *) fail "with BMB_SMOKE_KEEP=1 the last line must name the container, got: $last" ;;
esac
echo "   keep works, removing $kept myself"
docker rm "$kept" >/dev/null
mine=""

echo "== 4. Ctrl-C during a run removes the container by default"
run_interrupt() {
    local rc=0
    before="$(smoke_ids)"
    out="$(MSYS_NO_PATHCONV=1 BASH_ENV="$root/scripts/tests/smoke-docker-fault-env.sh" \
        env BMB_DOCKER_AGENT_LABEL=fix227b BMB_SMOKE_TEST_INTERRUPT=1 bash "$script" "$img" full "$want" 2>&1)" || rc=$?
    rc_smoke=$rc
    after="$(smoke_ids)"
    record_new_containers "$before" "$after"
    [ "$rc_smoke" -ne 0 ] || fail "Ctrl-C did not make the smoke run fail"
    left="$(leftover $mine)"
    [ -z "$left" ] || fail "Ctrl-C left container(s) behind: $left"
}
run_interrupt

run_faulted() {   # run_faulted <fault-variable>
    local fault="$1" rc=0
    before="$(smoke_ids)"
    out=""
    out="$(MSYS_NO_PATHCONV=1 BASH_ENV="$root/scripts/tests/smoke-docker-fault-env.sh" \
        env BMB_DOCKER_AGENT_LABEL=fix227b "${fault}=1" bash "$script" "$img" full "$want" 2>&1)" || rc=$?
    rc_smoke=$rc
    after="$(smoke_ids)"
    record_new_containers "$before" "$after"
    [ "$rc_smoke" -ne 0 ] || fail "$fault fault did not make the smoke run fail"
    left="$(leftover $mine)"
    [ -z "$left" ] || fail "$fault fault left container(s) behind: $left"
}

echo "== 5. docker run failure after creation is cleaned up"
run_faulted BMB_SMOKE_TEST_FAIL_RUN

echo "== 6. final docker stop failure is cleaned up"
run_faulted BMB_SMOKE_TEST_FAIL_STOP

echo "== 7. final docker rm failure is cleaned up"
run_faulted BMB_SMOKE_TEST_FAIL_REMOVE

echo "OK: the smoke container is removed by default (success, failure, Ctrl-C and cleanup faults) and kept with BMB_SMOKE_KEEP=1"
