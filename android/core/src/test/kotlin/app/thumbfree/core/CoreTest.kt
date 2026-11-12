package app.thumbfree.core

import kotlin.math.PI
import kotlin.math.sin
import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CoreTest {
    private val rate = SpeechAudio.SAMPLE_RATE

    /** [segments] of (seconds, speech?): a 220 Hz tone for speech, faint noise for silence. */
    private fun audio(vararg segments: Pair<Double, Boolean>): FloatArray {
        val rnd = Random(1)
        val out = ArrayList<Float>()
        for ((seconds, speech) in segments) {
            repeat((seconds * rate).toInt()) { i ->
                out += if (speech) (0.2 * sin(2 * PI * 220 * i / rate)).toFloat() else (rnd.nextFloat() - 0.5f) * 0.002f
            }
        }
        return out.toFloatArray()
    }

    @Test
    fun silence_has_no_speech() {
        assertFalse(SpeechAudio.hasSpeech(audio(3.0 to false)))
        assertNull(SpeechAudio.trim(audio(3.0 to false)))
    }

    @Test
    fun a_steady_hum_is_not_speech_but_steady_talking_is() {
        val hum = FloatArray(3 * rate) { i -> (0.01 * sin(2 * PI * 50 * i / rate)).toFloat() }
        assertFalse(SpeechAudio.hasSpeech(hum))
        assertEquals(1, SpeechAudio.speech(audio(4.0 to true)).size)
    }

    @Test
    fun speech_is_found_and_short_pauses_are_merged() {
        val a = audio(0.5 to false, 1.0 to true, 0.2 to false, 1.0 to true, 1.0 to false, 0.8 to true, 0.5 to false)
        val regions = SpeechAudio.speech(a)
        assertEquals(2, regions.size, "$regions")
        assertEquals(0.5, regions[0].start.toDouble() / rate, 0.03)
        assertEquals(2.7, regions[0].end.toDouble() / rate, 0.03)
        val trimmed = SpeechAudio.trim(a, regions)!!
        assertEquals(0.25, trimmed.start.toDouble() / rate, 0.03)
    }

    @Test
    fun long_dictation_is_cut_at_pauses() {
        // 10 s speech, 1 s pause, 10 s speech, 1 s pause, 5 s speech; pieces of at most 14 s.
        val a = audio(10.0 to true, 1.0 to false, 10.0 to true, 1.0 to false, 5.0 to true)
        val regions = SpeechAudio.speech(a)
        val pieces = SpeechAudio.split(Span(0, a.size), regions, Decoding.MAX_PIECE_SAMPLES)
        assertEquals(3, pieces.size, "$pieces")
        assertEquals(10.5, pieces[0].end.toDouble() / rate, 0.05)
        assertEquals(21.5, pieces[1].end.toDouble() / rate, 0.05)
        assertTrue(pieces.all { it.length <= Decoding.MAX_PIECE_SAMPLES })
        assertEquals(a.size, pieces.last().end)
    }

    @Test
    fun speech_without_pauses_is_cut_at_the_limit() {
        val a = audio(30.0 to true)
        val pieces = SpeechAudio.split(Span(0, a.size), SpeechAudio.speech(a), Decoding.MAX_PIECE_SAMPLES)
        assertEquals(listOf(Span(0, 14 * rate), Span(14 * rate, 28 * rate), Span(28 * rate, 30 * rate)), pieces)
    }

    @Test
    fun live_preview_commits_old_audio_and_keeps_its_text() {
        val calls = mutableListOf<Int>()
        val preview = LivePreview({ calls += it.size; "w${calls.size}" }, windowSamples = 10 * rate, minCommitSamples = 3 * rate)
        val a = audio(6.0 to true, 1.0 to false, 6.0 to true)
        assertEquals("w1 w2", preview.update(a))
        assertEquals(6.5, preview.committedSamples.toDouble() / rate, 0.05)
        assertEquals("w1 w3", preview.update(a.copyOfRange(preview.committedSamples, a.size)))
    }

    @Test
    fun live_preview_skips_silence_and_stops_when_asked() {
        var called = false
        assertEquals("", LivePreview({ called = true; "x" }).update(audio(2.0 to false)))
        assertFalse(called)
        assertNull(LivePreview({ null }).update(audio(2.0 to true)))
    }

    @Test
    fun password_and_search_fields_are_recognised() {
        assertTrue(FieldInfo.of(0x1 or 0x80, 0).password) // text password
        assertTrue(FieldInfo.of(0x1 or 0xe0, 0).password) // web password
        assertTrue(FieldInfo.of(0x2 or 0x10, 0).password) // number password (PIN)
        assertFalse(FieldInfo.of(0x1 or 0x10, 0).password) // a URI is not a password
        assertTrue(FieldInfo.of(0x1 or 0x10, 0).search)
        assertTrue(FieldInfo.of(0x1, 3).search) // IME_ACTION_SEARCH
        assertTrue(FieldInfo.of(0x1 or 0x20000, 0).multiLine)
        assertFalse(FieldInfo.of(0x1, 4).multiLine)
        assertTrue(FieldInfo.of(0x1, 0x1000000).private)
    }

    @Test
    fun dictation_joins_the_text_already_there() {
        assertEquals(" and then", Insertion.withSpacing("We shipped it", "and then"))
        assertEquals("Hello", Insertion.withSpacing("", "Hello"))
        assertEquals("Hello", Insertion.withSpacing("Hi ", "Hello"))
        assertEquals(", right?", Insertion.withSpacing("ok", ", right?"))
        assertEquals("mail", Insertion.withSpacing("@", "mail"))
        assertTrue(Insertion.continuesSentence("We shipped it"))
        assertFalse(Insertion.continuesSentence("We shipped it. "))
        assertFalse(Insertion.continuesSentence(""))
        assertFalse(Insertion.continuesSentence("Dear John,\n"))
        assertEquals("a b", Insertion.forField("a\nb", FieldInfo(false, false, false, false)))
        assertEquals("a\nb", Insertion.forField("a\nb", FieldInfo(false, false, true, false)))
    }

    @Test
    fun personal_dictionary_lines_become_replacements() {
        val map = PersonalDictionary.parse("git hub = GitHub\n\nno equals sign\n = empty heard\nsig = Thanks,\\nShivam\n")
        assertEquals(mapOf("git hub" to "GitHub", "sig" to "Thanks,\nShivam"), map)
        val formatter = DictationFormatter(DictationOptions(replacements = map))
        assertEquals("Push to GitHub.", formatter.format("push to git hub."))
    }

    @Test
    fun short_known_hallucinations_are_dropped() {
        assertTrue(Hallucinations.isLikely("Thanks for watching!", rate))
        assertFalse(Hallucinations.isLikely("Thanks for watching!", 4 * rate))
        assertFalse(Hallucinations.isLikely("Send it", rate))
    }

    @Test
    fun decoding_settings_suit_a_phone() {
        assertEquals(256, Decoding.previewContext(rate))
        assertEquals(576, Decoding.previewContext(10 * rate))
        assertEquals(1500, Decoding.previewContext(40 * rate))
        assertEquals(4, Decoding.threads(8))
        assertEquals(2, Decoding.threads(2))
        assertEquals(ModelCatalog.Hinglish, ModelCatalog.forLanguage(Language.Hinglish, accurateEnglish = true))
        assertEquals(ModelCatalog.EnglishAccurate, ModelCatalog.forLanguage(Language.English, accurateEnglish = true))
    }
}
