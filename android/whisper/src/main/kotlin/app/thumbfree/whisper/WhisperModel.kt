package app.thumbfree.whisper

import android.os.Build
import android.util.Log
import app.thumbfree.core.DecodeOptions
import java.io.Closeable
import java.io.File

/** Which native build this phone can run. */
internal object CpuFeatures {
    /** ARMv8.2 dot product and half-precision on every core (some phones mix core types). */
    fun supportsV82(): Boolean {
        if (Build.SUPPORTED_ABIS.firstOrNull() != "arm64-v8a") return false
        val features = runCatching { File("/proc/cpuinfo").readLines() }.getOrDefault(emptyList())
            .filter { it.startsWith("Features") }
            .map { it.substringAfter(':').trim().split(' ').toSet() }
        return features.isNotEmpty() && features.all { "asimddp" in it && ("fphp" in it || "asimdhp" in it) }
    }
}

internal object WhisperNative {
    /** "armv8.2" or "generic": the native build in use. */
    val variant: String = if (CpuFeatures.supportsV82() && tryLoad("thumbfree_whisper_v82")) {
        "armv8.2"
    } else {
        System.loadLibrary("thumbfree_whisper")
        "generic"
    }

    private fun tryLoad(name: String) = try {
        System.loadLibrary(name)
        true
    } catch (e: UnsatisfiedLinkError) {
        Log.w("ThumbFree", "falling back to the generic speech engine: ${e.message}")
        false
    }

    @JvmStatic external fun init(path: String): Long

    @JvmStatic external fun free(handle: Long)

    @JvmStatic external fun setAbort(handle: Long, abort: Boolean)

    @JvmStatic external fun transcribe(
        handle: Long,
        samples: FloatArray,
        threads: Int,
        audioContext: Int,
        maxTokens: Int,
        temperatureFallback: Boolean,
        language: String,
        prompt: String?,
    ): ByteArray?

    @JvmStatic external fun systemInfo(): String
}

/**
 * A loaded Whisper model. [transcribe] runs on one worker thread at a time; [abort] may be called from any thread to
 * stop the call in progress (and any started before [resume]).
 */
class WhisperModel private constructor(private var handle: Long, val file: File) : Closeable {
    private val callLock = Any()
    private val handleLock = Any()

    /** Text for 16 kHz mono audio, or null if aborted or failed. */
    fun transcribe(samples: FloatArray, options: DecodeOptions, prompt: String? = null): String? = synchronized(callLock) {
        if (handle == 0L) return null
        // whisper.cpp rejects input under one second, which would drop one-word dictation.
        val input = if (samples.size < MIN_SAMPLES) samples.copyOf(MIN_SAMPLES) else samples
        WhisperNative.transcribe(
            handle, input, options.threads, options.audioContext, options.maxTokens,
            options.temperatureFallback, options.language, prompt,
        )?.toString(Charsets.UTF_8)
    }

    /** Stops the current call and makes later calls return null until [resume]. */
    fun abort() = synchronized(handleLock) { if (handle != 0L) WhisperNative.setAbort(handle, true) }

    fun resume() = synchronized(handleLock) { if (handle != 0L) WhisperNative.setAbort(handle, false) }

    override fun close() {
        abort()
        synchronized(callLock) {
            synchronized(handleLock) {
                if (handle != 0L) {
                    WhisperNative.free(handle)
                    handle = 0L
                }
            }
        }
    }

    companion object {
        private const val MIN_SAMPLES = 16_000 * 11 / 10

        val engineVariant: String get() = WhisperNative.variant

        val systemInfo: String get() = WhisperNative.systemInfo()

        /** Loads a ggml model file; throws if it can't be read. */
        fun load(file: File): WhisperModel {
            val handle = WhisperNative.init(file.absolutePath)
            require(handle != 0L) { "Couldn't load ${file.name}" }
            return WhisperModel(handle, file)
        }
    }
}
