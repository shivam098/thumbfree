# ThumbFree for Android: how it's built

ThumbFree on Android is a **voice keyboard** (an input method). Switch to it in any app, hold the mic and talk; when you
let go, cleaned-up text is typed where the cursor is. Speech recognition runs on the phone; nothing is uploaded.

## Speech engines and models

| Language | Model | Download | Engine |
|---|---|---|---|
| English | NVIDIA Parakeet TDT 110M, int8 (default) | 137 MB | sherpa-onnx + ONNX Runtime |
| English, accurate | NVIDIA Parakeet TDT 0.6B v2, int8 (best on the Open ASR leaderboard; no live preview) | 661 MB | sherpa-onnx + ONNX Runtime |
| Hindi + English | Oriserve Whisper-Hindi2Hinglish-Swift (Whisper base), q8 | 82 MB | whisper.cpp |
| Hindi + English, accurate | Oriserve Whisper-Hindi2Hinglish-Apex (large-v3-turbo), q8 (no live preview) | 874 MB | whisper.cpp |

Why two engines: Parakeet is more accurate than Whisper models of similar size for English, much faster (no fixed 30 s
window to pad to) and adds punctuation and capitals itself, but it has no Hindi. Nothing open beats the Oriserve
Whisper fine-tunes for Hindi + English typed in Roman letters. On a Snapdragon X laptop CPU, Parakeet 110M transcribes
17 s of speech in 0.4 s and 0.6B in 1.0 s.

## Layout

```
android/
  core/          Pure Kotlin (JVM), no Android code: formatter, filler rules, spoken punctuation, personal
                 dictionary and word list, speech detection and segmentation, live preview, field rules, model
                 catalog. Unit tests + the shared formatter vectors run on any computer.
  whisper/       whisper.cpp built with the NDK (CMake), a small JNI bridge, and WhisperModel (load, transcribe,
                 abort). Generic arm64 (ARMv8.0) and x86_64 builds.
  whisper-v82/   The same engine built for ARMv8.2 (dot product + fp16): about 2× faster on phones from ~2018 on.
                 Picked at run time from /proc/cpuinfo, with the generic build as fallback.
  app/           The keyboard (VoiceKeyboard), the setup screen (SetupActivity), the recognizers (Whisper and
                 Parakeet behind one interface), the recorder, model downloads and settings. Views are built in code:
                 no AppCompat, Compose or other UI libraries, so the keyboard opens fast. sherpa-onnx comes from its
                 official AAR (2.0.1, ONNX Runtime linked in), downloaded by Gradle and pinned by SHA-256.
third_party/whisper.cpp   git submodule, pinned to v1.9.5
tests/vectors/formatter.tsv   inputs and expected outputs generated from the C# reference formatter
tests/audio/                  short speech clips for the on-device tests
```

## How a dictation flows

1. **Press the mic.** `Recorder` opens the microphone (`AudioRecord`, `VOICE_RECOGNITION`, 16 kHz mono float) on a
   high-priority thread. The model loads in the background on the single "speech" thread if it isn't loaded yet.
2. **While you talk** (optional live preview), every ~0.9 s `LivePreview` re-transcribes the newest audio (Whisper
   with a small audio context sized to the clip). Audio older than 10 s is transcribed once, cut at a pause and kept,
   so updates never grow with the length of the dictation.
3. **Release.** Any preview still running is aborted. The recording is trimmed to the speech (energy-based detection
   with 250 ms margins), split at pauses into pieces (at most 30 s for Parakeet, 14 s for Whisper), and each piece is
   transcribed with the full-quality settings. The accurate Hinglish model also gets your "words I use often" list as a
   prompt; the fast one doesn't (it was measured to garble the output), and Parakeet can't take one (sherpa-onnx
   hotwords made its output worse in tests).
4. **Clean up.** Common Whisper hallucinations on very short audio are dropped ("thanks for watching"), and so is a
   transcript that only repeats the prompt. Near misses of your listed words are corrected ("Kubirnets" →
   "Kubernetes", "git hub co-pilot" → "GitHub Copilot"; misspellings only for entries of 8+ letters). Then
   `DictationFormatter` removes fillers (English and Hinglish: "um", "basically", "thik hai na"…), applies spoken
   punctuation ("comma", "new line"), your personal dictionary and capitalization. It continues the sentence when the
   text before the cursor doesn't end with a full stop.
5. **Insert** with `InputConnection.commitText`, adding a space if needed. In single-line fields new lines become
   spaces. Password fields are refused.
6. After 3 idle minutes the model is unloaded to give the memory back.

## Efficiency choices

| Choice | Why |
|---|---|
| Native Kotlin + JNI, no .NET/Flutter runtime | Keyboard opens instantly |
| Parakeet for English | More accurate and several times faster than Whisper at the same size; punctuation built in |
| Static whisper.cpp with `-O3`, no OpenMP, two CPU builds | Fast on every phone without shipping a slow lowest-common-denominator |
| Uncompressed, 16 KB-aligned native libraries | Loaded straight from the APK; ready for Android 15's 16 KB pages |
| Single speech thread, at most 4 decoding threads | No contention between preview and final pass; leaves cores for the app you're typing in |
| Microphone only while dictating, no service | Zero battery use when you're not talking |
| Quantized (int8/q8) models, downloaded on demand | 137 MB for English; nothing bundled in the APK; models no longer used are deleted |
| Accurate models are opt-in | Parakeet 0.6B (661 MB) and Hinglish Apex (874 MB) skip the live preview, ask before downloading (memory, mobile data) and are unloaded after 3 idle minutes |

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
bash ../scripts/android-device-test.sh app/build/outputs/apk path/to/models   # ggml-base.en-q8_0.bin, parakeet-tdt-110m/
```

They check that Android's regex engine gives exactly the reference formatter's output, that the native engine loads,
and that recorded sentences are transcribed by both engines (final pass, live preview, abort, word list). CI runs them
on an emulator for every push. There, Parakeet 110M transcribes a 2.9 s sentence in about 0.3 s and Whisper base.en in
about 1.3 s.

## Releases

Pushing a `v*` tag builds a signed arm64 APK and publishes it on GitHub Releases with its SHA-256. The workflow needs
these repository secrets: `THUMBFREE_KEYSTORE_B64` (base64 of a PKCS#12 keystore), `THUMBFREE_KEYSTORE_PASSWORD`,
`THUMBFREE_KEY_ALIAS` and `THUMBFREE_KEY_PASSWORD`. Keep a backup of the keystore: updates must be signed with the
same key.
