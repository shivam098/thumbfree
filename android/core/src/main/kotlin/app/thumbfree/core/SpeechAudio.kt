package app.thumbfree.core

import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

/** A span of audio, in samples: [start, end). */
data class Span(val start: Int, val end: Int) {
    val length get() = end - start
}

/**
 * Light speech detection for a phone: 20 ms frames, an adaptive noise floor, and a few rules. It's used to skip
 * transcription when nothing was said (saves battery and avoids Whisper's “Thank you.” on silence), to trim silence
 * before decoding, and to cut long dictation at pauses into pieces the recognizer handles well.
 */
object SpeechAudio {
    const val SAMPLE_RATE = 16_000
    private const val FRAME = SAMPLE_RATE / 50 // 20 ms
    private const val MIN_THRESHOLD = 0.006f // about −44 dBFS
    private const val LOUD = 0.02f // about −34 dBFS: steady sound this loud is someone talking
    private const val MERGE_GAP_FRAMES = 15 // 300 ms
    private const val MIN_SPEECH_FRAMES = 6 // 120 ms

    /** Speech regions, in samples. Gaps shorter than 300 ms are merged; blips shorter than 120 ms are dropped. */
    fun speech(audio: FloatArray): List<Span> {
        val frames = audio.size / FRAME
        if (frames == 0) return emptyList()
        val energy = FloatArray(frames) { f -> rms(audio, f * FRAME, FRAME) }
        val sorted = energy.sortedArray()
        val floor = sorted[(frames * 0.1).toInt().coerceAtMost(frames - 1)]
        val peak = sorted[(frames * 0.95).toInt().coerceAtMost(frames - 1)]
        val threshold = when {
            peak < MIN_THRESHOLD -> return emptyList() // nothing but faint noise
            peak >= floor * 4 -> max(MIN_THRESHOLD, floor * 3f) // speech stands out from the background
            peak >= LOUD -> max(MIN_THRESHOLD, peak * 0.3f) // steady and loud: talking almost all the time
            else -> return emptyList() // steady and quiet: background noise only
        }

        val raw = mutableListOf<IntArray>()
        var f = 0
        while (f < frames) {
            if (energy[f] < threshold) {
                f++
                continue
            }
            val start = f
            while (f < frames && energy[f] >= threshold) f++
            raw += intArrayOf(start, f)
        }

        val merged = mutableListOf<IntArray>()
        for (r in raw) {
            val last = merged.lastOrNull()
            if (last != null && r[0] - last[1] < MERGE_GAP_FRAMES) last[1] = r[1] else merged += r
        }

        return merged.filter { it[1] - it[0] >= MIN_SPEECH_FRAMES }.map { Span(it[0] * FRAME, min(audio.size, it[1] * FRAME)) }
    }

    fun hasSpeech(audio: FloatArray) = speech(audio).isNotEmpty()

    /** The audio from just before the first speech to just after the last (250 ms margins); null if no speech. */
    fun trim(audio: FloatArray, regions: List<Span> = speech(audio)): Span? {
        if (regions.isEmpty()) return null
        val margin = SAMPLE_RATE / 4
        return Span(max(0, regions.first().start - margin), min(audio.size, regions.last().end + margin))
    }

    /**
     * Cuts [span] into pieces of at most [maxSamples], each cut in the middle of the longest pause available after
     * [minSamples] (or at the limit when there is none), so words aren't split.
     */
    fun split(span: Span, regions: List<Span>, maxSamples: Int, minSamples: Int = SAMPLE_RATE * 3): List<Span> {
        val pieces = mutableListOf<Span>()
        var start = span.start
        while (span.end - start > maxSamples) {
            val limit = start + maxSamples
            val earliest = start + min(minSamples, maxSamples / 2)
            val gaps = regions.zipWithNext { a, b -> Span(a.end, b.start) }
                .filter { it.length > 0 && (it.start + it.end) / 2 in earliest..limit }
            val cut = gaps.maxByOrNull { it.length }?.let { (it.start + it.end) / 2 } ?: limit
            pieces += Span(start, cut)
            start = cut
        }
        if (span.end > start) pieces += Span(start, span.end)
        return pieces
    }

    fun rms(audio: FloatArray, from: Int = 0, count: Int = audio.size - from): Float {
        if (count <= 0) return 0f
        var sum = 0.0
        for (i in from until from + count) sum += audio[i] * audio[i]
        return sqrt(sum / count).toFloat()
    }
}
