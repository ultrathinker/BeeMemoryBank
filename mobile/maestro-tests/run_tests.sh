#!/bin/bash
# Maestro test runner with cleanup and device wake-up

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

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
    ~/.maestro/bin/maestro test "$SCRIPT_DIR/" || RESULT=$?
else
    for test in "$@"; do
        echo "--- Running: $test ---"
        ~/.maestro/bin/maestro test "$SCRIPT_DIR/$test" || RESULT=$?
    done
fi

echo "=== Restoring screen timeout and turning off screen ==="
adb shell settings put system screen_off_timeout "$ORIG_TIMEOUT"
# adb shell input keyevent KEYCODE_SLEEP

exit $RESULT
