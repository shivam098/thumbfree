package app.thumbfree.core

import java.util.Locale

/** Cleaning and comparing raw recognizer output. Port of VoiceDictation.Core.TranscriptText. */
object TranscriptText {
    private val annotation = Regex("""\[[^\]]*\]|\([^)]*\)|\*[^*]*\*""")
    private val whitespace = Regex("""[\s\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+""")

    /** Removes Whisper annotations such as "[BLANK_AUDIO]" or "(music)" and speaker-change dashes. */
    fun clean(transcript: String?): String {
        if (transcript.isNullOrBlank()) return ""
        var text = annotation.replace(transcript, " ")
        text = whitespace.replace(text, " ").trim()
        text = text.trimStart('-', '>', ' ')
        return if (text.any { it.isLetterOrDigit() }) text else ""
    }

    /** Lowercase, punctuation-free form for comparing phrases ("Co-Pilot." -> "copilot"). */
    fun normalize(text: String?): String {
        if (text.isNullOrEmpty()) return ""
        val sb = StringBuilder(text.length)
        for (c in text.lowercase(Locale.ROOT)) {
            when {
                c.isLetterOrDigit() -> sb.append(c)
                c == '-' || c == '\'' || c == '\u2019' -> Unit // join "co-pilot" → "copilot"
                else -> sb.append(' ')
            }
        }
        return whitespace.replace(sb, " ").trim()
    }
}
