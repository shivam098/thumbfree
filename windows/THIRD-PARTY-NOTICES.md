# Third-party notices

ThumbFree for Windows runs these libraries and downloads these models (each once, checked against a fixed SHA-256).
Each keeps its own license.

## Libraries

| Component | Used for | License |
|---|---|---|
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (C# API, NuGet 2.0.1) | Running the Kroko and Parakeet models | Apache-2.0 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) (inside sherpa-onnx) | Neural network inference | MIT |
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) via [Whisper.net](https://github.com/sandrohanea/whisper.net) | Running the Hindi + English models and Silero | MIT |
| [NAudio](https://github.com/naudio/NAudio) | Microphone capture and the start/stop tones | MIT |

## Models

| Model | Used for | License |
|---|---|---|
| [Kroko ASR](https://huggingface.co/Banafo/Kroko-ASR) English streaming Zipformer, by Banafo (community model) | English, Live | CC BY-SA 4.0 |
| [Edge-Punct-Casing](https://huggingface.co/frankyoujian/Edge-Punct-Casing) | Punctuation for English, Live | Apache-2.0 |
| [NVIDIA Parakeet TDT CTC 110M](https://huggingface.co/nvidia/parakeet-tdt_ctc-110m), TDT head, int8 ONNX from sherpa-onnx | English, Standard | CC BY 4.0 |
| [NVIDIA Parakeet TDT 0.6B v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2), [int8 ONNX from sherpa-onnx](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8) | English, Accurate | CC BY 4.0 |
| [Oriserve/Whisper-Hindi2Hinglish-Swift](https://huggingface.co/Oriserve/Whisper-Hindi2Hinglish-Swift), fine-tuned from [openai/whisper-base](https://huggingface.co/openai/whisper-base) | Hindi + English, Standard | Apache-2.0 (fine-tune); MIT (OpenAI Whisper base model, mel filters, vocabulary) |
| [Oriserve/Whisper-Hindi2Hinglish-Apex](https://huggingface.co/Oriserve/Whisper-Hindi2Hinglish-Apex), fine-tuned from [openai/whisper-large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo) | Hindi + English, Accurate | Apache-2.0 (fine-tune); MIT (OpenAI Whisper base model, mel filters, vocabulary) |
| [Silero VAD](https://github.com/snakers4/silero-vad) (ggml from [ggml-org/whisper-vad](https://huggingface.co/ggml-org/whisper-vad)) | Finding pauses in speech | MIT |

## Kroko ASR and Edge-Punct-Casing

The Kroko model files (encoder, decoder, joiner, tokens) and the Edge-Punct-Casing punctuation model are used
unmodified. They are downloaded separately from the app, from the
[`models-kroko-en-live-1`](https://github.com/shivam098/thumbfree/releases/tag/models-kroko-en-live-1) release. Kroko
ASR is by [Banafo](https://huggingface.co/Banafo) and licensed under Creative Commons Attribution-ShareAlike 4.0
(<https://creativecommons.org/licenses/by-sa/4.0/>). Edge-Punct-Casing is licensed under the Apache License,
Version 2.0 (<https://www.apache.org/licenses/LICENSE-2.0>).

## NVIDIA Parakeet

The Parakeet models are by NVIDIA, licensed under Creative Commons Attribution 4.0
(<https://creativecommons.org/licenses/by/4.0/>). ThumbFree uses the int8 ONNX exports made for sherpa-onnx. The 110M
model is mirrored in the [`models-parakeet-110m-1`](https://github.com/shivam098/thumbfree/releases/tag/models-parakeet-110m-1)
release.

## Oriserve/Whisper-Hindi2Hinglish-Swift and Apex

Models by [Oriserve](https://oriserve.com). Licensed under the Apache License, Version 2.0
(<https://www.apache.org/licenses/LICENSE-2.0>); a copy of the license is attached to the
[`models-hinglish-swift-1`](https://github.com/shivam098/windows-voice-assistant/releases/tag/models-hinglish-swift-1) and
[`models-hinglish-apex-1`](https://github.com/shivam098/windows-voice-assistant/releases/tag/models-hinglish-apex-1) releases.

They are fine-tuned from OpenAI's `whisper-base` and `whisper-large-v3-turbo`, and the converted files also contain
OpenAI Whisper's mel filter bank and tokenizer vocabulary. Those are Copyright (c) 2022 OpenAI under the MIT License;
OpenAI's license text is attached to the same releases.

**Changes made:** the original Hugging Face weights were converted to the ggml format and quantized to 8-bit (q8_0) so
whisper.cpp can run them; the models themselves were not retrained. The steps are in
[tools/ModelConvert/README.md](https://github.com/shivam098/windows-voice-assistant/blob/main/tools/ModelConvert/README.md)
in the Voice Assistant repository. The models are distributed "AS IS", without warranties or conditions of any kind.
