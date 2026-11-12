# ThumbFree

**Type with your voice, not your thumbs.** ThumbFree turns speech into clean, punctuated text in any app, and it works
offline: speech is recognised on your device and never uploaded.

- Hold the mic, talk, let go: the text appears where the cursor is.
- Removes filler words ("um", "you know", "basically", "thik hai na"…) and repeated phrases.
- Spoken punctuation: "comma", "full stop", "question mark", "new line", "new paragraph".
- English on NVIDIA Parakeet TDT, which punctuates and capitalizes by itself (110M model by default, 0.6B for the best
  accuracy), and Hindi + English (Hinglish) written in Roman letters on Whisper (the accurate Hinglish model is 874 MB
  and best on phones with 8 GB+ memory).
- A personal dictionary for words it gets wrong (`journals = genres`), and a "words I use often" list that fixes near
  misses of names and jargon ("Kubirnets" → "Kubernetes").

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
3. Pick a language. The speech model downloads once (137 MB for English) and then works offline.

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

English recognition: NVIDIA Parakeet TDT models ([110M](https://huggingface.co/nvidia/parakeet-tdt_ctc-110m),
[0.6B v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2); CC BY 4.0) running on
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (Apache-2.0) and ONNX Runtime (MIT).
Hindi + English: [whisper.cpp](https://github.com/ggml-org/whisper.cpp) (MIT) running quantized conversions of
Oriserve's Whisper Hindi-to-Hinglish models (Apache-2.0).
