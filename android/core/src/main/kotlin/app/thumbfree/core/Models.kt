package app.thumbfree.core

/** Dictation language. Hindi + English is typed in Roman letters. */
enum class Language { English, Hinglish }

/** The recognizer a model runs on. */
enum class Engine {
    /** OpenAI Whisper via whisper.cpp (ggml). */
    Whisper,

    /** NVIDIA Parakeet TDT transducer via sherpa-onnx (ONNX Runtime). Punctuates and capitalizes by itself. */
    Parakeet,
}

/** One downloadable file, pinned by SHA-256 (a mismatch deletes it). [path] is relative to the models folder. */
data class ModelFile(val path: String, val url: String, val sha256: String, val bytes: Long)

data class ModelSpec(
    val id: String,
    val label: String,
    val language: Language,
    val engine: Engine,
    val files: List<ModelFile>,
    /** Gets the "words I use often" list as a Whisper prompt (measured: it garbles the fast Hindi + English model). */
    val promptVocabulary: Boolean = false,
    /** Fast enough on phones to re-transcribe while you speak. */
    val livePreview: Boolean = true,
    /** Phone memory below which the setup screen warns before downloading. */
    val recommendedMemoryGb: Int = 0,
) {
    val bytes: Long get() = files.sumOf { it.bytes }

    /** Longest piece transcribed in one call: Whisper's window is 30 s (we use 14 s); Parakeet has no fixed window. */
    val maxPieceSamples: Int get() = if (engine == Engine.Parakeet) SpeechAudio.SAMPLE_RATE * 30 else Decoding.MAX_PIECE_SAMPLES

    fun file(name: String) = files.first { it.path.substringAfterLast('/') == name }
}

object ModelCatalog {
    private const val PARAKEET_110M = "https://github.com/shivam098/thumbfree/releases/download/models-parakeet-110m-1"
    private const val PARAKEET_06B = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/84b478d541cdbf0c296a7ab5f1e20a6f8f998114"

    /** NVIDIA parakeet-tdt_ctc-110m (CC BY 4.0), TDT head, int8 ONNX from sherpa-onnx. */
    val EnglishFast = ModelSpec(
        id = "en-parakeet-110m",
        label = "English",
        language = Language.English,
        engine = Engine.Parakeet,
        files = listOf(
            ModelFile("parakeet-tdt-110m/encoder.int8.onnx", "$PARAKEET_110M/encoder.int8.onnx", "0f35509ddeb9b39002fb077d979a9fe74f06eb0bc4dd5c34f512f82e5111d657", 131_113_202),
            ModelFile("parakeet-tdt-110m/decoder.int8.onnx", "$PARAKEET_110M/decoder.int8.onnx", "f7c331c5504c2e593c76ed22b728e3f554af6c4a383dde862e719ced08b1da19", 3_955_863),
            ModelFile("parakeet-tdt-110m/joiner.int8.onnx", "$PARAKEET_110M/joiner.int8.onnx", "bf7dff69e9f2cdbe9943d70da358f38b361c115ba0105bae7e908e0d6ec782f6", 1_411_403),
            ModelFile("parakeet-tdt-110m/tokens.txt", "$PARAKEET_110M/tokens.txt", "450e56bd2f036fe5b6aa821865838cc5aa9d8b0106134ce9a9ba0664abe6cd10", 9_953),
        ),
    )

    /** NVIDIA parakeet-tdt-0.6b-v2 (CC BY 4.0), int8 ONNX from sherpa-onnx. Best English accuracy on the Open ASR leaderboard. */
    val EnglishAccurate = ModelSpec(
        id = "en-parakeet-0.6b",
        label = "English, accurate (slower; for phones with 6 GB+ memory)",
        language = Language.English,
        engine = Engine.Parakeet,
        files = listOf(
            ModelFile("parakeet-tdt-0.6b-v2/encoder.int8.onnx", "$PARAKEET_06B/encoder.int8.onnx", "92a10bd9c23936ea0566c29f963bc57c2a5df4261b6a8921c39abea66557ca8b", 652_282_309),
            ModelFile("parakeet-tdt-0.6b-v2/decoder.int8.onnx", "$PARAKEET_06B/decoder.int8.onnx", "4d097bae3bac5c96018d5293ba39f1b657296102b142affda40378f38b020f41", 7_257_753),
            ModelFile("parakeet-tdt-0.6b-v2/joiner.int8.onnx", "$PARAKEET_06B/joiner.int8.onnx", "77a29f3e5ac7d37074e0af465391b80f314015d5b97f0f90c6c7767f1d56f428", 1_739_080),
            ModelFile("parakeet-tdt-0.6b-v2/tokens.txt", "$PARAKEET_06B/tokens.txt", "ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d", 9_384),
        ),
        livePreview = false,
        recommendedMemoryGb = 6,
    )

    /** Oriserve Whisper-Hindi2Hinglish-Swift (Apache-2.0, fine-tuned from Whisper base), q8. */
    val Hinglish = ModelSpec(
        id = "hinglish-swift",
        label = "Hindi + English (typed in Roman letters)",
        language = Language.Hinglish,
        engine = Engine.Whisper,
        files = listOf(
            ModelFile(
                "ggml-hinglish-swift-q8_0.bin",
                "https://github.com/shivam098/windows-voice-assistant/releases/download/models-hinglish-swift-1/ggml-hinglish-swift-q8_0.bin",
                "0cf6e7077971d9aa43963b04bf764a47600e873f7e0f3f0f9742323560171245",
                81_768_585,
            ),
        ),
    )

    /** Oriserve Whisper-Hindi2Hinglish-Apex (Apache-2.0, fine-tuned from large-v3-turbo), q8. Best Hinglish, but big. */
    val HinglishAccurate = ModelSpec(
        id = "hinglish-apex",
        label = "Hindi + English, accurate (much slower; for phones with 8 GB+ memory)",
        language = Language.Hinglish,
        engine = Engine.Whisper,
        files = listOf(
            ModelFile(
                "ggml-hinglish-apex-q8_0.bin",
                "https://github.com/shivam098/windows-voice-assistant/releases/download/models-hinglish-apex-1/ggml-hinglish-apex-q8_0.bin",
                "0b4324d2c1ad64f20883ee7fcd5d2bb0a8466287dc70d74bc47066200c28c719",
                874_188_075,
            ),
        ),
        promptVocabulary = true,
        livePreview = false,
        recommendedMemoryGb = 8,
    )

    val all = listOf(EnglishFast, Hinglish, EnglishAccurate, HinglishAccurate)

    fun byId(id: String) = all.firstOrNull { it.id == id }

    /** Every file path any current model uses (anything else in the models folder is left over from older versions). */
    val knownPaths: Set<String> get() = all.flatMap { s -> s.files.map { it.path } }.toSet()

    /** The model to use for a language: the bigger, slower one when [accurate]. */
    fun forLanguage(language: Language, accurate: Boolean) = when (language) {
        Language.Hinglish -> if (accurate) HinglishAccurate else Hinglish
        Language.English -> if (accurate) EnglishAccurate else EnglishFast
    }
}

/** Decoder settings for one call. */
data class DecodeOptions(
    val threads: Int,
    /** Whisper encoder context: 50 per second of audio, at most 1500 (30 s). */
    val audioContext: Int,
    /** Stop after this many tokens (0 = no limit). */
    val maxTokens: Int = 0,
    /** Re-decode at higher temperature when the first result looks unreliable (slower; better on hard audio). */
    val temperatureFallback: Boolean = true,
    val language: String = "en",
)

object Decoding {
    /** Final pieces are at most 14 s: inside a 768-frame encoder window, ~3x faster than the full 30 s window. */
    const val MAX_PIECE_SAMPLES = SpeechAudio.SAMPLE_RATE * 14
    const val FINAL_AUDIO_CONTEXT = 768

    /** Preview context sized to the audio (rounded up to 64 frames), so short previews are cheap. */
    fun previewContext(samples: Int): Int {
        val frames = samples * 50 / SpeechAudio.SAMPLE_RATE + 64
        return ((frames + 63) / 64 * 64).coerceIn(256, 1500)
    }

    /** Threads for the phone: the big cores are usually 4 of 8; more threads than big cores only adds heat. */
    fun threads(cores: Int) = when {
        cores >= 8 -> 4
        cores >= 4 -> cores / 2 + 1
        else -> maxOf(1, cores)
    }.coerceAtMost(4)
}
