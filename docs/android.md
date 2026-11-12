# ThumbFree for Android: how it's built

ThumbFree on Android is a **voice keyboard** (an input method). Switch to it in any app, hold the mic and talk; when you
let go, cleaned-up text is typed where the cursor is. Speech recognition runs on the phone with
[whisper.cpp](https://github.com/ggml-org/whisper.cpp); nothing is uploaded.

## Layout

```
android/
  core/          Pure Kotlin (JVM), no Android code: formatter, filler rules, spoken punctuation, personal
                 dictionary, speech detection and segmentation, live preview, field rules, model catalog.
                 Unit tests + the shared formatter vectors run on any computer.
  whisper/       whisper.cpp built with the NDK (CMake), a small JNI bridge, and WhisperModel (load, transcribe,
                 abort). Generic arm64 (ARMv8.0) and x86_64 builds.
  whisper-v82/   The same engine built for ARMv8.2 (dot product + fp16): about 2× faster on phones from ~2018 on.
                 Picked at run time from /proc/cpuinfo, with the generic build as fallback.
  app/           The keyboard (VoiceKeyboard), the setup screen (SetupActivity), the recorder, model downloads and
                 settings. Views are built in code: no AppCompat, Compose or other UI libraries, so the APK stays
                 small and the keyboard opens fast.
third_party/whisper.cpp   git submodule, pinned to v1.9.5
tests/vectors/formatter.tsv   inputs and expected outputs generated from the C# reference formatter
tests/audio/                  short speech clips for the on-device tests
```

## How a dictation flows

1. **Press the mic.** `Recorder` opens the microphone (`AudioRecord`, `VOICE_RECOGNITION`, 16 kHz mono float) on a
   high-priority thread. The model loads in the background on the single "speech" thread if it isn't loaded yet.
2. **While you talk** (optional live preview), every ~0.9 s `LivePreview` re-transcribes the newest audio with a small
   audio context sized to the clip, so each update is quick. Audio older than 10 s is transcribed once, cut at a pause
   and kept, so updates never grow with the length of the dictation.
3. **Release.** Any preview still running is aborted (whisper.cpp abort callback). The recording is trimmed to the
   speech (energy-based detection with 250 ms margins), split at pauses into pieces of at most 14 s, and each piece is
   transcribed with the full-quality settings (audio context 768, temperature fallback).
4. **Clean up.** Common Whisper hallucinations on very short audio are dropped ("thanks for watching"). Then
   `DictationFormatter` removes fillers (English and Hinglish: "um", "basically", "thik hai na"…), applies spoken
   punctuation ("comma", "new line"), your personal dictionary and capitalization. It continues the sentence when the
   text before the cursor doesn't end with a full stop.
5. **Insert** with `InputConnection.commitText`, adding a space if needed. In single-line fields new lines become
   spaces. Password fields are refused.
6. After 3 idle minutes the model is unloaded to give the memory back.

## Efficiency choices

| Choice | Why |
|---|---|
| Native Kotlin + JNI, no .NET/Flutter runtime | Small APK, keyboard opens instantly |
| Static whisper.cpp with `-O3`, no OpenMP, two CPU builds | Fast on every phone without shipping a slow lowest-common-denominator |
| Uncompressed, 16 KB-aligned native libraries | Loaded straight from the APK; ready for Android 15's 16 KB pages |
| Single speech thread, at most 4 decoding threads | No contention between preview and final pass; leaves cores for the app you're typing in |
| Microphone only while dictating, no service | Zero battery use when you're not talking |
| Quantized (q8) models, downloaded on demand | 78 MB for English; nothing bundled in the APK |

## Building

You need JDK 17 and the Android SDK + NDK (Android Studio installs both).

```sh
git clone --recurse-submodules https://github.com/shivam098/thumbfree
cd thumbfree/android
./gradlew :core:test            # core logic and formatter vectors (no SDK needed: THUMBFREE_CORE_ONLY=1)
./gradlew :app:assembleDebug    # app/build/outputs/apk/debug/ThumbFree-0.1.0-debug.apk
```

Without an Android SDK, Gradle only includes the `core` module, so the core tests run on any machine.

### On-device tests

```sh
./gradlew :app:assembleDebug :app:assembleDebugAndroidTest
bash ../scripts/android-device-test.sh app/build/outputs/apk path/to/ggml-base.en-q8_0.bin
```

They check that Android's regex engine gives exactly the reference formatter's output, that the native engine loads,
and that a recorded sentence is transcribed (final pass, live preview and abort). CI runs them on an emulator for every
push.

## Releases

Pushing a `v*` tag builds a signed arm64 APK and publishes it on GitHub Releases with its SHA-256. The workflow needs
these repository secrets: `THUMBFREE_KEYSTORE_B64` (base64 of a PKCS#12 keystore), `THUMBFREE_KEYSTORE_PASSWORD`,
`THUMBFREE_KEY_ALIAS` and `THUMBFREE_KEY_PASSWORD`. Keep a backup of the keystore: updates must be signed with the
same key.
