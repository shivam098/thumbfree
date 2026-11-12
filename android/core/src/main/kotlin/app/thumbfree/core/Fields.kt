package app.thumbfree.core

/**
 * What kind of text field the keyboard is typing into, from Android's EditorInfo (inputType, imeOptions). The constants
 * mirror android.text.InputType / EditorInfo so this stays plain Kotlin and testable off-device.
 */
data class FieldInfo(
    val password: Boolean,
    /** Search or address boxes: no trailing full stop. */
    val search: Boolean,
    val multiLine: Boolean,
    /** Incognito / no personalised learning: don't keep this text as “last dictation”. */
    val private: Boolean,
) {
    companion object {
        private const val TYPE_MASK_CLASS = 0x0000000f
        private const val TYPE_MASK_VARIATION = 0x00000ff0
        private const val TYPE_CLASS_TEXT = 1
        private const val TYPE_CLASS_NUMBER = 2
        private const val TYPE_TEXT_FLAG_MULTI_LINE = 0x00020000
        private const val TYPE_TEXT_FLAG_IME_MULTI_LINE = 0x00040000
        private const val TYPE_TEXT_VARIATION_URI = 0x10
        private const val TYPE_TEXT_VARIATION_PASSWORD = 0x80
        private const val TYPE_TEXT_VARIATION_VISIBLE_PASSWORD = 0x90
        private const val TYPE_TEXT_VARIATION_FILTER = 0xb0
        private const val TYPE_TEXT_VARIATION_WEB_PASSWORD = 0xe0
        private const val TYPE_NUMBER_VARIATION_PASSWORD = 0x10
        private const val IME_MASK_ACTION = 0xff
        private const val IME_ACTION_GO = 2
        private const val IME_ACTION_SEARCH = 3
        private const val IME_FLAG_NO_PERSONALIZED_LEARNING = 0x1000000

        fun of(inputType: Int, imeOptions: Int): FieldInfo {
            val cls = inputType and TYPE_MASK_CLASS
            val variation = inputType and TYPE_MASK_VARIATION
            val password = when (cls) {
                TYPE_CLASS_TEXT -> variation == TYPE_TEXT_VARIATION_PASSWORD ||
                    variation == TYPE_TEXT_VARIATION_VISIBLE_PASSWORD ||
                    variation == TYPE_TEXT_VARIATION_WEB_PASSWORD
                TYPE_CLASS_NUMBER -> variation == TYPE_NUMBER_VARIATION_PASSWORD
                else -> false
            }
            val action = imeOptions and IME_MASK_ACTION
            val search = action == IME_ACTION_SEARCH || action == IME_ACTION_GO ||
                (cls == TYPE_CLASS_TEXT && (variation == TYPE_TEXT_VARIATION_URI || variation == TYPE_TEXT_VARIATION_FILTER))
            val multiLine = cls == TYPE_CLASS_TEXT && (inputType and (TYPE_TEXT_FLAG_MULTI_LINE or TYPE_TEXT_FLAG_IME_MULTI_LINE)) != 0
            return FieldInfo(password, search, multiLine, (imeOptions and IME_FLAG_NO_PERSONALIZED_LEARNING) != 0)
        }
    }
}

/** How dictated text joins what is already in the field. */
object Insertion {
    /** The text before the cursor doesn't end a sentence (so “And then…” continues as “and then…”). */
    fun continuesSentence(before: CharSequence?): Boolean {
        val trimmed = before?.trimEnd() ?: return false
        return trimmed.isNotEmpty() && trimmed.last() !in ".!?\n" && before.last() != '\n'
    }

    /** The text to commit: a leading space when it would otherwise run into the previous word. */
    fun withSpacing(before: CharSequence?, text: String): String {
        if (text.isEmpty() || before.isNullOrEmpty()) return text
        val last = before.last()
        val needsSpace = !last.isWhitespace() && text[0] !in ",.;:!?)\n" && last !in "([{\"'“‘/@#"
        return if (needsSpace) " $text" else text
    }

    /** Line breaks typed into a single-line box would trigger its action (send/search): use spaces there. */
    fun forField(text: String, field: FieldInfo): String = if (field.multiLine) text else text.replace(Regex(" *\n+ *"), " ")
}

/** Whisper's well-known outputs on silence or noise, dropped for short audio. */
object Hallucinations {
    private val known = setOf("you", "thanks for watching", "thank you for watching", "please subscribe")

    fun isLikely(text: String, audioSamples: Int): Boolean =
        audioSamples < SpeechAudio.SAMPLE_RATE * 5 / 2 && TranscriptText.normalize(text) in known
}

/** The personal dictionary as typed in settings: one “heard = typed” per line; `\n` in “typed” is a line break. */
object PersonalDictionary {
    fun parse(text: String): Map<String, String> {
        val map = LinkedHashMap<String, String>()
        for (line in text.lines()) {
            val eq = line.indexOf('=')
            if (eq <= 0) continue
            val heard = line.substring(0, eq).trim()
            val typed = line.substring(eq + 1).trim().replace("\\n", "\n")
            if (heard.isNotEmpty() && typed.isNotEmpty()) map[heard] = typed
        }
        return map
    }
}

/**
 * "Words I use often" (names, jargon): given to Whisper as a prompt so it spells them the way you do. Whisper can repeat
 * its prompt on near-silent audio, so a transcript made only of list entries is dropped (port of VoiceAssistant's
 * IsPromptEcho, but a single entry on its own is kept: that's indistinguishable from saying the word).
 */
object Vocabulary {
    const val MAX_ENTRIES = 40

    /** One entry per line or comma. */
    fun parse(text: String): List<String> =
        text.split('\n', ',').map { it.trim() }.filter { it.isNotEmpty() }.distinctBy { it.lowercase() }.take(MAX_ENTRIES)

    fun prompt(entries: List<String>): String? = if (entries.isEmpty()) null else entries.joinToString(", ")

    fun isEcho(text: String, entries: List<String>): Boolean {
        val heard = TranscriptText.normalize(text)
        if (heard.isEmpty()) return false
        val normalized = entries.map { TranscriptText.normalize(it) }.filter { it.isNotEmpty() }
        val padded = " $heard "
        if (!" ${normalized.joinToString(" ")} ".contains(padded)) return false
        return normalized.count { padded.contains(" $it ") } >= 2
    }
}
