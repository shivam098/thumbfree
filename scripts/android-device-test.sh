#!/usr/bin/env bash
# Installs the debug app and its test APK on a connected device or emulator, copies a speech model to it and runs the
# on-device tests. Usage: scripts/android-device-test.sh <apk-dir> [model-file]
set -euo pipefail

apks="$1"
model="${2:-}"

app=$(find "$apks" -path '*debug*' -name '*-debug.apk' ! -name '*androidTest*' | head -n1)
tests=$(find "$apks" -name '*-debug-androidTest.apk' | head -n1)
echo "app:   $app"
echo "tests: $tests"

adb wait-for-device
adb install -r -g "$app"
adb install -r "$tests"

if [ -n "$model" ]; then
  adb shell mkdir -p /data/local/tmp/thumbfree
  adb push "$model" /data/local/tmp/thumbfree/ggml-base.en-q8_0.bin
fi

# am instrument exits 0 even when tests fail, so check the summary line.
out=$(adb shell am instrument -w -r app.thumbfree.android.test/androidx.test.runner.AndroidJUnitRunner | tee /dev/stderr)
adb logcat -d -s System.out:I ThumbFree:* | grep -i thumbfree || true
echo "$out" | grep -q "^OK (" || { echo "On-device tests failed"; exit 1; }
