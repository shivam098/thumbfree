package app.thumbfree.core

/**
 * The live transcript while recording. Each update re-transcribes the audio after the last committed point; once that
 * exceeds the window, the oldest part is transcribed once, cut at a pause, and kept, so an update never costs more than
 * one window of audio. Port of VoiceDictation.Core.DictationPreview; runs on one worker thread.
 *
 * @param transcribe cleaned text for a piece of audio ("" when nothing was said); null means "stopped, give up".
 */
class LivePreview(
    private val transcribe: (FloatArray) -> String?,
    private val windowSamples: Int = SpeechAudio.SAMPLE_RATE * 10,
    minCommitSamples: Int = SpeechAudio.SAMPLE_RATE * 3,
) {
    private val minCommit = minOf(minCommitSamples, windowSamples)
    private val committed = mutableListOf<String>()

    /** Samples from the start of the recording that are already transcribed and kept. */
    var committedSamples = 0
        private set

    /** Transcribes [pending] (the audio after [committedSamples]); null if transcription stopped. */
    fun update(pending: FloatArray): String? {
        var rest = pending
        var speech = SpeechAudio.speech(rest)
        while (rest.size > windowSamples) {
            val cut = commitPoint(speech, rest.size, windowSamples, minCommit)
            if (speech.any { it.start < cut }) {
                val text = transcribe(rest.copyOfRange(0, cut)) ?: return null
                if (text.isNotEmpty()) committed += text
            }
            committedSamples += cut
            rest = rest.copyOfRange(cut, rest.size)
            speech = SpeechAudio.speech(rest)
        }

        val tentative = if (speech.isEmpty()) "" else transcribe(rest) ?: return null
        return (committed + tentative).filter { it.isNotEmpty() }.joinToString(" ")
    }

    companion object {
        /**
         * Where to cut audio longer than the window: in the latest pause between [minSamples] and [windowSamples],
         * else the window edge. Audio with no speech is dropped except its last second.
         */
        fun commitPoint(speech: List<Span>, length: Int, windowSamples: Int, minSamples: Int): Int {
            if (speech.isEmpty()) return maxOf(1, length - SpeechAudio.SAMPLE_RATE)
            val regions = speech.sortedBy { it.start }
            val candidates = regions.zipWithNext { a, b -> (a.end + b.start) / 2 }.toMutableList()
            if (regions.last().end < length) candidates += minOf(length, regions.last().end + SpeechAudio.SAMPLE_RATE / 5)
            val best = candidates.filter { it in minSamples..windowSamples }.maxOrNull() ?: 0
            return if (best > 0) best else windowSamples
        }
    }
}
