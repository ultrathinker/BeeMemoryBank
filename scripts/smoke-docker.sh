#!/usr/bin/env bash
# Start-up test of a Bee Memory Bank image (BMB-88). The image workflow runs it on each architecture before anything is
# tagged, and again on what it published; it is also the quick check for an image built by hand.
#
#   scripts/smoke-docker.sh <image-ref> <full|blind> <expected-version>
#   e.g. scripts/smoke-docker.sh bmb-full:dev full "$(cat VERSION)"
#
# Starts the image with nothing published (no host port is used, so runs cannot collide), waits for the image's own
# HEALTHCHECK, asks the node inside the container for its version, and for the full node also checks the first-run page and
# the baked-in search model. Then stops the container gracefully and removes it on success, failure, interruption, and
# startup failure alike (a failure prints the last log lines first). With BMB_SMOKE_KEEP=1 the stopped container is kept
# instead and named in the last line, for `docker logs`.
set -euo pipefail

if [ "$#" -ne 3 ]; then
    echo "usage: $0 <image-ref> <full|blind> <expected-version>" >&2
    exit 2
fi
img="$1"; kind="$2"; want="$3"
case "$kind" in
    full)  api=http://127.0.0.1:5300 ;;   # the Api inside the container
    blind) api=http://127.0.0.1:5612 ;;   # the blind node's plain loopback listener, never the TLS one
    *) echo "kind must be full or blind" >&2; exit 2 ;;
esac

fail() { echo "::error::smoke $kind $img: $*" >&2; exit 1; }

# Only BMB_SMOKE_KEEP=1 keeps the container; anything else removes it.
keep="${BMB_SMOKE_KEEP:-0}"

name="bmb-smoke-${kind}-$$"
container_created=0

container_present() {
    docker inspect "$name" >/dev/null 2>&1
}

on_exit() {
    rc=$?
    # Do not let a second Ctrl-C interrupt the cleanup itself. Keep the EXIT trap
    # installed until stop and remove have both had their chance to run.
    trap - INT TERM
    if [ "$rc" -ne 0 ] && container_present; then
        echo "--- last log lines of $name ---" >&2
        docker logs --tail 80 "$name" >&2 2>&1 || true
    fi
    if [ "$container_created" -eq 1 ] || container_present; then
        docker stop -t 30 "$name" >/dev/null 2>&1 || true
        if [ "$keep" != 1 ]; then
            docker rm "$name" >/dev/null 2>&1 || docker rm -f "$name" >/dev/null 2>&1 || true
        fi
    fi
    trap - EXIT
    exit "$rc"
}

# A stale name is not ours to remove. The PID-based name should make this only
# a diagnostic, but checking it avoids cleaning up somebody else's container on
# the rare name collision.
if container_present; then
    fail "container name already exists: $name"
fi
trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
docker run -d --name "$name" --label bmb.smoke=1 "$img" >/dev/null
container_created=1

status=starting
for _ in $(seq 1 60); do                                   # up to 5 minutes
    status="$(docker inspect -f '{{.State.Health.Status}}' "$name")"
    [ "$status" = healthy ] && break
    [ "$(docker inspect -f '{{.State.Running}}' "$name")" = true ] || fail "the container exited"
    sleep 5
done
[ "$status" = healthy ] || fail "not healthy after 5 minutes ($status)"

got="$(docker exec "$name" curl -fsS "$api/api/version" | sed -n 's/.*"version":"\([^"]*\)".*/\1/p')"
[ "$got" = "$want" ] || fail "/api/version says '$got', expected '$want'"

if [ "$kind" = full ]; then
    # A fresh node: the first-run page answers on the Web port without signing in.
    code="$(docker exec "$name" curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5301/Setup)"
    [ "$code" = 200 ] || fail "/Setup answered $code"

    # The model is baked in, and it is the file the Dockerfile verified (EmbeddingModelWiring.BundledModelSha256).
    dockerfile="$(dirname "$0")/../Dockerfile"
    expected="$(grep -o 'checksum=sha256:[0-9a-f]\{64\}' "$dockerfile" | head -n 1 | cut -d: -f2)"
    [ -n "$expected" ] || fail "no model checksum found in $dockerfile"
    sum="$(docker exec "$name" sha256sum /app/api/model.onnx | cut -d' ' -f1)"
    [ "$sum" = "$expected" ] || fail "/app/api/model.onnx has SHA-256 $sum, expected $expected"
    if docker exec "$name" test -e /app/cli/model.onnx; then fail "the CLI carries a second copy of the model"; fi

    # Only the Web port is declared: 5300 must not be offered by `docker run -P`.
    exposed="$(docker image inspect -f '{{json .Config.ExposedPorts}}' "$img")"
    [ "$exposed" = '{"5301/tcp":{}}' ] || fail "the image exposes $exposed, expected only 5301/tcp"
fi

# docker stop must reach every process: the full node's entrypoint passes SIGTERM to the Api and the Web front, and a clean
# stop of both exits 0. Reported, not failed: a slow shutdown is not a reason to hold a release.
docker stop -t 30 "$name" >/dev/null
exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$name")"
if [ "$kind" = full ] && [ "$exit_code" != 0 ]; then
    echo "::warning::smoke $kind $img: docker stop ended with exit code $exit_code, expected 0 (a clean stop of both hosts)"
fi

if [ "$keep" = 1 ]; then
    echo "smoke ok: $kind $img $got (stopped container: $name, exit code $exit_code)"
else
    docker rm "$name" >/dev/null || fail "could not remove the container $name"
    echo "smoke ok: $kind $img $got (container removed, exit code $exit_code)"
fi

trap - INT TERM
trap - EXIT
exit 0
