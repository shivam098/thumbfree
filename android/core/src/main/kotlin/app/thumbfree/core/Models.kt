package app.thumbfree.core

/** Dictation language. Hindi + English is typed in Roman letters. */
enum class Language { English, Hinglish }

/** A downloadable speech model, pinned by SHA-256 (a mismatch deletes the file). */
data class ModelSpec(
    val id: String,
    val file: String,
    val url: String,
    val sha256: String,
    val bytes: Long,
    val label: String,
    val language: Language,
)

object ModelCatalog {
    val EnglishFast = ModelSpec(
        id = "en-base",
        file = "ggml-base.en-q8_0.bin",
        url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en-q8_0.bin",
        sha256 = "a4d4a0768075e13cfd7e19df3ae2dbc4a68d37d36a7dad45e8410c9a34f8c87e",
        bytes = 81_781_811,
        label = "English",
        language = Language.English,
    )

    val EnglishAccurate = ModelSpec(
        id = "en-small",
        file = "ggml-small.en-q8_0.bin",
        url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q8_0.bin",
        sha256 = "67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067",
        bytes = 264_477_561,
        label = "English, accurate (slower; for phones with 6 GB+ memory)",
        language = Language.English,
    )

    val Hinglish = ModelSpec(
        id = "hinglish-swift",
        file = "ggml-hinglish-swift-q8_0.bin",
        url = "https://github.com/shivam098/windows-voice-assistant/releases/download/models-hinglish-swift-1/ggml-hinglish-swift-q8_0.bin",
        sha256 = "0cf6e7077971d9aa43963b04bf764a47600e873f7e0f3f0f9742323560171245",
        bytes = 81_768_585,
        label = "Hindi + English (typed in Roman letters)",
        language = Language.Hinglish,
    )

    val all = listOf(EnglishFast, Hinglish, EnglishAccurate)

    fun byId(id: String) = all.firstOrNull { it.id == id }

    /** The model to use for a language. */
    fun forLanguage(language: Language, accurateEnglish: Boolean) = when (language) {
        Language.Hinglish -> Hinglish
        Language.English -> if (accurateEnglish) EnglishAccurate else EnglishFast
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
