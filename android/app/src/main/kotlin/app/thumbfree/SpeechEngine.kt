package app.thumbfree

import app.thumbfree.core.Engine
import app.thumbfree.core.Hallucinations
import app.thumbfree.core.ModelSpec
import app.thumbfree.core.SpeechAudio
import app.thumbfree.core.TranscriptText
import app.thumbfree.core.Vocabulary
import java.io.File
import java.util.concurrent.Executors

/**
 * All speech recognition runs here, one call at a time, on a single background thread. The model stays loaded while
 * the keyboard is in use and is unloaded after a few idle minutes; nothing runs while you're not dictating.
 *
 * @param folder where a model's files are (the folder of its first file).
 */
class SpeechEngine(private val folder: (ModelSpec) -> File) {
    private val executor = Executors.newSingleThreadExecutor { r -> Thread(r, "speech").apply { isDaemon = true } }

    private var model: Recognizer? = null // speech thread only
    private var modelId: String? = null

    @Volatile private var active: Recognizer? = null

    val threads = app.thumbfree.core.Decoding.threads(Runtime.getRuntime().availableProcessors())

    fun run(block: () -> Unit) = executor.execute(block)

    /** Speech thread: the recognizer for [spec], loading or switching as needed. Throws if it can't be loaded. */
    fun model(spec: ModelSpec): Recognizer {
        model?.let { if (modelId == spec.id) return it }
        unloadNow()
        val loaded = when (spec.engine) {
            Engine.Whisper -> WhisperRecognizer.load(File(folder(spec), spec.files.single().path.substringAfterLast('/')), threads)
            Engine.Parakeet -> ParakeetRecognizer.load(folder(spec), threads)
        }
        model = loaded
        modelId = spec.id
        active = loaded
        return loaded
    }

    /** Speech thread: the loaded recognizer, if any. */
    fun loaded(): Recognizer? = model

    /** Any thread: stops the recognition in progress (the next session calls resume()). */
    fun abort() {
        active?.abort()
    }

    fun unload() = run { unloadNow() }

    private fun unloadNow() {
        model?.close()
        model = null
        modelId = null
        active = null
    }

    /**
     * Speech thread: a quick transcript for the live preview. [vocabulary] fixes near-misses of your words and, when
     * [prompt] is set, is also given to Whisper as a hint.
     */
    fun preview(model: Recognizer, audio: FloatArray, vocabulary: List<String> = emptyList(), prompt: Boolean = false): String? {
        val text = model.transcribe(audio, preview = true, prompt = if (prompt) Vocabulary.prompt(vocabulary) else null) ?: return null
        return finish(TranscriptText.clean(text), vocabulary, prompt)
    }

    /**
     * Speech thread: the final transcript. Silence is trimmed (and nothing runs if there was no speech); long dictation
     * is cut at pauses into pieces of at most [maxPieceSamples]. Null if aborted.
     */
    fun transcribe(
        model: Recognizer,
        audio: FloatArray,
        vocabulary: List<String> = emptyList(),
        prompt: Boolean = false,
        maxPieceSamples: Int = app.thumbfree.core.Decoding.MAX_PIECE_SAMPLES,
    ): String? {
        val hint = if (prompt) Vocabulary.prompt(vocabulary) else null
        val regions = SpeechAudio.speech(audio)
        val span = SpeechAudio.trim(audio, regions) ?: return ""
        val parts = ArrayList<String>()
        for (piece in SpeechAudio.split(span, regions, maxPieceSamples)) {
            val samples = audio.copyOfRange(piece.start, piece.end)
            val text = TranscriptText.clean(model.transcribe(samples, preview = false, prompt = hint) ?: return null)
            if (text.isNotEmpty() && !Hallucinations.isLikely(text, samples.size)) parts += finish(text, vocabulary, prompt)
        }
        return parts.filter { it.isNotEmpty() }.joinToString(" ")
    }

    /** Drops a transcript that only repeats the Whisper hint, then fixes near-misses of listed words. */
    private fun finish(text: String, vocabulary: List<String>, prompt: Boolean): String = when {
        prompt && Vocabulary.isEcho(text, vocabulary) -> ""
        else -> Vocabulary.correct(text, vocabulary)
    }
}
