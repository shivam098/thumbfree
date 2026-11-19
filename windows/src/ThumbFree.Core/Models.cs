namespace ThumbFree.Core;

/// <summary>Dictation language. Hindi + English is typed in Roman letters.</summary>
public enum Language
{
    English,
    Hinglish,
}

/// <summary>The recognizer a model runs on.</summary>
public enum Engine
{
    /// <summary>OpenAI Whisper through whisper.cpp (Whisper.net).</summary>
    Whisper,

    /// <summary>NVIDIA Parakeet TDT transducer through sherpa-onnx (ONNX Runtime). Punctuates and capitalizes by itself.</summary>
    Parakeet,

    /// <summary>
    /// A streaming transducer through sherpa-onnx's online recognizer: text arrives while you speak (live typing), with a
    /// small punctuation model on each finished phrase.
    /// </summary>
    Streaming,
}

/// <summary>Speed/accuracy choice per language. <see cref="Live"/> streams words as you speak; <see cref="Accurate"/> is the biggest model.</summary>
public enum Tier
{
    Live,
    Standard,
    Accurate,
}

/// <summary>One downloadable file, pinned by SHA-256 (a mismatch deletes it). <see cref="Path"/> is relative to the models folder, with '/'.</summary>
public sealed record ModelFile(string Path, string Url, string Sha256, long Bytes)
{
    /// <summary>The file name without its folder ("tokens.txt").</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>A speech model: its files, engine and how it's used. The same ids, paths and hashes as the Android app.</summary>
public sealed record ModelSpec(string Id, string Label, Language Language, Engine Engine, IReadOnlyList<ModelFile> Files)
{
    /// <summary>Gets the "words I use often" list as a Whisper prompt (measured: it garbles the fast Hindi + English model).</summary>
    public bool PromptVocabulary { get; init; }

    /// <summary>Fast enough to re-transcribe while you speak.</summary>
    public bool LivePreview { get; init; } = true;

    /// <summary>Memory below which the app warns before downloading.</summary>
    public int RecommendedMemoryGb { get; init; }

    public long Bytes => Files.Sum(f => f.Bytes);

    /// <summary>Longest piece transcribed in one call: Whisper's window is 30 s (14 s is used); Parakeet has no fixed window.</summary>
    public int MaxPieceSamples => Engine == Engine.Whisper ? Decoding.MaxPieceSamples : Decoding.SampleRate * 30;

    /// <summary>Words arrive while you speak (no re-transcription for live text).</summary>
    public bool Streaming => Engine == Engine.Streaming;

    public ModelFile File(string name) => Files.First(f => f.Name == name);
}

public static class ModelCatalog
{
    private const string Parakeet110M = "https://github.com/shivam098/thumbfree/releases/download/models-parakeet-110m-1";
    private const string Parakeet06B = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/84b478d541cdbf0c296a7ab5f1e20a6f8f998114";
    private const string KrokoLive = "https://github.com/shivam098/thumbfree/releases/download/models-kroko-en-live-1";

    /// <summary>
    /// Kroko ASR English streaming Zipformer (Banafo, CC BY-SA 4.0) + Edge-Punct-Casing punctuation (Apache-2.0). About
    /// 10× faster than real time on two laptop cores: words appear as you say them.
    /// </summary>
    public static ModelSpec EnglishLive { get; } = new(
        "en-kroko-live",
        "English, live (types as you speak)",
        Language.English,
        Engine.Streaming,
        [
            new("kroko-en-live/encoder.onnx", $"{KrokoLive}/encoder.onnx", "d4881c57449d581e0770fd53fa66c2fdc6cd167d92ece7c715e603defc96d9d4", 70_092_599),
            new("kroko-en-live/decoder.onnx", $"{KrokoLive}/decoder.onnx", "455ba38466fce8d5a57e7db68a323b684079ca4d9e1dd93a740d9b2429aae3b1", 617_488),
            new("kroko-en-live/joiner.onnx", $"{KrokoLive}/joiner.onnx", "d406f616736350e2a7df3e39398b78eb2fc1a2ca6973a19d3853fa3227e25b52", 336_817),
            new("kroko-en-live/tokens.txt", $"{KrokoLive}/tokens.txt", "396dbeb5f4858875690716084f54e90d339679d0ba3e6b5b584f3d7589254d2d", 6_310),
            new("kroko-en-live/punct.int8.onnx", $"{KrokoLive}/punct.int8.onnx", "9d611f445fe4a46186080fe161be6059d87d72eb88d3a8cb00c1a06e83a6067e", 7_490_500),
            new("kroko-en-live/punct-bpe.vocab", $"{KrokoLive}/punct-bpe.vocab", "e118b7ad88c54db562517df49e1cffd4836d166c34fb190fd311d7f34eb238f5", 149_430),
        ]);

    /// <summary>NVIDIA parakeet-tdt_ctc-110m (CC BY 4.0), TDT head, int8 ONNX from sherpa-onnx.</summary>
    public static ModelSpec EnglishFast { get; } = new(
        "en-parakeet-110m",
        "English",
        Language.English,
        Engine.Parakeet,
        [
            new("parakeet-tdt-110m/encoder.int8.onnx", $"{Parakeet110M}/encoder.int8.onnx", "0f35509ddeb9b39002fb077d979a9fe74f06eb0bc4dd5c34f512f82e5111d657", 131_113_202),
            new("parakeet-tdt-110m/decoder.int8.onnx", $"{Parakeet110M}/decoder.int8.onnx", "f7c331c5504c2e593c76ed22b728e3f554af6c4a383dde862e719ced08b1da19", 3_955_863),
            new("parakeet-tdt-110m/joiner.int8.onnx", $"{Parakeet110M}/joiner.int8.onnx", "bf7dff69e9f2cdbe9943d70da358f38b361c115ba0105bae7e908e0d6ec782f6", 1_411_403),
            new("parakeet-tdt-110m/tokens.txt", $"{Parakeet110M}/tokens.txt", "450e56bd2f036fe5b6aa821865838cc5aa9d8b0106134ce9a9ba0664abe6cd10", 9_953),
        ]);

    /// <summary>NVIDIA parakeet-tdt-0.6b-v2 (CC BY 4.0), int8 ONNX from sherpa-onnx. Best English accuracy on the Open ASR leaderboard.</summary>
    public static ModelSpec EnglishAccurate { get; } = new(
        "en-parakeet-0.6b",
        "English, accurate (slower; needs 6 GB+ memory)",
        Language.English,
        Engine.Parakeet,
        [
            new("parakeet-tdt-0.6b-v2/encoder.int8.onnx", $"{Parakeet06B}/encoder.int8.onnx", "92a10bd9c23936ea0566c29f963bc57c2a5df4261b6a8921c39abea66557ca8b", 652_282_309),
            new("parakeet-tdt-0.6b-v2/decoder.int8.onnx", $"{Parakeet06B}/decoder.int8.onnx", "4d097bae3bac5c96018d5293ba39f1b657296102b142affda40378f38b020f41", 7_257_753),
            new("parakeet-tdt-0.6b-v2/joiner.int8.onnx", $"{Parakeet06B}/joiner.int8.onnx", "77a29f3e5ac7d37074e0af465391b80f314015d5b97f0f90c6c7767f1d56f428", 1_739_080),
            new("parakeet-tdt-0.6b-v2/tokens.txt", $"{Parakeet06B}/tokens.txt", "ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d", 9_384),
        ])
    {
        LivePreview = false,
        RecommendedMemoryGb = 6,
    };

    /// <summary>Oriserve Whisper-Hindi2Hinglish-Swift (Apache-2.0, fine-tuned from Whisper base), q8.</summary>
    public static ModelSpec Hinglish { get; } = new(
        "hinglish-swift",
        "Hindi + English (typed in Roman letters)",
        Language.Hinglish,
        Engine.Whisper,
        [
            new(
                "ggml-hinglish-swift-q8_0.bin",
                "https://github.com/shivam098/windows-voice-assistant/releases/download/models-hinglish-swift-1/ggml-hinglish-swift-q8_0.bin",
                "0cf6e7077971d9aa43963b04bf764a47600e873f7e0f3f0f9742323560171245",
                81_768_585),
        ]);

    /// <summary>Oriserve Whisper-Hindi2Hinglish-Apex (Apache-2.0, fine-tuned from large-v3-turbo), q8. Best Hinglish, but big.</summary>
    public static ModelSpec HinglishAccurate { get; } = new(
        "hinglish-apex",
        "Hindi + English, accurate (much slower; needs 8 GB+ memory)",
        Language.Hinglish,
        Engine.Whisper,
        [
            new(
                "ggml-hinglish-apex-q8_0.bin",
                "https://github.com/shivam098/windows-voice-assistant/releases/download/models-hinglish-apex-1/ggml-hinglish-apex-q8_0.bin",
                "0b4324d2c1ad64f20883ee7fcd5d2bb0a8466287dc70d74bc47066200c28c719",
                874_188_075),
        ])
    {
        PromptVocabulary = true,
        LivePreview = false,
        RecommendedMemoryGb = 8,
    };

    /// <summary>
    /// Silero voice-activity model (MIT) for Whisper.net, Windows only: tells speech from music and noise and splits long
    /// recordings at pauses. Downloaded with the first model.
    /// </summary>
    public static ModelFile Vad { get; } = new(
        "ggml-silero-v6.2.0.bin",
        "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin",
        "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987",
        885_098);

    public static IReadOnlyList<ModelSpec> All { get; } = [EnglishFast, EnglishLive, Hinglish, EnglishAccurate, HinglishAccurate];

    public static ModelSpec? ById(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>Every file path any current model uses (anything else in the models folder is left over from older versions).</summary>
    public static IReadOnlySet<string> KnownPaths { get; } = All.SelectMany(s => s.Files).Select(f => f.Path).Append(Vad.Path).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The model for a language and tier. Hindi + English has no streaming model: its live tier is the standard model
    /// (live text then updates about every half second instead of word by word).
    /// </summary>
    public static ModelSpec ForLanguage(Language language, Tier tier) => language switch
    {
        Language.Hinglish => tier == Tier.Accurate ? HinglishAccurate : Hinglish,
        _ => tier switch
        {
            Tier.Live => EnglishLive,
            Tier.Standard => EnglishFast,
            _ => EnglishAccurate,
        },
    };
}

/// <summary>Decoding constants shared by the engines.</summary>
public static class Decoding
{
    public const int SampleRate = 16_000;

    /// <summary>Whisper pieces are at most 14 s: inside a 768-frame encoder window, ~3x faster than the full 30 s window.</summary>
    public const int MaxPieceSamples = SampleRate * 14;

    public const int FinalAudioContext = 768;
}
