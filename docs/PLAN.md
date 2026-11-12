# ThumbFree: plan

Private, on-device dictation for **Windows, macOS and Android**. You hold a key and speak, and clean text appears
wherever your cursor is. It works well in English and in Hindi + English (Hinglish) typed in Roman letters.

ThumbFree is the dictation half of Voice Assistant **without "Hey Jarvis"**: no wake word, no voice commands, no
navigation, no idea board. Because the microphone is on only while you dictate, there is no always-on listening and no
battery drain when idle.

## 1. Name

| Option | Notes |
|---|---|
| **ThumbFree** (recommended) | Your idea. It describes the benefit (no thumbs on a phone, no fingers on a keyboard). I found no dictation product with this name, and `shivam098/thumbfree` is free. |
| Zubaan ("tongue / language" in Hindi-Urdu) | Signals the Hinglish strength. Check trademarks first (there is a publisher called Zubaan Books). |
| SayWrite, Bolo | **Taken**: both are existing open-source dictation apps for Mac/Windows. Don't use them. |

Repo: `github.com/shivam098/thumbfree`. Product name: **ThumbFree**. App IDs: `app.thumbfree.windows`,
`app.thumbfree.mac`, `app.thumbfree.android`.

Before publishing to the Microsoft, Apple or Google stores, do a quick trademark search (USPTO, EUIPO, India IP office)
and check the store listings.

## 2. What ThumbFree does (v1, all three platforms)

| Feature | Notes |
|---|---|
| Push-to-talk | Hold a key and speak; release to insert. Windows: Ctrl+Win. Mac: Fn/Globe (configurable). Android: mic key on the ThumbFree keyboard. |
| Hands-free | Toggle on, speak at length, toggle off to insert. Esc / swipe-down cancels. |
| Live preview | Words appear while you speak. Windows/Mac: status pill. Android: keyboard panel. |
| Clean text | Removes filler words in English and Hinglish ("um", "basically", "thik hai na", "Haan to like…", repeated phrases). Spoken punctuation ("comma", "new line"). Inline "scratch that". Personal dictionary ("git hub = GitHub"). Snippets. |
| Languages | English (small.en). Hindi + English fast (Swift). Hindi + English accurate (Apex), where the device can run it. |
| Vocabulary hint | "Words I use often" helps the accurate model spell names and jargon. |
| Paste last | Re-insert the last dictation (Windows Alt+Shift+Z, Mac ⌥⇧Z, Android long-press mic). |
| Settings window | All settings in a UI, not JSON. Microphone choice. Model downloads with progress. |
| History (opt-in, off by default) | The last 50 dictations, kept on the device only, so text that failed to insert can be recovered. |
| Privacy | Speech never leaves the device. Logs record timings only, never words. No accounts, no analytics. |

**Left out on purpose** (these stay in Voice Assistant): the wake word, voice commands ("open Chrome", "send"),
clicking by name, tabs, spoken editing ("delete the last two words"), and the idea board.

## 3. Starting point (what already exists)

`shivam098/windows-dictation` (Voice Dictation 0.3.0) is already about 70% of ThumbFree for Windows:

| Piece | Status | Lines |
|---|---|---|
| `VoiceDictation.Core`: formatter (fillers, punctuation, scratch that, replacements, snippets), inserter, live preview, speech segments | Done, platform-neutral, 77 tests | ~720 |
| `VoiceDictation.Speech`: Whisper + Silero wrappers, WAV | Done, platform-neutral | ~230 |
| Windows app: Ctrl+Win hook, clipboard paste with history/cloud exclusion, mic capture, pill, tray, model downloader with SHA-256 checks, self-test lab | Done | ~2,600 |
| App window and Settings page | Missing (Voice Assistant has them: port HomePage/SettingsPage/Theme/Motion) | |
| macOS, Android | Not started. Mapping in Voice Assistant `docs/specs/platform-porting.md` | |

Models are already published with pinned SHA-256s: small.en q8 (252 MB), Hinglish Swift q8 (78 MB) and Hinglish Apex
q8 (834 MB, release `models-hinglish-apex-1`).

## 4. Architecture

**Recommendation: one shared C# core, one native-feeling .NET app per platform.**

```
thumbfree/
  src/ThumbFree.Core/        net10.0   formatter, fillers, punctuation, dictionary, snippets, segmenter,
                                       preview scheduling, settings model, history store (no OS calls)
  src/ThumbFree.Speech/      net10.0   Whisper.net + Silero, model catalog, downloader, SHA-256 checks
  apps/windows/              net10.0-windows   WPF: hook, paste, pill, tray, window
  apps/macos/                net10.0-macos     AppKit: menu bar, CGEventTap, paste, NSPanel pill, window
  apps/android/              net10.0-android   InputMethodService (voice keyboard) + settings activity
  tests/ThumbFree.Core.Tests/                  xUnit; runs on Windows, macOS, Linux CI
  tests/vectors/*.json                         the same cases as data (input → expected text)
  tests/labs/                                  real-app labs per platform (DictationLab, etc.)
  docs/specs/                                  product, dictation, platforms, privacy, acceptance tests
  .github/workflows/                           ci.yml (matrix), release.yml, publish-model.yml
```

**Why:** the hardest-won logic is the formatter and the Hinglish filler rules, which change often. One implementation
with one test suite means a fix reaches all three apps at once. Whisper.net (already in use) supports Windows, macOS
(Metal/Core ML) and Android, and .NET can build an Android keyboard and a macOS menu-bar app.

**Fallback:** native Kotlin (Android) or Swift (Mac), calling whisper.cpp directly, with the core ported and checked
against `tests/vectors/*.json`. Use it only if a week-1 spike fails, for example if the .NET keyboard is too large or too
slow to open.

> **Decision for Android (Nov 2026): native Kotlin + whisper.cpp through JNI.** A keyboard must open instantly and stay
> small, so the Android app skips the .NET runtime. The core (formatter, filler rules, segmentation, live preview) is
> ported to Kotlin in `android/core` and must match the C# reference on every row of `tests/vectors/formatter.tsv`, both
> on the JVM and on a device (Android's ICU regex engine). See [android.md](android.md).

| | Shared .NET (recommended) | Native Swift + Kotlin |
|---|---|---|
| Core logic | Written once, 77+ tests reused | Written 3 times; vectors keep them in step |
| App size | Android +15–25 MB runtime (trimmed) | Smallest |
| OS integration | Full through bindings, with some P/Invoke on Mac | Most natural, best docs and samples |
| Speed to ship | Fastest: Windows is mostly done | Slowest |

## 5. Platform designs

### Windows (from Voice Dictation)

- Keep: Ctrl+Win hold, Ctrl+Win+Space hands-free, Esc cancel, Alt+Shift+H language, Alt+Shift+Z paste last.
- Add: an app window (Home, Settings, History, Help), ported from Voice Assistant 0.9. Also smooth UI, first-run
  onboarding (microphone check and model download) and start-at-sign-in (tray only).
- Insertion: clipboard paste with restore and history/cloud exclusion (done). Refuse password fields and elevated
  windows (done).
- Distribution: zip (x64/ARM64) on GitHub Releases, then **winget**. Optional later: MSIX and a code-signing
  certificate, so SmartScreen stops warning.

### macOS (new)

- **Shape:** a menu-bar app (`LSUIElement`). The status pill is a non-activating, click-through `NSPanel`. The settings
  window is AppKit.
- **Hotkey:** hold Fn/Globe via a `CGEventTap` on `flagsChanged`, which needs **Input Monitoring** permission.
  Alternatives in Settings: Right Option, or a custom chord. Fn may need "Press 🌐 key to: Do Nothing" in System
  Settings; onboarding explains this.
- **Audio:** `AVAudioEngine` at 16 kHz mono, plus the **Microphone** permission (`NSMicrophoneUsageDescription`).
- **Insertion:**
  - Normally: save the pasteboard, write the text marked `org.nspasteboard.TransientType` and `ConcealedType`, send
    ⌘V with `CGEvent` (needs **Accessibility** permission), then restore the pasteboard.
  - Fallback: `AXUIElementSetAttributeValue` on the focused element's `kAXSelectedTextAttribute`.
- **Safety:** refuse when `IsSecureEventInputEnabled()` is on or the focused element is an `AXSecureTextField`
  (password fields).
- **Speed:** Whisper.net with Metal/Core ML. Target on Apple Silicon: under 0.4 s per phrase with small.en; Apex usable.
- **Distribution:**
  - Developer ID signing plus **notarization**. Needs an Apple Developer account, US$99 a year. Without it, users must
    right-click › Open past Gatekeeper; acceptable for early testers only.
  - A `.dmg` on GitHub Releases, then a **Homebrew cask**. The Mac App Store is not an option: its sandbox doesn't allow
    global event taps and pasting into other apps.
- **Build and test:**
  - GitHub `macos-latest` runners build the app.
  - Microphone, permissions and pasting need a **real Mac**.

### Android (new)

- **Shape:** a **voice keyboard (IME)**, the cleanest and Play-policy-safe way to put text into any app. The user
  switches to ThumbFree, or taps its mic from the keyboard switcher, then holds the big mic key and speaks. On release
  the text is committed with `commitText`, and it switches back to the previous keyboard automatically
  (`switchToPreviousInputMethod`), like Google voice typing.
- **Not planned:**
  - An accessibility service: Play restricts it to accessibility uses.
  - A floating bubble: needs the overlay permission and is awkward to use.
- **Audio:** `AudioRecord` at 16 kHz with **RECORD_AUDIO**, granted once in the companion settings activity. Recording
  happens only while the key is held, so there is no foreground service and no background microphone use.
- **Keyboard panel:** live preview, a language toggle (EN / HI+EN), backspace, enter, a "paste last" chip, and a
  "switch keyboard" key.
- **Models:**
  - Default English: base.en q8 (78 MB, already published and pinned).
  - Hinglish: Swift (78 MB).
  - small.en offered only on devices with 6 GB RAM or more.
  - Apex not offered on phones.
  - Downloads over Wi-Fi by default, with SHA-256 checks.
- **Targets:**
  - Mid-range phone (Snapdragon 7-series): about 1.5 s for a 5 s phrase with base.en.
  - Live preview with tiny.en or base.en every ~1 s.
  - Battery: zero cost when not dictating.
- **Safety:** refuse in password fields (the field's `EditorInfo.inputType` variation is `TYPE_TEXT_VARIATION_PASSWORD`,
  `TYPE_TEXT_VARIATION_VISIBLE_PASSWORD`, `TYPE_TEXT_VARIATION_WEB_PASSWORD` or `TYPE_NUMBER_VARIATION_PASSWORD`). Never
  learn from fields flagged `IME_FLAG_NO_PERSONALIZED_LEARNING` (incognito).
- **Distribution:**
  - First: signed APK on GitHub Releases.
  - Then: **Google Play**. Needs a US$25 one-time developer account, a privacy policy page, and a Data safety form
    ("no data collected").
  - F-Droid is possible later.

## 6. Phases

Rough effort for one developer with Copilot. Each phase ends with something usable and released.

| Phase | Goal | Done when |
|---|---|---|
| **0. Repo and spikes** (week 1) | Create `shivam098/thumbfree` with Voice Dictation's history, rename namespaces to ThumbFree, set up CI. Two throw-away spikes: (a) a .NET Android IME that records 3 s, runs base.en and commits text on a real phone; (b) a .NET macOS menu-bar app where Fn-hold records and ⌘V pastes. | CI green on Windows, macOS and Linux. Both spikes work on a real device, or the fallback (native) is chosen for that platform. |
| **1. Shared core** (week 2) | Move platform-neutral code into `ThumbFree.Core`/`Speech` (model catalog, downloader, settings model, opt-in history store). Export `tests/vectors/*.json` from the existing tests. | Core tests pass on all three CI runners. |
| **2. Windows 1.0** (weeks 2–3) | Parity with Voice Dictation 0.3, plus the app window, Settings, History, onboarding and smooth UI. Archive `windows-dictation` with a pointer to ThumbFree; existing users keep their settings (migrated from `%APPDATA%\VoiceDictation\settings.json`; models reused from `%LOCALAPPDATA%\VoiceDictation\models`). | Real-app labs pass (Notepad, Chrome, Teams, VS Code, terminal). Release `v1.0.0-windows`, zips plus winget. |
| **3. macOS 0.x → 1.0** (weeks 3–6) | Menu-bar app, hotkey, audio, paste, pill, live preview, Settings, onboarding for the three permissions, Metal. | Acceptance tests on a real Mac (Notes, Safari, Chrome, Slack, VS Code, Terminal). Notarized `.dmg`, then a Homebrew cask. |
| **4. Android 0.x → 1.0** (weeks 5–8) | Voice keyboard, settings activity, model manager, per-device model choice. | Acceptance tests on 2 phones (one mid-range): WhatsApp, Gmail, Chrome, Notes. APK on GitHub, then a Play internal test, then production. |
| **5. Polish** (ongoing) | Learn from corrections (add to the dictionary), more Hinglish rules, auto language pick (measured signal: English text from small.en and Swift agree in 16 of 17 clips, Hinglish 0 of 6), optional iOS keyboard later. | |

Version scheme: one product version across platforms (`1.0.0`), with release assets per platform. Tags
`v1.0.0`, `v1.1.0`, and so on. A platform that isn't ready yet just doesn't attach an asset.

## 7. Testing

- **Unit:** the core suite (formatter, fillers, Hinglish rules, segmenter, preview, history) on every CI run, on all
  three OSes. The JSON vectors make any native port prove the same behaviour.
- **Speech benchmarks:** the existing SpeechBench and Hinglish bench, run per platform and model (latency, CPU per
  phrase, word error). Results go in `docs/BENCHMARKS.md`.
- **Real-app labs:**
  - Windows: the existing DictationLab self-test.
  - Mac: an XCUITest-free script that drives TextEdit and checks its text through AX.
  - Android: an instrumented test with a sample app hosting an `EditText`, driving the IME's commit path with recorded
    audio.
- **Acceptance checklist** per platform in `docs/specs/acceptance-tests.md`:
  - dictate, cancel, hands-free, Hinglish;
  - password refusal;
  - clipboard restored;
  - nothing recorded when not dictating;
  - works offline.

## 8. Privacy and safety (same rules everywhere)

- The microphone is on only while dictating. No wake word and no background listening.
- Audio and text are never uploaded and never written to disk. History is opt-in and local.
- Logs record states and timings only.
- Never type into password fields or secure input. On Windows, never into elevated windows.
- The clipboard is restored after pasting and excluded from clipboard history and sync where the OS allows.
- Models are downloaded only from pinned URLs and verified with SHA-256 before loading.

## 9. Costs and accounts

| Item | Cost | Needed for |
|---|---|---|
| Apple Developer Program | US$99/year | Signing and notarizing the Mac app (otherwise Gatekeeper warns) |
| Google Play developer | US$25 once | Play Store listing |
| Windows code signing (optional) | ~US$100–400/year (or Azure Trusted Signing, ~US$10/month) | Removing SmartScreen warnings |
| A Mac and an Android phone for testing | — | Permissions, microphone, real-app tests |

## 10. Risks

| Risk | Mitigation |
|---|---|
| .NET Android keyboard too large or slow to open | Week-1 spike; fallback to a Kotlin IME + whisper.cpp with the vectors |
| Fn key capture on Mac varies by keyboard and settings | Configurable hotkey; onboarding check that shows the key was detected |
| Phone speed for small.en / Apex | base.en + Swift by default; larger models only on capable devices |
| Hinglish model licences | Confirm the Oriserve model licences allow redistribution before store listings (whisper.cpp and OpenAI Whisper are MIT) |
| Store review (Play Data safety, Mac notarization) | No data collection; honest permission text; signing set up early |
| Two products (Voice Assistant + ThumbFree) drifting apart | ThumbFree.Core becomes the source of the formatter; Voice Assistant later consumes it as a package or submodule |

## 11. Decisions needed from you

1. **Name:** ThumbFree, or another?
2. **Approach:** shared .NET (recommended) or native Swift/Kotlin?
3. **Old repo:** archive `windows-dictation` and move its users to ThumbFree?
4. **Accounts:** will you get the Apple Developer (US$99/yr) and Google Play (US$25) accounts, and do you have a Mac
   and an Android phone to test on?
5. **History:** keep it opt-in (off by default)?
