package app.thumbfree

import app.thumbfree.core.DecodeOptions
import app.thumbfree.core.Decoding
import app.thumbfree.core.SpeechAudio
import app.thumbfree.whisper.WhisperModel
import com.k2fsa.sherpa.onnx.FeatureConfig
import com.k2fsa.sherpa.onnx.OfflineModelConfig
import com.k2fsa.sherpa.onnx.OfflineRecognizer
import com.k2fsa.sherpa.onnx.OfflineRecognizerConfig
import com.k2fsa.sherpa.onnx.OfflineTransducerModelConfig
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
