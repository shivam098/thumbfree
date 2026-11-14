# ThumbFree

**Type with your voice, not your thumbs. 100% offline.**

ThumbFree turns speech into clean, punctuated text in any app, and your voice never leaves your phone. There's no
account and no cloud: speech is recognised on the device, so it works on a plane, in the metro, or with no signal, and
nobody else ever hears what you said. Most voice-typing apps (Wispr Flow, for example) send every dictation to their
servers; ThumbFree never does.

- **Hold the mic, talk, let go**: the text appears where the cursor is.
- **Writes like you meant to**: removes filler words ("um", "you know", "basically", "thik hai na"…) and repeats, adds
  punctuation, and understands spoken punctuation ("comma", "new line", "new paragraph").
- **Emails and lists, laid out for you**: "Hey Tim, … Thanks, Ben" gets its greeting and sign-off on their own lines;
  "Things to do today: order groceries, go on a walk and call mom" becomes a bulleted list; "Friday at 3 pm… actually
  4 pm" keeps only your correction.
- **Works with any keyboard**: use the ThumbFree voice keyboard, or keep Gboard and tap the floating **bubble**.
- **Never lose a dictation**: you're told straight away if a call or another app takes the microphone, and a
  dictation that can't be typed is kept in a notification for you to copy.
- **English and Hinglish**: English on NVIDIA Parakeet TDT (110M by default, 0.6B for the best accuracy); Hindi +
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
2. Open **ThumbFree**. A short guide walks you through the microphone, the speech model (downloads once, 137 MB for
   English, then works offline), the keyboard, a practice email and list, and the optional bubble.

Requires Android 8.0 or newer on a 64-bit phone.

### Use

The keyboard:

- **Hold** the mic and speak; **let go** to write. **Tap** for hands-free; tap again when you're done.
- **Slide up** while holding to cancel.
- **EN / HI+EN** switches language; **⌨** goes back to your usual keyboard (long-press for the list).
- **↺** types your last dictation again.

The bubble (any keyboard): it appears beside your keyboard whenever you type. **Tap** it, talk, **tap** again. Drag
it to move it; long-press to hide it until the next text box. Turn it on in the guide or in Settings.

### Privacy

- Speech is recognised on the phone. The app uses the internet only to download speech models, each checked against a
  pinned SHA-256.
- The microphone is on only while you dictate. Audio is kept in memory and discarded once it's text.
- The bubble uses Android's accessibility access only to find the text box you're typing in and type your dictation
  there. It doesn't read, keep or send anything else on screen.
- **Help improve ThumbFree** is off unless you choose it. When on, your dictations (audio, recognised text, typed text)
  from the ThumbFree keyboard are kept on the phone (never from password or incognito fields, never from the bubble), and are sent only if you tap **Share** and pick
  where they go. **Delete all** removes them.
- Settings are not included in cloud backups.

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
