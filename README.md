# ThumbFree

**Type with your voice, not your thumbs.** ThumbFree turns speech into clean, punctuated text in any app, and it works
offline: speech is recognised on your device and never uploaded.

- Hold the mic, talk, let go: the text appears where the cursor is.
- Removes filler words ("um", "you know", "basically", "thik hai na"…) and repeated phrases.
- Spoken punctuation: "comma", "full stop", "question mark", "new line", "new paragraph".
- English, and Hindi + English (Hinglish) written in Roman letters, each with a fast model and an optional accurate one
  (the accurate Hinglish model is 874 MB and best on phones with 8 GB+ memory).
- A personal dictionary for words it gets wrong (`journals = genres`), and a "words I use often" list that helps
  Whisper spell names and jargon your way.

| Platform | Status |
|---|---|
| Android | **In progress**: voice keyboard, v0.1 |
| Windows | Available today as [Voice Dictation](https://github.com/shivam098/windows-dictation); moving here next |
| macOS | Planned |

The full plan is in [docs/PLAN.md](docs/PLAN.md).

## Android

### Install

1. Download `ThumbFree-x.y.z.apk` from [Releases](https://github.com/shivam098/thumbfree/releases) and open it. Android
   asks you to allow installs from your browser or file manager the first time.
2. Open **ThumbFree** and follow the three steps: allow the microphone, turn on the ThumbFree keyboard, switch to it.
3. Pick a language. The speech model downloads once (78 MB for English) and then works offline.

Requires Android 8.0 or newer on a 64-bit phone.

### Use

- **Hold** the mic and speak; **let go** to write.
- **Tap** the mic for hands-free; tap again when you're done.
- **Slide up** while holding to cancel.
- **EN / HI+EN** switches language; **⌨** goes back to your usual keyboard (long-press for the list).
- **↺** types your last dictation again.

### Privacy

The microphone is on only while you're dictating. Audio is kept in memory, turned into text on the phone and then
discarded. It is never saved or sent anywhere. The app uses the internet only to download speech models, and checks
each one against a pinned SHA-256. Settings are not included in cloud backups.

### Build it yourself

See [docs/android.md](docs/android.md) for the architecture, how a dictation flows, and how to build and test.

```sh
git clone --recurse-submodules https://github.com/shivam098/thumbfree
cd thumbfree/android && ./gradlew :app:assembleDebug
```

## Credits

Speech recognition by [whisper.cpp](https://github.com/ggml-org/whisper.cpp) (MIT) running OpenAI's Whisper models.
The Hinglish model is a quantized conversion of Oriserve's Whisper Hindi-to-Hinglish model.
