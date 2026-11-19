# SpeechBench

Measures ThumbFree's speech engines on this PC: how long each model takes to load, its real-time factor (RTF:
processing time ÷ audio length) on the shared test clips (`tests/audio`), the text it produces, and for live typing
(Kroko) how soon a word shows after it was spoken. It uses the same `ThumbFree.Speech` code as the app
(`ModelStore`, `Recognizers.Load`, `StreamingRecognizer.Session`).

```powershell
# Download the English models (Kroko live 79 MB, Parakeet 110M 137 MB, Parakeet 0.6B 661 MB) into
# %LOCALAPPDATA%\ThumbFree\models with ModelStore (resumable, SHA-256 checked):
dotnet run --project windows/tools/SpeechBench -c Release -- download

# Benchmark every installed model (Hindi + English Whisper models are found in Voice Assistant's and Voice Dictation's
# models folders too), or only the ones listed; --threads takes a list for a thread sweep:
dotnet run --project windows/tools/SpeechBench -c Release
dotnet run --project windows/tools/SpeechBench -c Release -- en-parakeet-110m en-kroko-live --threads 6,2,4,8
```

The integration tests in `windows/tests/ThumbFree.Speech.Tests` use the same models and skip when they aren't there.

## How it measures

- **Load**: creating the recognizer. **1st call**: the first transcription (1 s of silence) after loading, which the
  app does as a warm-up. Then each clip is transcribed 5 times and the median is shown (once only if a run takes over
  3 s). "all three" is the three clips joined: 17.3 s of speech, the "17 s" in PLAN.md 4.4.
- Whisper decodes at most 14 s at a time in the app (a 768-frame window), so it isn't run on the 17.3 s clip.
- **Live typing**: Kroko gets each clip 100 ms at a time, as from the microphone, with a simulated clock (a chunk is
  decoded once it has been "recorded" and the previous one is done). A word *appears* when the text so far first holds
  it in its final form. When it was *spoken* comes from Parakeet TDT 110M's token timestamps (the end of the word's last
  token), matched to Kroko's words in order. The difference is the latency: the 100 ms chunk, the model's own delay and
  the decode time.

## Results: Snapdragon X Elite (X1E80100, 12 cores), Windows 11 ARM64, Nov 2026

sherpa-onnx 2.0.1 (onnxruntime 1.28.2), Whisper.net 1.9.1, .NET 10.0.14, ARM64 process. Each cell is the median time
and, in brackets, the RTF.

| Model | Threads | Load | 1st call | send-logs (2.9 s) | genres (3.9 s) | dictation (10.5 s) | all three (17.3 s) |
|---|---|---|---|---|---|---|---|
| Kroko live (streaming) | 2 | 1871 ms | 70 ms | 123 ms (0.042) | 123 ms (0.031) | 309 ms (0.030) | 455 ms (0.026) |
| Parakeet TDT 110M | 2 | 776 ms | 23 ms | 51 ms (0.017) | 67 ms (0.017) | 185 ms (0.018) | 283 ms (0.016) |
| Parakeet TDT 110M | 4 | 817 ms | 23 ms | 45 ms (0.016) | 56 ms (0.014) | 148 ms (0.014) | 250 ms (0.014) |
| Parakeet TDT 110M | 6 | 755 ms | 25 ms | 49 ms (0.017) | 63 ms (0.016) | 151 ms (0.014) | 236 ms (0.014) |
| Parakeet TDT 110M | 8 | 803 ms | 38 ms | 89 ms (0.030) | 84 ms (0.022) | 180 ms (0.017) | 265 ms (0.015) |
| Parakeet TDT 0.6B v2 | 2 | 1595 ms | 76 ms | 166 ms (0.057) | 207 ms (0.053) | 523 ms (0.050) | 863 ms (0.050) |
| Parakeet TDT 0.6B v2 | 4 | 1530 ms | 61 ms | 123 ms (0.042) | 160 ms (0.041) | 377 ms (0.036) | 624 ms (0.036) |
| Parakeet TDT 0.6B v2 | 6 | 1641 ms | 60 ms | 134 ms (0.046) | 163 ms (0.042) | 369 ms (0.035) | 620 ms (0.036) |
| Parakeet TDT 0.6B v2 | 8 | 1453 ms | 61 ms | 132 ms (0.045) | 172 ms (0.044) | 368 ms (0.035) | 567 ms (0.033) |
| Whisper Hinglish Swift | 2 | 145 ms | 460 ms | 419 ms (0.143) | 454 ms (0.116) | 497 ms (0.047) | (split in the app) |
| Whisper Hinglish Swift | 4 | 117 ms | 260 ms | 255 ms (0.087) | 265 ms (0.068) | 329 ms (0.031) | (split in the app) |
| Whisper Hinglish Swift | 6 | 238 ms | 228 ms | 208 ms (0.071) | 218 ms (0.056) | 285 ms (0.027) | (split in the app) |
| Whisper Hinglish Swift | 8 | 117 ms | 186 ms | 183 ms (0.063) | 193 ms (0.049) | 285 ms (0.027) | (split in the app) |
| Whisper Hinglish Apex | 2 | 1271 ms | 5845 ms | 5939 ms (2.030) | 5551 ms (1.420) | 5661 ms (0.541) | (split in the app) |
| Whisper Hinglish Apex | 4 | 1195 ms | 3102 ms | 3188 ms (1.090) | 3236 ms (0.828) | 3277 ms (0.313) | (split in the app) |
| Whisper Hinglish Apex | 6 | 1270 ms | 2655 ms | 2555 ms (0.873) | 2639 ms (0.675) | 3102 ms (0.296) | (split in the app) |
| Whisper Hinglish Apex | 8 | 1158 ms | 1958 ms | 2181 ms (0.746) | 2713 ms (0.694) | 3924 ms (0.375) | (split in the app) |

Live typing (Kroko, 2 threads):

| Clip | Decode per 100 ms chunk: mean / max | Finish | Word shown after it was spoken: median / p90 / max | Words matched |
|---|---|---|---|---|
| send-logs | 2.0 / 29.3 ms | 68 ms | 808 / 1389 / 1389 ms | 7 of 7 |
| genres | 1.6 / 31.3 ms | 67 ms | 811 / 1391 / 1391 ms | 9 of 9 |
| dictation | 2.5 / 36.9 ms | 65 ms | 1072 / 1630 / 1850 ms | 35 of 37 |
| all three | 2.3 / 32.7 ms | 67 ms | 989 / 1609 / 1729 ms | 50 of 52 |
| **all clips** | | | **989 / 1589 / 1850 ms** | 101 |

The same build for x64, run under emulation on this PC (`dotnet publish -r win-x64`): Parakeet 110M RTF 0.039–0.052,
Kroko 0.064–0.067, about 2.7 times slower than native ARM64.

Text (6 threads):

| Model | Clip | Text |
|---|---|---|
| Kroko live (streaming) | send-logs | Please send me the latest build logs. |
| Kroko live (streaming) | genres | I tested it on YouTube videos of different genres. |
| Kroko live (streaming) | dictation | So um I wanted to check the pull request comma, and then I'm merge it into main full stop new line. After that, we can ask GitHub copilot to review the Kubernet's config and push the build. |
| Kroko live (streaming) | all three | Please send me the latest build logs. I, tested it on YouTube videos of different genres. So I wanted to check the pull request comma and then a merge it into main full stop new line. After that, we can ask GitHub copilot to review the Kubernets config and push the build. |
| Parakeet TDT 110M | send-logs | Please send me the latest build logs. |
| Parakeet TDT 110M | genres | I tested it on YouTube videos of different genres. |
| Parakeet TDT 110M | dictation | So um I wanted to check the pull request comma and then a merge it into main full stop new line after that we can ask GitHub CoPilot to review the Kubirnets config and push the build. |
| Parakeet TDT 110M | all three | Please send me the latest build logs. I tested it on YouTube videos of different genres. So um I wanted to check the pull request comma and then a merge it into main full stop new line after that we can ask GitHub Co pilot to review the Kubirnetz config and push the build. |
| Parakeet TDT 0.6B v2 | send-logs | Please send me the latest build logs. |
| Parakeet TDT 0.6B v2 | genres | I tested it on YouTube videos of different genres. |
| Parakeet TDT 0.6B v2 | dictation | So um I wanted to check the pull request comma and then um merge it into main full stop new line after that we can ask GitHub co-pilot to review the Cabarenets config and push the build. |
| Parakeet TDT 0.6B v2 | all three | Please send me the latest build logs. I tested it on YouTube videos of different genres. So um I wanted to check the pull request comma and then um merge it into main full stop new line after that we can ask GitHub Copilot to review the Cubarenets config and push the build. |
| Whisper Hinglish Swift | send-logs | Please send me the latest build logs. |
| Whisper Hinglish Swift | genres | I tested it on YouTube videos of different gendures. |
| Whisper Hinglish Swift | dictation | So am I wanted to check the poll request comma and then emerge it into main full stop new line after that we can ask GitHub co pilot to review the kubernets config and push the build. |
| Whisper Hinglish Apex | send-logs | Please send me the latest build logs. |
| Whisper Hinglish Apex | genres | I tested it on YouTube videos of different jaanres. |
| Whisper Hinglish Apex | dictation | So um I wanted to check the pull request comma and then merge it into main full stop new line after that we can ask GitHub co pilot to review the kubernets config and push the build. |

This is the raw recognizer text; the app's word list ("Kubernetes", "GitHub Copilot") and formatter ("comma", "full
stop", "new line", "um") clean it up afterwards.

## What it means

- **Parakeet 110M** turns the 17.3 s of speech into text in about 0.24 s (PLAN.md said 0.4 s), loads in under a
  second, and is ready on the first call. 4 threads is as fast as 6; 8 is slower. 4 threads is a good default, which
  matches PLAN.md 3.1 ("at most 4 decoding threads").
- **Parakeet 0.6B** needs about 0.6 s for the same 17.3 s and 1.5 s to load: fine for "types when you finish".
- **Kroko** keeps up with ease (a 100 ms chunk takes 2–4 ms on average; the encoder runs once per ~1.3 s and then takes
  30–70 ms), but words show about **1 s after they're spoken** (median; 1.6 s at p90, 1.9 s at worst). That's the
  model, not the integration: its encoder decodes 1.28 s chunks (`decode_chunk_len` 128 frames in the ONNX metadata),
  so words arrive in bursts about every 1.3 s. A Kroko export with smaller chunks would show words sooner. For the same
  reason, finishing a dictation adds silence until two windows past the end are decoded, so the last words aren't lost
  (about 70 ms here). Loading takes about 2 s (onnxruntime setting up the 70 MB float encoder; the punctuation model
  takes 50 ms), so the app should load it in the background at start.
- **Whisper** costs the same for any piece up to 14 s (the encoder always runs the 768-frame window): about 0.2–0.3 s
  per piece for Swift and 2–3 s for Apex at 6–8 threads. Whisper benefits from more threads than Parakeet.
- An **x64 build is 2.7 times slower** on an ARM64 PC: Snapdragon users should get the `win-arm64` zip.
