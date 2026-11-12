package app.thumbfree

import app.thumbfree.core.DecodeOptions
import app.thumbfree.core.Decoding
import app.thumbfree.core.Hallucinations
import app.thumbfree.core.SpeechAudio
import app.thumbfree.core.TranscriptText
import app.thumbfree.whisper.WhisperModel
import java.io.File
import java.util.concurrent.Executors

/**
 * All speech recognition runs here, one call at a time, on a single background thread. The model stays loaded while
 * the keyboard is in use and is unloaded after a few idle minutes; nothing runs while you're not dictating.
 */
class SpeechEngine {
    private val executor = Executors.newSingleThreadExecutor { r -> Thread(r, "speech").apply { isDaemon = true } }

    private var model: WhisperModel? = null // speech thread only

    @Volatile private var active: WhisperModel? = null

    val threads = Decoding.threads(Runtime.getRuntime().availableProcessors())

    fun run(block: () -> Unit) = executor.execute(block)

    /** Speech thread: the model for [file], loading or switching as needed. Throws if it can't be loaded. */
    fun model(file: File): WhisperModel {
        model?.let { if (it.file == file) return it }
        model?.close()
        model = null
        active = null
        return WhisperModel.load(file).also {
            model = it
            active = it
        }
    }

    /** Speech thread: the loaded model, if any. */
    fun loaded(): WhisperModel? = model

    /** Any thread: stops the recognition in progress (the next session calls resume()). */
    fun abort() {
        active?.abort()
    }

    fun unload() = run {
        model?.close()
        model = null
        active = null
    }

    /** Speech thread: a quick transcript for the live preview (no retries, context sized to the audio). */
    fun preview(model: WhisperModel, audio: FloatArray): String? =
        model.transcribe(audio, DecodeOptions(threads, Decoding.previewContext(audio.size), temperatureFallback = false))
            ?.let { TranscriptText.clean(it) }

    /**
     * Speech thread: the final transcript. Silence is trimmed (and nothing runs if there was no speech); long dictation
     * is cut at pauses into pieces of at most 14 s. Null if aborted.
     */
    fun transcribe(model: WhisperModel, audio: FloatArray): String? {
        val regions = SpeechAudio.speech(audio)
        val span = SpeechAudio.trim(audio, regions) ?: return ""
        val parts = ArrayList<String>()
        for (piece in SpeechAudio.split(span, regions, Decoding.MAX_PIECE_SAMPLES)) {
            val samples = audio.copyOfRange(piece.start, piece.end)
            val text = TranscriptText.clean(model.transcribe(samples, DecodeOptions(threads, Decoding.FINAL_AUDIO_CONTEXT)) ?: return null)
            if (text.isNotEmpty() && !Hallucinations.isLikely(text, samples.size)) parts += text
        }
        return parts.joinToString(" ")
    }
}
