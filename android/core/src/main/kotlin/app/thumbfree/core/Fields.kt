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

    private val wordRegex = Regex("""[\p{L}\p{Mn}\p{Nd}][\p{L}\p{Mn}\p{Nd}'’-]*""")

    /**
     * Writes listed words the way they're listed when the recognizer got them nearly right: spacing and case
     * ("git hub co-pilot" → "GitHub Copilot") for any entry, and small misspellings ("Kubirnets" → "Kubernetes") for
     * entries of 8+ letters, at most one edit per 5 letters, same first letter. Never across punctuation, never
     * changing a number ("Windows 10" stays with "Windows 11" listed) and never dropping or adding an ending
     * ("Priyanka's", "Chromebooks"). Short entries are only matched exactly, so "genre" never turns into "genres".
     */
    fun correct(text: String, entries: List<String>): String {
        if (entries.isEmpty() || text.isEmpty()) return text
        val targets = entries.mapNotNull { e -> compact(e).takeIf { it.length >= 3 }?.let { e to it } }
            .sortedByDescending { it.second.length }
        if (targets.isEmpty()) return text
        val words = wordRegex.findAll(text).toList()
        val out = StringBuilder()
        var copied = 0
        var i = 0
        while (i < words.size) {
            // Words from i that are separated only by spaces (a match never swallows punctuation).
            var joinable = 1
            while (i + joinable < words.size && text.substring(words[i + joinable - 1].range.last + 1, words[i + joinable].range.first).isBlank()) {
                joinable++
            }
            var best: Triple<Int, String, Int>? = null // words used, replacement, edits
            for ((entry, target) in targets) {
                val allowed = if (target.length >= 8) target.length / 5 else 0
                val entryWords = entry.trim().split(Regex("\\s+")).size
                for (n in maxOf(1, entryWords - 1)..minOf(entryWords + 2, joinable)) {
                    val heard = compact(words.subList(i, i + n).joinToString(" ") { it.value })
                    if (kotlin.math.abs(heard.length - target.length) > allowed || heard.isEmpty() || heard[0] != target[0]) continue
                    val edits = distance(heard, target, allowed)
                    if (edits > allowed || (edits > 0 && !plausibleTypo(heard, target))) continue
                    if (best == null || edits < best.third || (edits == best.third && n > best.first)) {
                        best = Triple(n, entry.trim(), edits)
                    }
                }
            }
            val match = best
            if (match == null) {
                i++
                continue
            }
            val start = words[i].range.first
            val end = words[i + match.first - 1].range.last + 1
            out.append(text, copied, start).append(match.second)
            copied = end
            i += match.first
        }
        return out.append(text, copied, text.length).toString()
    }

    private fun compact(s: String) = TranscriptText.normalize(s).replace(" ", "")

    private val endings = listOf("s", "es", "ed", "d", "ing")

    /** A misspelling, not a different number or the same word with another ending. */
    private fun plausibleTypo(heard: String, target: String): Boolean {
        if (heard.filter { it.isDigit() } != target.filter { it.isDigit() }) return false
        val (long, short) = if (heard.length >= target.length) heard to target else target to heard
        return !(long.startsWith(short) && long.substring(short.length) in endings)
    }

    /** Levenshtein distance, or limit + 1 once it's certainly above [limit]. */
    private fun distance(a: String, b: String, limit: Int): Int {
        var prev = IntArray(b.length + 1) { it }
        var cur = IntArray(b.length + 1)
        for (x in 1..a.length) {
            cur[0] = x
            var rowMin = cur[0]
            for (y in 1..b.length) {
                cur[y] = minOf(prev[y] + 1, cur[y - 1] + 1, prev[y - 1] + if (a[x - 1] == b[y - 1]) 0 else 1)
                rowMin = minOf(rowMin, cur[y])
            }
            if (rowMin > limit) return limit + 1
            val t = prev
            prev = cur
            cur = t
        }
        return prev[b.length]
    }
}
