#!/bin/bash
# Maestro test runner with cleanup and device wake-up

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# The flows log in with ${BMB_TEST_PASSWORD} and expect the fixture data from
# stand/seed-fixtures.sh (see stand/README.md).
if [ -z "${BMB_TEST_PASSWORD:-}" ]; then
    echo "Set BMB_TEST_PASSWORD to the test node's password first." >&2
    exit 2
fi

# The Maestro installer puts the CLI in ~/.maestro/bin; the zip unpacks to ~/.maestro/maestro/bin.
MAESTRO="$HOME/.maestro/bin/maestro"
[ -x "$MAESTRO" ] || MAESTRO="$HOME/.maestro/maestro/bin/maestro"
export MAESTRO_CLI_NO_ANALYTICS=1
export MAESTRO_CLI_ANALYSIS_NOTIFICATION_DISABLED=true

echo "=== Cleaning up stale Maestro sessions ==="
pkill -f maestro.cli 2>/dev/null || true
adb forward --remove-all 2>/dev/null || true
sleep 2

echo "=== Waking up device, setting 30 min screen timeout ==="
ORIG_TIMEOUT=$(adb shell settings get system screen_off_timeout)
adb shell settings put system screen_off_timeout 1800000
adb shell input keyevent KEYCODE_WAKEUP
sleep 1
adb shell input swipe 540 1800 540 800 300
sleep 1

echo "=== Running Maestro tests ==="
RESULT=0
if [ $# -eq 0 ]; then
    "$MAESTRO" test "$SCRIPT_DIR/" -e BMB_TEST_PASSWORD="$BMB_TEST_PASSWORD" || RESULT=$?
else
    for test in "$@"; do
        echo "--- Running: $test ---"
        "$MAESTRO" test "$SCRIPT_DIR/$test" -e BMB_TEST_PASSWORD="$BMB_TEST_PASSWORD" || RESULT=$?
    done
fi

echo "=== Restoring screen timeout and turning off screen ==="
adb shell settings put system screen_off_timeout "$ORIG_TIMEOUT"
# adb shell input keyevent KEYCODE_SLEEP

exit $RESULT
