package app.thumbfree

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Process
import app.thumbfree.core.SpeechAudio

/**
 * 16 kHz mono capture, only between [start] and [stop]. Audio stays in memory and is never written to storage.
 * [onLevel] gets the loudness of each 100 ms chunk (on the capture thread).
 */
class Recorder(private val onLevel: (Float) -> Unit) {
    private val lock = Any()
    private var data = FloatArray(RATE * 20)
    private var size = 0

    @Volatile private var running = false
    private var record: AudioRecord? = null
    private var worker: Thread? = null

    /** Samples recorded so far. */
    val samples: Int get() = synchronized(lock) { size }

    /** True if every sample so far is exactly zero (another app holds the microphone). */
    @Volatile var silentSoFar = true
        private set

    @SuppressLint("MissingPermission") // checked by the caller
    fun start(): Boolean {
        val min = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_FLOAT)
        if (min <= 0) return false
        val rec = try {
            AudioRecord(
                MediaRecorder.AudioSource.VOICE_RECOGNITION, RATE, AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_FLOAT, maxOf(min * 2, RATE * 4 / 2),
            )
        } catch (e: RuntimeException) {
            return false
        }
        if (rec.state != AudioRecord.STATE_INITIALIZED) {
            rec.release()
            return false
        }
        try {
            rec.startRecording()
        } catch (e: IllegalStateException) {
            rec.release()
            return false
        }
        if (rec.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
            rec.release()
            return false
        }

        synchronized(lock) { size = 0 }
        silentSoFar = true
        record = rec
        running = true
        worker = Thread({ capture(rec) }, "microphone").also { it.start() }
        return true
    }

    private fun capture(rec: AudioRecord) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        val chunk = FloatArray(RATE / 10)
        while (running) {
            val n = rec.read(chunk, 0, chunk.size, AudioRecord.READ_BLOCKING)
            if (n < 0) break
            if (n == 0) continue
            synchronized(lock) {
                if (size + n <= MAX_SAMPLES) {
                    if (size + n > data.size) data = data.copyOf(minOf(MAX_SAMPLES, maxOf(data.size * 2, size + n)))
                    System.arraycopy(chunk, 0, data, size, n)
                    size += n
                }
            }
            if (silentSoFar && (0 until n).any { chunk[it] != 0f }) silentSoFar = false
            onLevel(SpeechAudio.rms(chunk, 0, n))
        }
    }

    /** A copy of the audio from [from] to now. */
    fun copy(from: Int = 0): FloatArray = synchronized(lock) { data.copyOfRange(from.coerceIn(0, size), size) }

    /** Stops recording and returns everything recorded. */
    fun stop(): FloatArray {
        running = false
        record?.let {
            try {
                it.stop()
            } catch (_: IllegalStateException) {
            }
        }
        worker?.join(1_000)
        record?.release()
        record = null
        worker = null
        return copy()
    }

    companion object {
        const val RATE = SpeechAudio.SAMPLE_RATE
        const val MAX_SAMPLES = RATE * 60 * 10 // 10 minutes
    }
}
