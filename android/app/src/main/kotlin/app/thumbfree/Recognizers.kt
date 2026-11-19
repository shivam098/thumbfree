package app.thumbfree

import app.thumbfree.core.DecodeOptions
import app.thumbfree.core.Decoding
import app.thumbfree.core.SpeechAudio
import app.thumbfree.whisper.WhisperModel
import app.thumbfree.core.LiveText
import com.k2fsa.sherpa.onnx.EndpointConfig
import com.k2fsa.sherpa.onnx.EndpointRule
import com.k2fsa.sherpa.onnx.FeatureConfig
import com.k2fsa.sherpa.onnx.OfflineModelConfig
import com.k2fsa.sherpa.onnx.OfflineRecognizer
import com.k2fsa.sherpa.onnx.OfflineRecognizerConfig
import com.k2fsa.sherpa.onnx.OfflineTransducerModelConfig
import com.k2fsa.sherpa.onnx.OnlineModelConfig
import com.k2fsa.sherpa.onnx.OnlinePunctuation
import com.k2fsa.sherpa.onnx.OnlinePunctuationConfig
import com.k2fsa.sherpa.onnx.OnlinePunctuationModelConfig
import com.k2fsa.sherpa.onnx.OnlineRecognizer
import com.k2fsa.sherpa.onnx.OnlineRecognizerConfig
import com.k2fsa.sherpa.onnx.OnlineTransducerModelConfig
import java.io.Closeable
import java.io.File

/** A loaded speech model. [transcribe] runs on the speech thread; [abort] may be called from any thread. */
interface Recognizer : Closeable {
    /** Raw text for 16 kHz mono audio, or null if aborted or failed. [preview] trades accuracy for speed. */
    fun transcribe(samples: FloatArray, preview: Boolean, prompt: String? = null): String?

    /** Stops the call in progress; later calls return null until [resume]. */
    fun abort()

    fun resume()
}

class WhisperRecognizer(private val model: WhisperModel, private val threads: Int) : Recognizer {
    override fun transcribe(samples: FloatArray, preview: Boolean, prompt: String?): String? {
        val options = if (preview) {
            DecodeOptions(threads, Decoding.previewContext(samples.size), temperatureFallback = false)
        } else {
            DecodeOptions(threads, Decoding.FINAL_AUDIO_CONTEXT)
        }
        return model.transcribe(samples, options, prompt)
    }

    override fun abort() = model.abort()

    override fun resume() = model.resume()

    override fun close() = model.close()

    companion object {
        fun load(file: File, threads: Int) = WhisperRecognizer(WhisperModel.load(file), threads)
    }
}

/**
 * NVIDIA Parakeet TDT through sherpa-onnx. Greedy decoding; output already has punctuation and capitals. A decode
 * can't be interrupted, but it takes a fraction of the audio's length, and an aborted result is thrown away.
 */
class ParakeetRecognizer private constructor(private val recognizer: OfflineRecognizer) : Recognizer {
    @Volatile private var aborted = false
    private val lock = Any()
    private var closed = false

    override fun transcribe(samples: FloatArray, preview: Boolean, prompt: String?): String? = synchronized(lock) {
        if (aborted || closed) return null
        // Very short clips confuse the encoder; pad to a second.
        val input = if (samples.size < SpeechAudio.SAMPLE_RATE) samples.copyOf(SpeechAudio.SAMPLE_RATE) else samples
        val stream = recognizer.createStream()
        try {
            stream.acceptWaveform(input, SpeechAudio.SAMPLE_RATE)
            recognizer.decode(stream)
            if (aborted) null else recognizer.getResult(stream).text
        } finally {
            stream.release()
        }
    }

    override fun abort() {
        aborted = true
    }

    override fun resume() {
        aborted = false
    }

    override fun close() {
        aborted = true
        synchronized(lock) {
            if (!closed) {
                closed = true
                recognizer.release()
            }
        }
    }

    companion object {
        /** Loads encoder/decoder/joiner/tokens from [dir]; throws if they can't be read. */
        fun load(dir: File, threads: Int): ParakeetRecognizer {
            val names = listOf("encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt")
            names.forEach { require(File(dir, it).canRead()) { "Missing ${File(dir, it)}" } }
            val config = OfflineRecognizerConfig(
                featConfig = FeatureConfig(sampleRate = SpeechAudio.SAMPLE_RATE, featureDim = 80),
                modelConfig = OfflineModelConfig(
                    transducer = OfflineTransducerModelConfig(
                        encoder = File(dir, "encoder.int8.onnx").path,
                        decoder = File(dir, "decoder.int8.onnx").path,
                        joiner = File(dir, "joiner.int8.onnx").path,
                    ),
                    tokens = File(dir, "tokens.txt").path,
                    modelType = "nemo_transducer",
                    numThreads = threads,
                    provider = "cpu",
                ),
                decodingMethod = "greedy_search",
            )
            return ParakeetRecognizer(OfflineRecognizer(null, config))
        }
    }
}

/**
 * A streaming model (Kroko Zipformer) through sherpa-onnx's online recognizer, for live typing: audio goes in as it's
 * recorded and the text so far comes straight back. A phrase ends at a short pause; each finished phrase gets
 * punctuation from a small model, keeping the recognizer's own words and capitals.
 */
class StreamingRecognizer private constructor(
    private val recognizer: OnlineRecognizer,
    private val punctuation: OnlinePunctuation?,
) : Recognizer {
    private val lock = Any()
    @Volatile private var aborted = false
    private var closed = false

    /** One dictation. Speech thread only. */
    inner class Session {
        private val stream = recognizer.createStream()
        private val finished = mutableListOf<String>()
        private var partial = ""
        private var done = false

        /** Adds 16 kHz audio and returns the text so far. */
        fun accept(samples: FloatArray): String = synchronized(lock) {
            if (closed || done) return text()
            stream.acceptWaveform(samples, SpeechAudio.SAMPLE_RATE)
            while (recognizer.isReady(stream)) recognizer.decode(stream)
            partial = recognizer.getResult(stream).text.trim()
            if (recognizer.isEndpoint(stream)) {
                if (partial.isNotEmpty()) finished += punctuate(partial)
                partial = ""
                recognizer.reset(stream)
            }
            text()
        }

        fun text() = LiveText.join(finished, partial)

        /** Flushes the last words and returns the whole text; the session can't be used afterwards. */
        fun finish(): String = synchronized(lock) {
            if (!closed && !done) {
                // A little silence lets the model emit the last word before the stream ends.
                stream.acceptWaveform(FloatArray(SpeechAudio.SAMPLE_RATE * 3 / 10), SpeechAudio.SAMPLE_RATE)
                stream.inputFinished()
                while (recognizer.isReady(stream)) recognizer.decode(stream)
                val last = recognizer.getResult(stream).text.trim()
                if (last.isNotEmpty()) finished += punctuate(last)
                partial = ""
            }
            close()
            LiveText.endSentence(LiveText.join(finished, ""))
        }

        fun close() = synchronized(lock) {
            if (!done) {
                done = true
                stream.release()
            }
        }
    }

    /** Speech thread: a new dictation. */
    fun session() = Session()

    /**
     * Kroko writes a few marks itself ("push the build."); given those, the punctuation model adds its own after them
     * ("build.."). Only the model's marks are kept anyway ([LiveText.mergePunctuation]), so it gets the bare words.
     */
    private fun punctuate(text: String): String =
        punctuation?.let { model ->
            runCatching {
                val bare = text.split(' ').map { it.trimEnd('.', ',', '?', '!', ';', ':') }.filter { it.isNotEmpty() }.joinToString(" ")
                LiveText.mergePunctuation(text, model.addPunctuation(bare))
            }.getOrNull()
        } ?: text

    /** The whole recording at once (tests, and the fallback when no live session ran). */
    override fun transcribe(samples: FloatArray, preview: Boolean, prompt: String?): String? {
        if (aborted) return null
        val session = session()
        var i = 0
        while (i < samples.size) {
            val end = minOf(samples.size, i + SpeechAudio.SAMPLE_RATE / 10)
            session.accept(samples.copyOfRange(i, end))
            i = end
            if (aborted) {
                session.close()
                return null
            }
        }
        return session.finish()
    }

    override fun abort() {
        aborted = true
    }

    override fun resume() {
        aborted = false
    }

    override fun close() {
        aborted = true
        synchronized(lock) {
            if (!closed) {
                closed = true
                recognizer.release()
                punctuation?.release()
            }
        }
    }

    companion object {
        /** Loads the Kroko streaming model (and the punctuation model when present) from [dir]. */
        fun load(dir: File, threads: Int): StreamingRecognizer {
            val names = listOf("encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt")
            names.forEach { require(File(dir, it).canRead()) { "Missing ${File(dir, it)}" } }
            val config = OnlineRecognizerConfig(
                featConfig = FeatureConfig(sampleRate = SpeechAudio.SAMPLE_RATE, featureDim = 80),
                modelConfig = OnlineModelConfig(
                    transducer = OnlineTransducerModelConfig(
                        encoder = File(dir, "encoder.onnx").path,
                        decoder = File(dir, "decoder.onnx").path,
                        joiner = File(dir, "joiner.onnx").path,
                    ),
                    tokens = File(dir, "tokens.txt").path,
                    numThreads = minOf(threads, 2), // two threads already run ~10× faster than you speak
                    provider = "cpu",
                ),
                // A phrase ends after 0.8 s of silence (punctuation is added then).
                endpointConfig = EndpointConfig(
                    rule1 = EndpointRule(false, 2.4f, 0.0f),
                    rule2 = EndpointRule(true, 0.8f, 0.0f),
                    rule3 = EndpointRule(false, 0.0f, 20.0f),
                ),
                enableEndpoint = true,
                decodingMethod = "greedy_search",
            )
            val recognizer = OnlineRecognizer(null, config)
            val punctModel = File(dir, "punct.int8.onnx")
            val punctuation = if (punctModel.canRead()) {
                runCatching {
                    OnlinePunctuation(
                        null,
                        OnlinePunctuationConfig(
                            model = OnlinePunctuationModelConfig(cnnBilstm = punctModel.path, bpeVocab = File(dir, "punct-bpe.vocab").path, numThreads = 1),
                        ),
                    )
                }.getOrNull()
            } else {
                null
            }
            return StreamingRecognizer(recognizer, punctuation)
        }
    }
}
