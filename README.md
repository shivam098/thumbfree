# ThumbFree

**Type with your voice, not your thumbs. 100% offline.**

ThumbFree turns speech into clean, punctuated text in any app, and your voice never leaves your phone. There's no
account and no cloud: speech is recognised on the device, so it works on a plane, in the metro, or with no signal, and
nobody else ever hears what you said. Most voice-typing apps (Wispr Flow, for example) send every dictation to their
servers; ThumbFree never does.

- **Tap, talk, tap**: tap the mic and speak while you watch your voice level and words appear; tap again, it shows
  "Transcribing…", and the text appears where the cursor is. Nothing to hold.
- **Writes like you meant to**: removes filler words ("um", "you know", "basically", "thik hai na"…) and repeats, adds
  punctuation, and understands spoken punctuation ("comma", "new line", "new paragraph").
- **Emails and lists, laid out for you**: "Hey Tim, … Thanks, Ben" gets its greeting and sign-off on their own lines;
  "Things to do today: order groceries, go on a walk and call mom" becomes a bulleted list; "Friday at 3 pm… actually
  4 pm" keeps only your correction.
- **Works with any keyboard**: use the ThumbFree voice keyboard, or keep Gboard and tap the floating **bubble**.
- **Never lose a dictation**: you're told straight away if a call or another app takes the microphone, and a
  dictation that can't be typed is kept in a notification for you to copy.
- **History**: your dictations from the last 30 days, on the phone only. Tap one to copy it (Settings › History, or hold
  ↺ on the keyboard). Clear it or turn it off any time.
- **Live typing**: words appear in the text box as you speak, like Gboard on a Pixel, on any phone and offline. Pick
  the **Live** English model (79 MB); the finished text replaces them when you tap the mic again.
- **English and Hinglish**: English on a Kroko streaming model (Live) or NVIDIA Parakeet TDT (110M Standard, 0.6B for the best accuracy); Hindi +
  English typed in Roman letters on Whisper (the accurate Hinglish model is 874 MB, for phones with 8 GB+ memory).
- **Your words**: a personal dictionary (`journals = genres`) and a "words I use often" list that fixes near misses
  ("Kubirnets" → "Kubernetes"), pre-filled from your line of work if you like.

| Platform | Status |
|---|---|
| Android | **Preview**: voice keyboard and bubble |
| Windows | Available today as [Voice Dictation](https://github.com/shivam098/windows-dictation); moving here next |
| macOS | Planned |

The full plan is in [docs/PLAN.md](docs/PLAN.md).

## Android

### Install

1. Download `ThumbFree-x.y.z.apk` from [Releases](https://github.com/shivam098/thumbfree/releases) and open it. Android
   asks you to allow installs from your browser or file manager the first time.
2. Open **ThumbFree**. A short guide walks you through the microphone, the speech model (downloads once, 79 MB for
   live English, then works offline), the keyboard, and a practice email and list.

Requires Android 8.0 or newer on a 64-bit phone.

**Two editions.** Each release has `ThumbFree-x.y.z.apk` (everything except the bubble) and
`ThumbFree-x.y.z-with-bubble.apk`. The bubble uses an accessibility service, and in some countries (India) Google Play
Protect blocks installing such an app from a browser or file manager ("App blocked to protect your device"). Install
the bubble edition with `adb install ThumbFree-x.y.z-with-bubble.apk` from a computer, or get it from the Play Store
once it's listed. Both are signed with the same key, so you can switch without losing settings or models.

### Use

The keyboard:

- **Tap** the mic and speak. It turns red, a ring moves with your voice, a timer runs and your words appear as you
  talk. **Tap** it again when you're done: it shows "Transcribing…" and then types the text.
- **✕** cancels while recording.
- **EN / HI+EN** switches language; **⌨** goes back to your usual keyboard (long-press for the list).
- **↺** types your last dictation again.

The bubble (any keyboard, bubble edition): it appears beside your keyboard whenever you type. **Tap** it, talk,
**tap** again. Drag it to move it; long-press to hide it until the next text box. Turn it on in the guide or in
Settings.

### Privacy

- Speech is recognised on the phone. The app uses the internet only to download speech models, each checked against a
  pinned SHA-256.
- The microphone is on only while you dictate. Audio is kept in memory and discarded once it's text.
- The bubble uses Android's accessibility access only to find the text box you're typing in and type your dictation
  there. It doesn't read, keep or send anything else on screen.
- **Help improve ThumbFree** is off unless you choose it. When on, your dictations (audio, recognised text, typed text)
  from the ThumbFree keyboard are kept on the phone (never from password or incognito fields, never from the bubble), and are sent only if you tap **Share** and pick
  where they go. **Delete all** removes them.
- **History** keeps the text (not the audio) of your last 30 days of dictations on the phone so you can copy them
  again. Never from password fields, nor incognito fields with the keyboard. Clear it or turn it off in History.
- Settings and History are not included in cloud backups.

### Google Play

How to publish (account, listing text, data safety, accessibility declaration, testing tracks): [docs/google-play.md](docs/google-play.md).
Privacy policy: [docs/privacy.md](docs/privacy.md).

### Build it yourself

See [docs/android.md](docs/android.md) for the architecture, how a dictation flows, and how to build and test.

```sh
git clone --recurse-submodules https://github.com/shivam098/thumbfree
cd thumbfree/android && ./gradlew :app:assembleDebug
```

## Credits

English recognition: NVIDIA Parakeet TDT models ([110M](https://huggingface.co/nvidia/parakeet-tdt_ctc-110m),
[0.6B v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2); CC BY 4.0) running on
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (Apache-2.0) and ONNX Runtime (MIT).
Live English: the Kroko streaming model by [Banafo](https://huggingface.co/Banafo/Kroko-ASR) (community model,
CC BY-SA 4.0) and the [Edge-Punct-Casing](https://huggingface.co/frankyoujian/Edge-Punct-Casing)
punctuation model (Apache-2.0), both unmodified and downloaded separately from the app.
Hindi + English: [whisper.cpp](https://github.com/ggml-org/whisper.cpp) (MIT) running quantized conversions of
Oriserve's Whisper Hindi-to-Hinglish models (Apache-2.0).
