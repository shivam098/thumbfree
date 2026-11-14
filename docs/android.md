# ThumbFree for Android: how it's built

ThumbFree on Android is a **voice keyboard** (an input method). Switch to it in any app, tap the mic and talk, and tap
it again: cleaned-up text is typed where the cursor is. Speech recognition runs on the phone; nothing is uploaded.

## Speech engines and models

| Language | Model | Download | Engine |
|---|---|---|---|
| English, live (default for new installs) | Kroko streaming Zipformer (Banafo, CC BY-SA 4.0) + Edge-Punct-Casing punctuation (Apache-2.0): types as you speak | 79 MB | sherpa-onnx + ONNX Runtime |
| English, standard | NVIDIA Parakeet TDT 110M, int8 | 137 MB | sherpa-onnx + ONNX Runtime |
| English, accurate | NVIDIA Parakeet TDT 0.6B v2, int8 (best on the Open ASR leaderboard; types when you finish) | 661 MB | sherpa-onnx + ONNX Runtime |
| Hindi + English | Oriserve Whisper-Hindi2Hinglish-Swift (Whisper base), q8 | 82 MB | whisper.cpp |
| Hindi + English, accurate | Oriserve Whisper-Hindi2Hinglish-Apex (large-v3-turbo), q8 (types when you finish) | 874 MB | whisper.cpp |

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
  app/           The keyboard (VoiceKeyboard), the bubble (BubbleService + MicService), the first-run guide
                 (OnboardingActivity), settings (SetupActivity), the shared dictation controller (Dictation), the
                 recognizers (Whisper, Parakeet and the Kroko streaming model behind one interface), the recorder, notifications, opt-in samples
                 and model downloads. Views are built in code: no AppCompat, Compose or other UI libraries, so the
                 keyboard opens fast. sherpa-onnx comes from its official AAR (2.0.1, ONNX Runtime linked in),
                 downloaded by Gradle and pinned by SHA-256.
third_party/whisper.cpp   git submodule, pinned to v1.9.5
tests/vectors/formatter.tsv   inputs and expected outputs generated from the C# reference formatter
tests/vectors/layout.tsv      emails, lists and corrections (formatter + layout), checked on the JVM and on a device
tests/audio/                  short speech clips for the on-device tests
```

## How a dictation flows

1. **Tap the mic.** It turns red with a stop square, a ring follows your voice level and a timer runs. `Recorder` opens the microphone (`AudioRecord`, `VOICE_RECOGNITION`, 16 kHz mono float) on a
   high-priority thread. The model loads in the background on the single "speech" thread if it isn't loaded yet.
2. **While you talk** (Live typing, on by default) the words go into the text box as underlined composing text
   (`setComposingText`), spaced from the text that was before the cursor when you started. They are cleaned up by the
   formatter but not laid out yet.
   - **Live model (Kroko):** `Recorder` hands every 100 ms of audio to a `StreamingRecognizer.Session` on the speech
     thread, which returns the text so far. A phrase ends after a 0.8 s pause and then gets punctuation from a small
     model; only its punctuation is kept, the recognizer's own words and capitals stay (`LiveText.mergePunctuation`).
   - **Other models:** every ~0.5 s (0.9 s with Live typing off, where the words show only on the keyboard)
     `LivePreview` re-transcribes the newest audio (Whisper with a small audio context sized to the clip). Audio older
     than 10 s is transcribed once, cut at a pause and kept, so updates never grow with the length of the dictation.
     The accurate models skip this.
   - Tapping elsewhere in the box ends the live text: it stays as it is and the dictation isn't typed a second time
     (↺ inserts it). The keyboard's own keys wait until you finish.
3. **Tap again** (or ✕ to cancel, which removes the live text). With the Live model the session already has
   everything, so the final text is ready at once. Otherwise the mic shows a spinning "Transcribing…" state until the
   text is written. Any preview still running is aborted. The recording is trimmed to the speech (energy-based detection
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
5. **Lay out** (Smart formatting, on by default): `Layout` keeps only a spoken correction of a time or day ("3 pm…
   actually 4 pm" → "4 pm"), and in multi-line fields turns an email into greeting / body / sign-off paragraphs (in
   email apps, or anywhere when there's both a greeting and a signed-off name) and a spoken list into bullets ("bullet
   point …", "first … second … third …", or a list heading such as "Things to do today:" followed by three or more
   items). It is deliberately conservative; `tests/vectors/layout.tsv` holds the cases that must stay unchanged too.
6. **Insert** with `InputConnection.commitText` (which replaces the live text), adding a space if needed. In single-line fields new lines become
   spaces. Password fields are refused.
7. After 3 idle minutes the model is unloaded to give the memory back.

## The bubble

**Editions.** The bubble ships only in the `bubble` product flavor. Its manifest (`app/src/bubble/AndroidManifest.xml`)
adds the accessibility service and the microphone foreground service. The `standard` flavor declares neither:
Google Play Protect's enhanced fraud protection (India, since 2024) blocks installing any app that declares an
accessibility service from an "internet sideloading source" (browser, messaging app, file manager). Apps installed from
the Play Store or with `adb` aren't affected. `BuildConfig.HAS_BUBBLE` hides the bubble's guide step and settings in the
standard edition. An on-device test checks that the standard APK has no accessibility service. Releases attach both
APKs, signed with the same key.

`BubbleService` is an accessibility service (declared `isAccessibilityTool=false`, with the disclosure shown before
Android's settings page). It listens only for focus and window changes. When an editable field in another app has
focus and a keyboard is on screen, it adds a floating mic (`TYPE_ACCESSIBILITY_OVERLAY`, not focusable, so the text
box keeps focus). It stays hidden while the ThumbFree keyboard is in use.

Tapping it starts `MicService`, a foreground service of type `microphone`: Android lets an app that isn't on screen
record only from one, and allows starting it because the bubble is a visible window. Then the same `Dictation` the
keyboard uses records and transcribes. The text goes into the field that had focus when you tapped:

1. `ACTION_SET_TEXT` with the selection replaced, then `ACTION_SET_SELECTION` (native text boxes; the clipboard is
   left alone).
2. Otherwise the clipboard plus `ACTION_PASTE` (web pages, rich editors). The clip is marked sensitive and the
   previous clip is put back when Android lets the app read it.
3. If neither works or the box has gone, the text is kept in a notification with **Tap to copy**.

## Never speaking into the void

`Dictation` watches the recording. Exact digital silence for more than a second at the start, or three seconds later
on, means Android has cut the app off the microphone (a call or another app took it). Real rooms are never exactly
silent. When that happens, the keyboard or bubble shows a warning and a high-priority notification is posted. If
recording stops by itself, what was said so far is still written. If the text box goes away while a dictation is
being written, the result is never typed elsewhere: it's kept in a notification to copy.

## History

Every dictation's final text goes to `HistoryStore` (on by default, Settings › History or hold ↺ on the keyboard):
one line per entry (`time`, app package, text, escaped; format and 30-day / 500-entry pruning in core `History`) in
`noBackupFilesDir/history.txt`, written on its own thread. Typed, undelivered (notification) and cursor-moved
dictations are all kept, so nothing is lost. Not kept: password fields, and keyboard fields marked incognito
(`IME_FLAG_NO_PERSONALIZED_LEARNING`). The bubble keeps everything but passwords: it can't see incognito, and History
is local text the user can clear or turn off (unlike samples, which exist to be shared). `HistoryActivity` lists them
(newest first, search once there are more than three): tap copies, hold offers Copy / Share / Delete; it's hidden
from the recent-apps preview on Android 13+. Turning History off deletes it.

## First-run guide and data choice

`OnboardingActivity` covers why ThumbFree is different (offline), the microphone, the model download, the keyboard,
practice rounds for an email and a list, the bubble, notifications, an optional "What do you do?" (adds that
field's names and acronyms to "Words I use often", on the phone only) and the data choice. "Keep everything private"
is the default. "Help improve ThumbFree" keeps samples from the keyboard in app storage: 16 kHz WAV, recognised text,
typed text, model and app version. It never keeps them from the bubble, which can't tell whether a field is
incognito. Storage is capped at 300 samples or 150 MB. **Share** zips them and hands the zip to the share sheet
through `SampleProvider`; there is no upload code.

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
