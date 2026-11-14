#!/usr/bin/env bash
# Installs each edition's debug app and test APK on a connected device or emulator, copies speech models to it and runs
# the on-device tests. Usage: scripts/android-device-test.sh <apk-dir> [models-dir]
#   apk-dir is app/build/outputs/apk (or a copy of it); editions: standard, bubble.
#   models-dir may hold ggml-base.en-q8_0.bin (Whisper tests) and parakeet-tdt-110m/ (Parakeet tests).
set -euo pipefail

apks="$1"
models="${2:-}"

adb wait-for-device
if [ -n "$models" ]; then
  adb shell mkdir -p /data/local/tmp/thumbfree
  adb push "$models/." /data/local/tmp/thumbfree/
fi

failed=0
for edition in standard bubble; do
  app=$(find "$apks" -path "*/$edition/debug/*" -name "*-$edition-debug.apk" | head -n1)
  tests=$(find "$apks" -name "*-$edition-debug-androidTest.apk" | head -n1)
  echo "== $edition"
  echo "app:   $app"
  echo "tests: $tests"
  adb uninstall app.thumbfree.android > /dev/null 2>&1 || true
  adb install -r -g "$app"
  adb install -r "$tests"
  # am instrument exits 0 even when tests fail, so check the summary line.
  adb shell am instrument -w -r app.thumbfree.android.test/androidx.test.runner.AndroidJUnitRunner > "instrument-$edition.txt"
  cat "instrument-$edition.txt"
  grep -q "^OK (" "instrument-$edition.txt" || { echo "On-device tests failed ($edition)"; failed=1; }
done
adb logcat -d -s System.out:I ThumbFree:* | grep -i thumbfree || true
exit $failed