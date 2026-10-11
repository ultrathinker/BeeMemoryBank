#!/usr/bin/env bash
# TERM at once (BMB-185): a full node container that is stopped a moment after it starts must stop by itself.
#
# PID 1 of the container is the entrypoint script. The kernel drops a signal that PID 1 has no handler for, so a TERM that
# arrives before the script has installed its trap is lost: the container runs on until `docker stop` SIGKILLs it after the
# stop timeout (exit 137). Before the trap was moved to the top of the script, that window lasted up to about 8 ms after the
# start. A `docker kill` from the host arrives later than that, so each round sends the TERM from inside the container, 2 to
# 20 ms after the entrypoint starts (the delay changes from round to round); the kernel treats it exactly like one from the
# host. A TERM within the first 1 to 2 ms, before the shell has run its first line, cannot be caught by any script: PID 1
# has no handler yet. That case is outside this test (a real init such as `docker run --init` would cover it; not tested here).
#
#   scripts/test-entrypoint-term.sh <image-ref> [rounds] [entrypoint]
#   e.g. scripts/test-entrypoint-term.sh bmb-full:dev 40
#        scripts/test-entrypoint-term.sh bmb-blind:dev 40 /app/entrypoint.sh
#
# The entrypoint is the script in the image: /app/docker-entrypoint.sh (the full node, the default) or /app/entrypoint.sh
# (the blind node). The image is tested as it is. To test another version of an entrypoint, build an image on top of the
# image under test with that script (`FROM <image-ref>` and a `COPY` of the script to the same path).
#
# Each container runs with --cpus 2 --memory 4g, or, on the T480 node, with the runner's $BMB_DOCKER_ARGS minus --rm (the
# cpuset, memory and the bmb.agent label stay): this script reads each exit code before it removes the container, so the
# container must not vanish first. A round passes when the container exits by itself within STOP_LIMIT seconds with exit
# code 0 or 143 (a clean stop of both hosts, or the TERM ending them). It fails when it is still running after that, or exits
# any other way (137 = SIGKILL).
set -euo pipefail
export MSYS_NO_PATHCONV=1   # Git Bash: keep the container paths (/app/...) as they are

STOP_LIMIT=8   # seconds; `docker stop` waits 10 s by default before it SIGKILLs

if [ "$#" -lt 1 ] || [ "$#" -gt 3 ]; then
    echo "usage: $0 <image-ref> [rounds] [entrypoint]" >&2
    exit 2
fi
img="$1"
rounds="${2:-40}"
entry="${3:-/app/docker-entrypoint.sh}"

if [ -n "${BMB_DOCKER_ARGS:-}" ]; then
    limits=( ${BMB_DOCKER_ARGS//--rm/} )
else
    limits=( --cpus 2 --memory 4g )
fi

failures=0
for i in $(seq 1 "$rounds"); do
    name="bmb-term-$$-$i"
    delay="$(printf '0.%03d' $(( 2 + i % 19 )))"   # 0.002 to 0.020 seconds
    docker run -d --name "$name" "${limits[@]}" --entrypoint /bin/sh "$img" \
        -c "(sleep $delay; kill -TERM 1) & exec /bin/bash $entry" >/dev/null

    # The container's exit code, or "running" when it is still up after STOP_LIMIT seconds.
    if code="$(timeout "$STOP_LIMIT" docker wait "$name" 2>/dev/null)"; then
        if [ "$code" != 0 ] && [ "$code" != 143 ]; then
            echo "round $i (TERM after ${delay}s): exited with $code" >&2
            failures=$((failures + 1))
        fi
    else
        echo "round $i (TERM after ${delay}s): still running after ${STOP_LIMIT}s, the TERM was lost" >&2
        failures=$((failures + 1))
    fi
    docker rm -f "$name" >/dev/null 2>&1 || true
done

echo "term test: $((rounds - failures))/$rounds rounds stopped by themselves within ${STOP_LIMIT}s ($img, $entry)"
[ "$failures" -eq 0 ]
