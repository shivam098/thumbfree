# ThumbFree: plan

Private, on-device dictation for **Android, iPhone, Mac and Windows**. You speak, and clean text appears wherever your
cursor is. It works in English and in Hindi + English (Hinglish) typed in Roman letters, and **it never needs the
internet**: speech is recognised on the device.

ThumbFree is the dictation half of Voice Assistant **without "Hey Jarvis"**: no wake word, no voice commands, no
navigation, no idea board. The microphone is on only while you dictate, so there's no always-on listening and no battery
drain when idle.

**Status (Nov 2026):** Android is built (v0.7.0: voice keyboard and bubble with tap-to-start / tap-to-finish, live
typing, History, first-run guide; Google Play ready). Windows is built (v0.8.0: every section 3 feature, replacing Voice
Dictation). iPhone and Mac are next. [Section 3](#3-feature-reference-built-on-android-copy-to-every-platform) is the
feature reference that every platform implements.

## 1. Positioning: offline is the selling point

Most voice-typing apps send every dictation to a server. Wispr Flow, the best known, is cloud-only on every platform: it
can't transcribe without a connection. ThumbFree never does:

- **Your voice never leaves your device.** There's no account and no cloud, and nobody else hears what you said.
- **Works anywhere.** On a plane, in the metro, with no signal: just as fast.
- **Nothing to trust us with.** No upload code paths. Sharing data is opt-in, kept on the device, and sent only by you.

Use this line first everywhere: the store listing, the first screen of the guide, the README and the About text.
Suggested tagline: *"Type with your voice, not your thumbs. 100% offline."*

## 2. Name

| Option | Notes |
|---|---|
| **ThumbFree** (chosen) | Describes the benefit (no thumbs on a phone, no fingers on a keyboard). Repo `github.com/shivam098/thumbfree`. |
| Zubaan ("tongue / language" in Hindi-Urdu) | Signals the Hinglish strength. Check trademarks (there is a publisher called Zubaan Books). |
| SayWrite, Bolo | **Taken**: existing open-source dictation apps. |

App IDs: `app.thumbfree.android`, `app.thumbfree.ios` (with `app.thumbfree.ios.keyboard`), `app.thumbfree.mac`,
`app.thumbfree.windows`. Before store listings, do a quick trademark search (USPTO, EUIPO, India IP office).

## 3. Feature reference (built on Android, copy to every platform)

Each feature below shipped on Android between v0.1 and v0.7. It came from testing on a Pixel 11 and comparing with
Wispr Flow's onboarding. The rules are written so the iPhone, Mac and Windows apps behave identically. **Shared test
data proves it:** every port must pass `tests/vectors/formatter.tsv` and `tests/vectors/layout.tsv` on the device
itself, not just on a computer, because regex engines differ.

### 3.0 How dictation starts and stops: tap, talk, tap (every platform)

**No push-to-talk anywhere.** Nobody has to keep holding a button or keys. One control toggles dictation:

| State | What you do | What you see |
|---|---|---|
| **Ready** | Tap the mic (Android/iPhone keyboard, bubble), or press the hotkey once (Mac: Fn/Globe or Right Option; Windows: Ctrl+Win) | Mic icon; "Tap to talk" / "Ctrl+Win to dictate" |
| **Listening** | Speak. Tap / press the hotkey again to finish. ✕ or Esc cancels. | The control turns red with a stop square, a ring or meter moves with your voice, an elapsed timer runs ("0:07 · tap when you're done"), and your words appear in the text box as you speak (live typing, 3.10) |
| **Transcribing** | Nothing: wait a moment | The control dims with a spinning arc, and the pill or status reads "Transcribing…" until the text is typed |
| **Done** | — | The typed text is shown briefly, or "Didn't catch anything" / an error |

Rules:

- **Releasing the key does nothing.** Holding by habit just means it's still listening; the next press finishes.
- Recording also finishes by itself at the 10-minute limit, or when the system takes the microphone (3.6). It then goes
  straight to Transcribing.
- Haptic tick (phones) when listening starts and when it stops.
- Shipped: Android v0.5 (keyboard and bubble). Windows (Voice Dictation 0.4, Voice Assistant 0.10): Ctrl+Win
  toggles, and Ctrl+Win+Space is kept as an alias.

### 3.1 Speech engines and models

| Language | Default model | Accurate model (opt-in) | Engine |
|---|---|---|---|
| English | **Live** (new installs): Kroko streaming, 79 MB, types as you speak (3.10). **Standard**: NVIDIA Parakeet TDT 110M int8, 137 MB, punctuates and capitalizes itself | Parakeet TDT 0.6B v2 int8: 661 MB, best on the Open ASR leaderboard | sherpa-onnx (ONNX Runtime) |
| Hindi + English (Roman letters) | Oriserve Whisper-Hindi2Hinglish-Swift q8: 82 MB | Oriserve …-Apex q8: 874 MB (large-v3-turbo) | whisper.cpp |

- **Why two engines:** Parakeet is more accurate and much faster than Whisper for English (on the Android emulator,
  0.27 s for a 2.9 s sentence vs 1.3 s for Whisper base.en; on a Snapdragon X laptop, 17 s of speech in 0.4 s). But it
  has no Hindi. Nothing open beats the Oriserve fine-tunes for Hinglish in Roman letters (researched Nov 2026: Canary,
  Moonshine, IndicConformer and Sarvam don't fit).
- **Models download on demand**, never bundled. They're pinned by SHA-256, and downloads resume after a dropped
  connection. Multi-file models live in their own folder. Models no longer used are deleted on upgrade.
- **Big models (over 300 MB) ask first:** size, the phone's memory against a recommendation (6 GB for 0.6B, 8 GB for
  Apex), and a mobile-data warning. Free space is checked before downloading.
- **Live typing is off for the big models** (too slow to re-run twice a second on a phone): they type when you finish.
- **Efficiency:** one speech thread, at most 4 decoding threads, model unloaded after 3 idle minutes, microphone only
  while dictating.
- **Future (not started):** run Apex on the Qualcomm NPU (about 0.3 s encoder on a Snapdragon 8 Elite, per Qualcomm AI
  Hub) and on the Apple Neural Engine (Core ML).

### 3.2 Text pipeline (same order everywhere)

1. **Trim and split:** energy-based speech detection with 250 ms margins; nothing runs if there was no speech. Long
   dictation is cut at pauses (pieces of at most 30 s for Parakeet, 14 s for Whisper).
2. **Transcribe** each piece. Only the accurate Hinglish model gets the word list as a prompt. The fast one garbles
   with a prompt, and Parakeet's hotwords made results worse in tests.
3. **Drop hallucinations:** "thanks for watching" and similar on audio under 2.5 s. Also drop text that only repeats the
   prompt.
4. **Word-list near-miss fix** (3.4).
5. **Formatter** (C# reference, ported): fillers in English and Hinglish ("um", "basically", "thik hai na", "Haan to
   like…", repeated phrases), spoken punctuation ("comma", "new line", "new paragraph"), "scratch that", the personal
   dictionary and capitalization. It continues the sentence when the text before the cursor doesn't end with `.!?`.
6. **Layout** (3.3, "Smart formatting", on by default).
7. **Field rules:** new lines become spaces in single-line fields; add a space when needed; refuse password fields.
8. **Insert**, only into the field the dictation started in (3.6).

### 3.3 Smart formatting: emails, lists, corrections

All rules are deliberately conservative. `layout.tsv` holds both the cases to change and the ones that must stay as
they are.

**Corrections, in every field.** "Friday at 3 pm… actually 4 pm" → "Friday at 4 pm".

- Markers: *actually, sorry, I mean, no wait, or rather, make it/that*.
- Only these pairs are corrected: time → time, number → time, day → day, and time → number (keeping am/pm: "3 PM,
  actually 4" → "4 PM"). Number → number is corrected only inside a sentence.
- Never inside a decimal: "2.5, actually 3" is left alone.
- After a full stop, correct only if the correction ends the clause: "3 PM. Actually 4 PM?" is corrected; "Friday.
  Actually, tomorrow I'm busy" is not.
- Only weekday names are capitalized.

**Emails, multi-line fields, dictation starting the text.**

- The greeting ("Hi / Hello / Hey / Dear / Good morning…" plus up to 3 name words and `,!:`) goes on its own line,
  then a blank line and the body.
- The sign-off at the end ("Thanks / Thank you / Best regards / Cheers…", optionally followed by a capitalised name)
  goes on its own line, with the name under it ("Thanks,\nBen").
- In email apps (Gmail, Outlook, Proton, Spark…) a greeting or a sign-off alone is enough. Elsewhere it needs both a
  greeting and a signed name, so chat messages aren't turned into emails.

**Lists, multi-line fields.**

- "bullet point … next bullet …" (never "bite the bullet", "bullet train" or "bullet-proof") → "- Item."
- "first … second … third …" at clause starts → "1. Item."
- A list heading followed by items: "Things to do today: order groceries, go on a walk and call mom" → heading line and
  "- Item." lines.
  - The heading starts with a cue: to-do, things to…, shopping, groceries, checklist, agenda, action items, tasks,
    reminders, list, steps, ingredients, packing.
  - The heading has no "is/was/were…".
  - It needs 2+ items after a colon, otherwise 3+. Each item has at most 12 words.
  - A final "X and Y" is split only when both sides have 2+ words, so "bread and butter" stays one item.
- A laid-out list or email starts on a new line if the cursor isn't at the start. A spoken "new line" alone doesn't
  trigger this.

### 3.4 "Words I use often", personal dictionary, profession

- **Words I use often** (one per line, up to 40 entries): exact matches take your spelling ("git hub co-pilot" →
  "GitHub Copilot"). Near misses are corrected only within strict limits:
  - only for entries of 8+ letters;
  - at most 1 edit per 5 letters, and the first letter must match;
  - never changing a number ("Windows 10" stays with "Windows 11" listed);
  - never adding or dropping an ending ("Chromebooks", "Priyanka's");
  - never across punctuation.
- **Personal dictionary:** "what it hears = what to type", for words it mishears completely (`journals = genres`).
- **"What do you do?"** (optional, first-run guide or Settings): adds that field's names and acronyms to the word list.
  It stays on the device. It's the likely reason Wispr asks for your profession, and here it's done with nothing sent
  anywhere.
  - The lists hold proper nouns, brands and acronyms only (GitHub, Kubernetes, JSON; Salesforce; MRI, ECG…).
  - No ordinary words that are also names ("Notion", "Excel"), and no ordinary 8+ letter words ("escalation"). A test
    enforces this.

### 3.5 Works with any keyboard ("the bubble")

Most people won't give up their keyboard. ThumbFree must let them dictate without switching keyboards.

| Platform | How |
|---|---|
| Android (done) | An accessibility service shows a floating mic (`TYPE_ACCESSIBILITY_OVERLAY`, not focusable) when a text box in another app has focus and a keyboard is up. Tap to talk and tap to finish. Drag to move; it snaps to an edge and remembers its place. Long-press hides it until the next text box. Recording runs in a short-lived foreground service of type microphone. Text goes in with `ACTION_SET_TEXT`, else clipboard plus `ACTION_PASTE` (the clip is marked sensitive). |
| iPhone | No floating overlays on iOS. The ThumbFree keyboard is the way in. Extras: a Shortcut / Action Button "Dictate with ThumbFree" that copies the text, and a share-sheet or Control Center control later. See 4.2. |
| Mac | The hotkey is the main way. Optionally, a small floating mic next to the focused text field (position from `kAXBoundsForRangeParameterizedAttribute`), shown only while a text field has focus. |
| Windows | The hotkey plus the existing pill; an optional floating mic near the caret (UI Automation) later. |

The disclosure text, shown before the OS permission screen, is the same everywhere: *"Used only to find the text box
you're typing in and type your dictation there. ThumbFree doesn't read, keep or send anything else on your screen."*

### 3.6 Never speak into the void, never lose a dictation

- **Microphone cut off:** exact digital silence (all-zero samples) for more than 1 s at the start, or 3 s later on,
  means the OS cut the app off the microphone (a call, or another app). Real rooms are never exactly silent. Show a
  warning in the UI and post a high-priority notification. Clear both when sound returns.
- **Recording stopped by the system:** write what was said so far, and say so.
- **Text box gone before the text was ready:** never type it anywhere else (not even into whatever has focus now). Keep
  it in a notification with *Tap to copy*. The notification hides the text on the lock screen.
- **The app didn't accept the text:** same notification.
- **10-minute cap per dictation:** at the limit, the text is written automatically.

### 3.7 First-run guide

A full-screen, skippable guide with a progress bar. Back goes to the previous step. "Show the guide again" is in
Settings. Steps:

1. **Welcome:** the offline pitch (no cloud, works offline, writes like you meant to, English + Hinglish).
2. **Microphone** permission, with what it's used for.
3. **Speech model:** pick English or Hindi + English and download (size shown, Wi-Fi recommended). You can carry on
   while it downloads.
4. **Keyboard:** enable it and switch to it, with live ticks as each step is done.
5. **Practice an email:** say the "Hey Tim… 3 pm… actually 4 pm… Thanks, Ben" sentence into a box that asks for email
   layout, and see it formatted.
6. **Practice a list:** "Things to do today…".
7. **Bubble / any-keyboard access** (platform-specific), with the disclosure. Optional.
8. **Notifications:** "Don't speak into the void". Optional.
9. **What do you do?** Optional.
10. **Your data:** "Keep everything private 🔒" (default) or "Help improve ThumbFree" (3.8).

### 3.8 Data: opt-in, local, shared only by you

- Off by default.
- When on, each keyboard dictation keeps a 16 kHz WAV, the recognised text, the typed text, the model and the app
  version, in app-private storage. The limit is 300 samples or 150 MB, oldest deleted first.
- **Never from password or incognito fields.** On Android the bubble doesn't save samples at all, because another
  app's field can't say whether it's incognito. Use the same rule on any platform where privacy is unknown.
- **Share** makes a zip and opens the system share sheet (email, Drive…). **Delete all** removes everything. There is
  no upload code.
- A server upload would need a backend and a privacy policy. Decide that before adding it, and keep it opt-in.

### 3.9 Keyboard UI (Android reference)

- Big round mic: tap to talk, tap to finish (3.0). While listening: red stop square, voice ring, timer, ✕ cancel key. While transcribing: spinning arc.
- Live words go into the text box (3.10); the status line shows "Listening…" (or the words, with live typing off). Keys: switch keyboard (long-press for the list), EN / HI+EN, insert the last
  dictation again, space, delete (repeats), enter (follows the field's action: send, search, done…).
- Settings button. Idle text: "Tap the mic and speak · offline".

### 3.10 Live typing: words appear as you speak (every platform)

Modelled on Gboard's voice typing on the Pixel 11 (Tensor G6): the words go **into the text box** as you speak,
underlined until you finish, and then the finished text replaces them. ThumbFree does it fully offline on any phone,
with no special chip, by letting people pick a **lighter streaming model**.

**Models (people choose; Settings and the first-run guide):**

| English tier | Model | Size | Live typing | Notes |
|---|---|---|---|---|
| **Live** (default for new installs) | Kroko streaming Zipformer (Banafo, CC BY-SA 4.0) + Edge-Punct-Casing punctuation (Apache-2.0) via sherpa-onnx | 79 MB | **Streaming**: text after every 100 ms of audio | Cased words; a finished phrase (0.8 s pause) gets punctuation. Measured on a Snapdragon X laptop (2 threads): 0.024 × real time, but each word arrives about 1 s after it's said (sherpa-onnx decodes 1.28 s windows), so finishing pads silence until two windows past the end are decoded |
| **Standard** | Parakeet TDT 110M | 137 MB | Re-reads the newest audio every 0.5 s | Slightly more accurate final text |
| **Accurate** | Parakeet TDT 0.6B v2 | 661 MB | No (types when you finish) | Best accuracy, needs 6 GB+ |

- **Hindi + English** has no open streaming model yet: Standard (Swift) re-reads the newest audio every 0.5 s for live
  typing; Accurate (Apex) types when you finish. Revisit when a streaming Hinglish model appears.
- Rejected after benchmarks: FastConformer 80 ms streaming (lowercase, worse words) and Nemotron 0.6B streaming (0.6–1.1
  × real time on a laptop: too slow for phones).
- The punctuation model re-cases words badly ("YOUTUBE"), so only its **punctuation** is merged into the recognizer's
  own words (`LiveText.mergePunctuation`, in the core with tests). If the words differ, the phrase stays unpunctuated.

**Behaviour (the same on every platform):**

1. When listening starts, remember the text before the cursor; spacing and "continue the sentence" are decided once,
   from there.
2. Each update replaces the **live text** in the box: the words so far, cleaned up by the formatter (fillers, spoken
   punctuation, dictionary, word list) but **not laid out**. An email or a list only makes sense once you've finished.
3. On finish, the full pipeline (3.2, with layout) runs and its result **replaces** the live text. With a streaming
   model this is instant: the session already heard everything.
4. Cancel, or nothing heard: the live text is removed. If the final pass fails, the live text is kept and a warning
   shown.
5. **The cursor moved** (the person tapped elsewhere in the box): stop updating, leave what's there, and don't type the
   dictation a second time; the whole text is available from "insert again" (↺).
6. **The text box closed**: the live text is removed, and the dictation goes to the tap-to-copy notification (3.6).
7. While live text is in the box, the keyboard's own keys (space, delete, enter, ↺) wait until the dictation finishes,
   so they can't split or replace it.
8. Never in password fields. A **Live typing** switch (on by default) turns it off: words then show only on the
   keyboard / pill, and the non-streaming models stop re-reading audio (saves battery). It replaces the old "Live
   preview" switch.

**Per platform:**

| Platform | How the live text is shown | Status |
|---|---|---|
| Android keyboard | `InputConnection.setComposingText` (underlined), then `commitText` replaces it; `onUpdateSelection` without a composing span = cursor moved | **Built (v0.6)** |
| Android bubble | Phase 1: the live words show in the bubble's pill; typed when you finish. Phase 2: type finished phrases into the other app at each pause (accessibility `ACTION_SET_TEXT` is all-or-nothing, so no underline) | Phase 1 built |
| iPhone keyboard | `textDocumentProxy.setMarkedText(_:selectedRange:)` (marked = underlined), then `unmarkText()` + `insertText`. The app streams partial text to the keyboard through the App Group + Darwin notification every ~200 ms. Kroko via sherpa-onnx iOS | Design |
| Mac | Type each **finished phrase** at a pause (paste, same-field check, 4.3); the pill shows the phrase still being spoken. Marked text isn't available to other apps | Design |
| Windows | Same as Mac: each finished phrase is typed once the next one has started (text typed into another app can't be taken back, so only final text goes in), the rest in the pill. Kroko streams through the sherpa-onnx C# API; Parakeet (both sizes: a PC keeps up with 0.6B) and Hindi + English Standard re-read the newest audio every 0.5 s | **Built (v0.8)** |

**Tests:** core tests for the punctuation merge and joining; an on-device test feeds a 10 s dictation in 100 ms chunks
and checks that text arrives while speaking, that it keeps up with real time, the word-list fix, punctuation, and abort.
Real-phone checks: Messages, WhatsApp, Gmail, Chrome's address bar (a field that handles composing text differently),
moving the cursor mid-dictation, switching apps mid-dictation.

### 3.11 History: every dictation, kept locally, tap to copy (every platform)

Like Wispr Flow's history, but **only on the device**. It's the safety net for live typing and the bubble: whatever
happened in the other app, the words are here.

- **What's kept:** the final text (not audio), the app it was for and the time. Typed dictations, ones that went to the
  tap-to-copy notification, and ones cut short because the cursor moved. **30 days, at most 500**, newest first. Stored
  as one line per entry (`time<TAB>app<TAB>text`, escaped), with the format and pruning in the shared core (`History`) so
  every platform reads it the same way.
- **Never kept:** password / secure fields. Fields marked private where the platform says so (Android keyboard:
  `IME_FLAG_NO_PERSONALIZED_LEARNING`; iPhone: secure fields never reach a custom keyboard; Windows: browser windows
  whose title says they're private). Where the platform can't tell (the Android bubble, Mac, most Windows windows), only
  passwords are excluded: History is local text the person can clear or turn off, unlike voice samples (3.8), which
  exist to be shared and so are kept only where privacy is known.
- **On by default**, with a **Keep history** switch. Turning it off deletes everything, after a confirmation.
  **Clear history** deletes all of it. Excluded from cloud backups. Uninstalling removes it.
- **Screen:** a list of cards (the text, up to 6 lines; "WhatsApp · 2 minutes ago"). **Tap copies** to the clipboard.
  Hold for Copy / Share / Delete. Search appears once there are more than three. Hidden from the recent-apps preview
  (Android 13+).
- **Where to open it:** Settings › History, and hold ↺ on the keyboard.

| Platform | Storage | Opened from | Status |
|---|---|---|---|
| Android | `noBackupFilesDir/history.txt`, written on its own thread | Settings, hold ↺ on the keyboard | **Built (v0.7)** |
| iPhone | App Group container (the keyboard writes, the app shows), `NSFileProtectionComplete`, excluded from iCloud backup | The app, and a History key on the keyboard (it can insert as well as copy) | Design |
| Mac | `~/Library/Application Support/ThumbFree/history.txt`, excluded from Time Machine | The menu-bar menu ("History…", the last 5 inline: click copies) | Design |
| Windows | `%LOCALAPPDATA%\ThumbFree\history.txt`; also skips windows that say they're private (InPrivate, Firefox private browsing) | The app window's History page, Home, and the tray menu | **Built (v0.8)** |

## 4. Platform designs

### 4.1 Android (built: v0.7.0)

Native Kotlin. sherpa-onnx (Parakeet) and whisper.cpp (two CPU builds: ARMv8.0 and ARMv8.2) through JNI. Views are
built in code, with no UI libraries. APK: 27.6 MB.

- Pieces: voice keyboard (IME), bubble (accessibility service + microphone foreground service), first-run guide,
  Settings, a shared `Dictation` controller, notifications, opt-in samples.
- **Two editions** (Gradle flavors, same signing key):
  - **standard**: no bubble. Installs from GitHub anywhere.
  - **bubble**: adds the bubble.
  - Reason: in India, Play Protect's enhanced fraud protection blocks installing, from a browser or file manager, any
    app that declares an accessibility service. That's what happened to v0.4.0 on the Pixel 11.
  - The bubble edition is meant for the Play Store (not affected) or `adb` installs.
  - Any other platform feature that needs a "sensitive" permission should follow the same pattern.
- Details and build steps: [android.md](android.md). Publishing on Google Play, step by step: [google-play.md](google-play.md).
- Next: real-phone testing of the bubble and the notifications. Then a Play internal test, which needs the
  accessibility declaration form, the Data safety form ("no data collected", since samples never leave the device
  unless the user shares them) and a privacy policy page.

### 4.2 iPhone (new)

iOS doesn't allow a custom keyboard to use the microphone, and has no floating overlays. So the iPhone app is **an app
plus a keyboard extension that work together**, the same way Wispr Flow does it on iPhone.

- **Shape:**
  - The **ThumbFree app** owns the microphone, the models and the speech engines.
  - The **ThumbFree keyboard** (extension) shows the mic button, live preview and the same keys as Android (3.9), and
    inserts text with `textDocumentProxy.insertText`.
  - Both share an **App Group** container for settings and results. The keyboard needs **Full Access** for that; the
    disclosure says why: no network access.
- **Starting a session:**
  - The first time (or after the session has ended), the keyboard's **Start** button opens the app.
  - The app starts an `AVAudioSession` with the background-audio mode, so recording can start later from the keyboard.
    It then asks the user to swipe back (iOS shows "◀ back to app" at the top left).
  - After that, the keyboard's mic starts and stops recording with no app switch: the keyboard signals the app through
    the App Group (plus a Darwin notification), and the app records, transcribes and writes the result back.
  - The orange microphone dot is visible while the session is open.
  - The session ends after a few idle minutes to save battery (configurable, e.g. 5 minutes). It ends immediately when
    the user taps "End session".
- **Models and speed:**
  - Models and engines run **in the app, never in the keyboard**. Keyboard extensions have a tight memory limit (tens of
    MB).
  - English: Parakeet via sherpa-onnx (it has iOS builds). Evaluate a Core ML / Neural Engine build in the spike.
  - Hinglish: whisper.cpp with Metal / Core ML.
- **Field info:**
  - `documentContextBeforeInput` → continue the sentence and "at start".
  - `keyboardType` / `returnKeyType` / `textContentType` → single-line, search and email (`.emailAddress`, plus mail
    apps).
  - Secure text fields automatically switch to the system keyboard. Never record there.
- **Bubble substitute:** Shortcuts / Action Button action "Dictate with ThumbFree" (runs in the app, copies the result,
  can type into a Shortcuts text step). Later, a Control Center control (iOS 18+).
- **Notifications:** local notifications for "can't hear you" and "tap to copy" (3.6).
- **Data sharing:** `UIActivityViewController`.
- **First-run guide** (3.7), with these iPhone steps: add the keyboard (Settings › General › Keyboard › Keyboards › Add
  New Keyboard › ThumbFree), turn on Allow Full Access (explain why), then a first session.
- **App Review:**
  - Guideline 4.4.1: keyboards must provide typing input and a way to switch to the next keyboard, and must remain
    functional without Full Access. So include a basic letters layout, and a clear "Allow Full Access to dictate"
    state.
  - Honest microphone and background-audio usage strings.
  - Privacy "nutrition label": data not collected.
- **Build and test:**
  - Xcode project in `apple/`.
  - GitHub `macos-latest` runners build it and run unit tests and vectors on the iOS Simulator.
  - The microphone, the session handoff and the keyboard need a **real iPhone** and TestFlight.

### 4.3 Mac (new)

- **Shape:**
  - A menu-bar app (`LSUIElement`) plus a settings window.
  - The status pill is a non-activating, click-through `NSPanel` showing the live preview and the warnings (3.6).
- **Hotkey:** press Fn/Globe once to start and once more to finish (3.0), via a `CGEventTap` on `flagsChanged` (needs
  **Input Monitoring**). Alternatives: Right Option or a custom chord. Fn may need "Press 🌐 key to: Do Nothing"; the
  guide explains it. Esc cancels. The pill shows the listening and transcribing states (3.0).
- **Optional floating mic** next to the focused text field (3.5).
- **Audio:** `AVAudioEngine` at 16 kHz mono, plus the Microphone permission.
- **Insertion:**
  - Check that the focused element is still the one from the start of the dictation (`AXUIElement` equality). If not,
    use the "tap to copy" notification (3.6).
  - Then save the pasteboard, write the text marked `org.nspasteboard.TransientType` / `ConcealedType`, send ⌘V with
    `CGEvent` (needs **Accessibility**), and restore the pasteboard.
  - Fallback: `kAXSelectedTextAttribute`.
- **Field info:**
  - The AX role and attributes give single-line vs multi-line and the text before the cursor (`kAXValueAttribute` +
    `kAXSelectedTextRangeAttribute`).
  - Email layout in Mail, Outlook and Spark, and for Gmail in the browser (from the window title or URL when
    available).
- **Safety:** refuse when `IsSecureEventInputEnabled()` or the element is an `AXSecureTextField`.
- **Speed:** Parakeet via sherpa-onnx (macOS builds) and whisper.cpp with Metal / Core ML. Target on Apple Silicon:
  under 0.3 s per phrase in English; Apex usable.
- **Notifications:** `UNUserNotificationCenter`. **Sharing:** `NSSharingServicePicker`.
- **First-run guide** (3.7), with these Mac steps: Microphone, Input Monitoring, Accessibility, then press the hotkey to
  test it.
- **Distribution:**
  - Developer ID signing and notarization (Apple Developer Program; one US$99/year account covers iPhone and Mac).
  - A `.dmg` on GitHub Releases, then a Homebrew cask.
  - Not the Mac App Store: its sandbox blocks global event taps and pasting into other apps.

### 4.4 Windows (from Voice Dictation 0.3)

- Keep: Ctrl+Win to start and to finish (3.0; it was hold-to-talk before 0.4), Ctrl+Win+Space as an alias, Esc cancel,
  Alt+Shift+H language, Alt+Shift+Z paste last. Clipboard
  paste with restore and history/cloud exclusion. Refuse password fields and elevated windows.
- Add from 3:
  - Parakeet for English (sherpa-onnx has Windows x64 and ARM64 builds; measured 17 s of speech in 0.4 s on a
    Snapdragon X CPU).
  - Smart formatting.
  - The word-list near-miss fix.
  - "Never lose a dictation" toasts.
  - The first-run guide.
  - Opt-in samples.
- Also add: the app window (Home, Settings, History, Help) from Voice Assistant 0.9.
- Distribution: zips on GitHub Releases, then winget. Code signing optional.
- **Built in v0.8 (replaces Voice Dictation, whose settings, dictionary and models carry over):**
  - The text box is read once, when dictation starts (UI Automation: password, single- or multi-line including ARIA,
    search and address bars, email apps and webmail, the text before the cursor). Right before each paste or key
    press goes out (after the clipboard holds the text, and again while a modifier key is held), the same window and
    the same element must have the focus; otherwise nothing more is typed and the whole dictation goes to the
    tap-to-copy notification (3.6). A password field found while typing refuses the dictation (nothing kept) only if
    it's the dictation's own box, or in its window when the box couldn't be told apart; a password box elsewhere
    detaches it like any other box.
  - Chromium (Chrome, Edge, Electron and WebView2 apps) builds its pages' accessibility tree only when an assistive
    tool asks; until then the focus is just a pane, with nothing about the text box. So when a dictation starts and a
    pane has the focus in a window with Chromium pages, ThumbFree asks the way screen readers do
    (`ChromiumAccessibility.Wake`). It looks up the window's documents by control type and reads their names through
    UI Automation, since Chromium turns on basic page accessibility for the whole app once a client reads such
    properties of a page. It also sends the "screen reader honeypot" (`WM_GETOBJECT` with object id 1) to the page
    windows (`Chrome_RenderWidgetHostHWND`) with an MSAA name read. Then the focus is read again for up to 1.2 s. The
    app keeps page accessibility on while it runs (a tab hidden for 5 minutes drops its tree until it's shown again).
    Nothing is cached, so a restarted app is asked again; the checks before each typed piece read the focus as it is.
  - Chrome 157 keeps its page windows out of the browser window (in a separate or hidden top-level window), so UI
    Automation reports the focus as the browser's pane even once the page is there. The focused page element is then
    the deepest element with the keyboard focus under that pane, or else in the window (`FocusedInPage`), and its ARIA
    properties are looked up by runtime id. If it can't be found, only the window is compared while typing.
  - Measured on Chrome 157 (fresh profile, window in the background): reading the documents' properties brought the
    page's text boxes within a second. The honeypot with a name read did once (in about 80 ms) and didn't another time,
    when the only page window was parked in Chromium's hidden window. Without any of this, password fields in a fresh
    Chrome weren't detected.
  - Recognition runs on one speech thread; a model loads when a dictation needs it (audio is kept meanwhile) and is
    unloaded after 3 idle minutes.
  - Silero (whisper.cpp's VAD) finds the pauses for Whisper and Parakeet: phrases end at 0.7 s, and long dictation is
    cut into pieces of at most 14 s (Whisper) or 30 s (Parakeet).
  - Samples (3.8): never from web browsers, because a browser can't reliably say whether a window is private (Chrome's
    and Brave's private windows have no marker in their titles, and other browsers translate theirs). Other desktop
    apps and the guide's practice boxes are fine.
  - Settings apply without a restart, except the hotkeys, the microphone, threads, snippets and new-line keys.
  - `--self-test` runs scripts through the real host: scripted phrases typed live or at the end, and recordings
    played as if spoken (tests/DictationLab).

## 5. Architecture: one behaviour, native apps, shared test data

**Decision (Nov 2026): native apps per platform.**

- A keyboard has to open instantly and stay small, which rules out shipping a .NET runtime on phones.
- Behaviour is kept identical by **shared test vectors**, not shared binaries:
  - `formatter.tsv`, generated from the C# reference formatter;
  - `layout.tsv`, emails, lists and corrections.
- Every port runs both on the real device in CI.

| Platform | App | Core logic | Speech |
|---|---|---|---|
| Android | Kotlin | `android/core` (Kotlin, JVM-testable) | sherpa-onnx AAR + whisper.cpp via JNI |
| iPhone + Mac | Swift / SwiftUI + AppKit | **Recommended: reuse `android/core` through Kotlin Multiplatform** as an XCFramework (one source for Android, iPhone and Mac). Fallback: a Swift port held to the same vectors. | sherpa-onnx xcframework + whisper.cpp (Metal / Core ML) |
| Windows | C# / WPF | `windows/src/ThumbFree.Core` (the C# reference, held to the same vectors) | sherpa-onnx (C# API) for Kroko and Parakeet, Whisper.net for Hindi + English |

**Kotlin Multiplatform for Apple** means one fix to the filler or layout rules reaches Android, iPhone and Mac at once.

- The core is already pure Kotlin with no Android calls, so it's a good fit.
- Risk: the Kotlin/Native regex engine differs from Java's and Android's. That's exactly what the vectors catch: run
  them on the iOS Simulator in CI.
- If the spike shows problems (framework size, regex gaps), port to Swift instead.

Repo layout:

```
thumbfree/
  android/            Kotlin: core/, whisper/, whisper-v82/, app/   (built)
  apple/              ThumbFree.xcodeproj: iOS app + keyboard extension, macOS menu-bar app, shared Swift package
  windows/            later: moved from shivam098/windows-dictation
  tests/vectors/      formatter.tsv, layout.tsv   (all platforms, on device)
  tests/audio/        speech clips for on-device engine tests
  third_party/        whisper.cpp (submodule)
  docs/               PLAN.md, android.md, apple.md (to write)
```

## 6. Phases

| Phase | Goal | Done when |
|---|---|---|
| **Android 0.1–0.4** ✅ | Keyboard, Parakeet + Whisper, word list, emails/lists/corrections, bubble, notifications, guide, opt-in samples | Released v0.4.0; CI: core tests, APK build, emulator tests (formatter + layout vectors, both engines, every guide screen) |
| **Android 0.5** ✅ | Tap to start / tap to finish with listening and transcribing states (3.0); standard + bubble editions; Android 16 target and a Play app bundle | Released v0.5.0 |
| **Android 0.6** ✅ | Live typing (3.10): Kroko streaming English tier, composing text in the keyboard, faster live Hinglish | Released v0.6.0 |
| **Android 0.7** ✅ | History (3.11): every dictation kept locally for 30 days, tap to copy | Released v0.7.0 |
| **Android 0.8** | Real-phone fixes from the Pixel 11 test (bubble in WhatsApp/Gmail/Chrome, call interruption), then Google Play: internal test, then a closed test (12+ testers for 14 days, required for new personal accounts), then production | Bubble works in the top 10 apps; Play listing approved |
| **Apple spike** (week 1) | (a) The `android/core` KMP framework passing both vector files on the iOS Simulator and macOS; (b) iPhone: keyboard Start → app session → swipe back → mic in the keyboard → text inserted, on a real iPhone; (c) Parakeet 110M speed on iPhone and Mac | All three work, or the fallback (Swift port, different session design) is chosen |
| **iPhone 0.x** (weeks 2–5) | Keyboard + app session, models, guide, notifications, Shortcuts action, opt-in samples | TestFlight with the acceptance list (Messages, WhatsApp, Mail, Notes, Safari) |
| **Mac 0.x** (weeks 4–7) | Menu bar, hotkey, pill, paste with the same-field check, guide, optional floating mic | Notarized `.dmg`; acceptance on a real Mac (Notes, Mail, Safari, Chrome, Slack, VS Code, Terminal) |
| **Windows 0.8** ✅ | Every section 3 feature on Windows, replacing Voice Dictation: Kroko live typing (phrases typed at pauses, the rest in the pill), Parakeet, smart formatting, the word list, History, the app window and first-run guide, notifications, opt-in samples | Released v0.8.0; CI: Core, Speech and App tests, x64 and ARM64 builds; self-test labs on a Chrome test page |
| **Windows 1.0** | Real-app labs (Notepad, Word, Teams, WhatsApp, Outlook, VS Code); archive `windows-dictation`; winget | Real-app labs pass; winget |
| **Later** | Auto language pick (English word overlap ≥ 0.5 in 16/17 English clips, 0/6 Hinglish); learn from corrections; NPU / Neural Engine for Apex | |

One product version across platforms; release assets per platform.

## 7. Testing

- **Vectors on every device:** `formatter.tsv` and `layout.tsv` run in CI on the real target (Android emulator, iOS
  Simulator, macOS, Windows). Any rule change updates the vectors first.
- **Engine tests with recorded speech** (`tests/audio`): final pass, live preview, abort, word list, a 10 s dictation
  through formatting. Timings are printed so regressions show.
- **Screen smoke tests:** every guide step and Settings open without crashing.
- **Real-device acceptance checklist** per platform:
  - dictate (tap / tap: listening and transcribing states visible), cancel, Hinglish;
  - email and list layout in a mail app and a notes app;
  - "actually" corrections;
  - password refusal;
  - a call during dictation (warning + notification);
  - the text box closing mid-dictation (tap-to-copy notification, nothing typed elsewhere);
  - the bubble / hotkey in the top apps;
  - offline (flight mode);
  - nothing recorded when not dictating;
  - samples only when opted in, and never from private fields.
- **Independent code review** of every change before release (on Android it caught 7 real bugs in v0.4, including the
  bubble typing into the wrong app).

## 8. Privacy and safety (same rules everywhere)

- The microphone is on only while dictating (on iPhone, during the visible session). No wake word.
- Speech is recognised on the device. The internet is used only to download pinned, SHA-256-checked models.
- Audio is kept only in memory, unless the user opts in to keep samples on the device. Nothing is ever uploaded by the
  app.
- Never type into password / secure fields, or into a field other than the one the dictation started in.
- Clipboard: restored after pasting where the OS allows, and marked sensitive / transient.
- Accessibility and Full Access are used only for the one stated purpose, with the disclosure shown first.
- Logs record states and timings only, never words.

## 9. Costs and accounts

| Item | Cost | Needed for |
|---|---|---|
| Apple Developer Program | US$99/year | iPhone (TestFlight, App Store) and Mac (Developer ID, notarization): one account covers both |
| Google Play developer | US$25 once | Play listing |
| Windows code signing (optional) | ~US$10/month (Azure Trusted Signing) | Removing SmartScreen warnings |
| Devices | — | A real iPhone and Mac (keyboard session, permissions); the Pixel 11 for Android |

## 10. Risks

| Risk | Mitigation |
|---|---|
| iOS keyboard session feels clunky (app switch to start) | Long idle timeout (user choice), clear "session on" state in the keyboard, Shortcuts action as an alternative; spike on a real iPhone first |
| App Review rejects the keyboard (4.4.1) or the background audio | Basic letters layout, next-keyboard key, honest usage strings, audio only during a visible session |
| Kotlin/Native regex differs from Java/ICU | Vectors on the iOS Simulator in CI; Swift port as the fallback |
| Play policy for the accessibility bubble | `isAccessibilityTool=false`, disclosure before settings, the declaration form; the keyboard works without it |
| Bubble text insertion fails in some apps (web views, custom editors) | Paste fallback, then the tap-to-copy notification; list the top apps in acceptance tests |
| Live text misbehaves in some editors (composing text in web views, custom editors) | The Live typing switch; the cursor-moved rule (3.10); test the top apps; fall back to typing when you finish per app if needed |
| Kroko licence (CC BY-SA 4.0) | Attribution in About and the README; the model files are unmodified and downloaded separately from the app (the app's own licence is unaffected) |
| Big models on phones (memory, heat) | Opt-in with memory warnings; no live preview; unload when idle; NPU / Neural Engine later |
| Hinglish model licences | Confirm the Oriserve licences allow redistribution before store listings (Parakeet: CC BY 4.0 with attribution; whisper.cpp: MIT) |
| Platforms drifting apart | Section 3 is the spec; the vectors are the contract; one release checklist |

## 11. Decisions needed from you

1. **Apple core:** reuse the Kotlin core through Kotlin Multiplatform (recommended), or port to Swift?
2. **iPhone session timeout:** how long a dictation session stays ready after the last use (suggest 5 minutes; 15 and
   "until I end it" as options)?
3. **Accounts and devices:** the Apple Developer account (US$99/yr, covers iPhone and Mac), plus a real iPhone and Mac
   to test on.
4. **Data sharing destination:** keep "Share via the share sheet" only, or set up a server with a privacy policy for
   opt-in uploads?
5. **Order after the Android phone test:** iPhone first or Mac first?
