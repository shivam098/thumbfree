package app.thumbfree.core

import java.util.Locale

data class DictationOptions(
    val removeFillerWords: Boolean = true,
    /** Convert spoken “comma”, “question mark”, “new line”… into symbols. */
    val spokenPunctuation: Boolean = true,
    /** Whole-word, case-insensitive fixes (“git hub” → “GitHub”). */
    val replacements: Map<String, String> = emptyMap(),
    /** Saying the trigger (or “insert <trigger>”) as the whole phrase inserts the expansion instead. */
    val snippets: Map<String, String> = emptyMap(),
)

/**
 * Local, rule-based clean-up of dictated text: filler words (English and Hinglish), spoken punctuation, inline
 * “scratch that”, personal replacements, snippets and capitalisation. May return text containing '\n'.
 *
 * A port of VoiceDictation.Core.DictationFormatter (C#). `tests/vectors/formatter.tsv` holds the C# outputs and the
 * tests require identical results. Regexes avoid engine-specific shorthands (`\w`, `\b`, unbounded look-behind) so they
 * behave the same on the JVM and on Android's ICU regex engine.
 */
class DictationFormatter(private val options: DictationOptions = DictationOptions()) {
    private val replacements: List<Pair<Regex, String>> = options.replacements
        .filterKeys { it.isNotBlank() }
        .entries
        .sortedByDescending { it.key.length }
        .map { Regex("(?<!$W)" + Regex.escape(it.key.trim()) + "(?!$W)", RegexOption.IGNORE_CASE) to it.value }

    private val snippets: Map<String, String> = LinkedHashMap<String, String>().also { map ->
        for ((key, value) in options.snippets) {
            if (key.isBlank()) continue
            map.putIfAbsent(TranscriptText.normalize(key), value)
        }
    }

    /**
     * @param continuesSentence the text before the cursor doesn't end a sentence, so don't start with a capital.
     * @param searchField typing into a search or address box: drop a trailing full stop.
     */
    fun format(transcript: String, continuesSentence: Boolean = false, searchField: Boolean = false): String {
        var text = TranscriptText.clean(transcript)
        if (text.isEmpty()) return ""

        expandSnippet(text)?.let { return it }

        if (options.removeFillerWords) text = removeFillers(text)
        text = applyScratchThat(text)
        if (options.spokenPunctuation) text = applySpokenPunctuation(text)
        for ((pattern, replacement) in replacements) {
            text = pattern.replace(text, Regex.escapeReplacement(replacement))
        }

        text = tidy(text)
        if (text.isEmpty()) return ""

        text = if (continuesSentence) lowercaseContinuation(text) else text[0].uppercaseChar() + text.substring(1)
        if (searchField) text = text.trimEnd('.', ' ')
        return text
    }

    private fun expandSnippet(text: String): String? {
        var key = TranscriptText.normalize(text)
        if (key.startsWith("insert ")) key = key.substring("insert ".length)
        return snippets[key]
    }

    companion object {
        // .NET's \w is [\p{L}\p{Mn}\p{Nd}\p{Pc}]; spelled out so every engine agrees (Mn matters for Devanagari).
        private const val WC = """\p{L}\p{Mn}\p{Nd}\p{Pc}"""
        private const val W = "[$WC]"

        // An exact .NET \b: a word character on exactly one side.
        private const val BND = "(?:(?<=$W)(?!$W)|(?<!$W)(?=$W))"

        // Look-behinds must be bounded on Android; runs of spaces are short by this point.
        private const val SP = """\s{1,16}"""

        private val ContinuationWords = setOf(
            "a", "an", "and", "as", "at", "but", "by", "for", "from", "if", "in", "into", "is", "it", "of", "on", "or",
            "so", "than", "that", "the", "then", "this", "to", "was", "when", "where", "which", "while", "with", "because",
            "before", "after", "about", "are", "be", "can", "could", "should", "would", "will", "we", "you", "they", "he",
            "she", "my", "our", "your", "their", "its", "not", "also", "just", "maybe", "please", "there", "what", "how",
        )

        // Words that end a clause; “… mein thik hai na?” is a tag, “Papa ki tabiyat thik hai na?” a real question.
        private val ClauseEnders = setOf(
            "hai", "hain", "ho", "hoga", "hogi", "honge", "tha", "thi", "the", "hoon", "hu", "hun", "chahiye", "chahie",
            "chaahie", "chaahiye", "mein", "me", "par", "pe", "ko", "se", "karna", "karo", "karenge", "karunga", "karungi",
            "kar", "dena", "lena", "do", "lo", "jao", "aana", "jaana", "milenge", "milte", "sakte", "sakta", "sakti",
        )

        private val ProtectingWords = setOf("sab", "kya", "bilkul", "ekdum", "bhi", "kuch")

        private val SpokenSymbolWords = setOf(
            "new", "line", "paragraph", "comma", "full", "stop", "question", "exclamation", "mark", "point", "colon",
            "semicolon", "period",
        )

        private val NumberWords = setOf(
            "zero", "oh", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "double", "triple",
        )

        private val I = setOf(RegexOption.IGNORE_CASE)

        private val FillerRegex = Regex("""(?<![$WC'-])(?:u+m+|u+h+|uhm|e+r+m+|e+r|a+h+|h+m+|m+h*m+)(?![$WC'-])[,.]?""", I)

        // Fillers that may open a sentence or clause. “to/toh” only counts inside a run that doesn't end with it, so
        // “Ok to proceed” and “To do that” stay.
        private const val OpeningFiller = """(?:haan|han|haa|so|ok|okay|ach+ha|acha|accha|achcha|th(?:i|ee)k\s+hai|matlab|chalo|alright|like)"""

        // “thik hai na” as a tag; group 1 is the punctuation after it.
        private val TagRegex = Regex("""$BND(?:th(?:i|ee|ea)k|thick)\s+(?:hai|he|hain)\s+(?:na+|nah)(?![\p{L}'])\s*([?.!,]?)""", I)
        private val PreviousWordRegex = Regex("""\p{L}+$""")
        private val CommaTagRegex = Regex(""",\s*(?:th(?:i|ee|ea)k|thick)\s+(?:hai|he)\s*\?""", I)
        private val BasicallyRegex = Regex(""",?\s*${BND}basically$BND,?""", I)
        private val YouKnowRegex = Regex(""",\s*you know\s*(?=[,.!?]|$)|(?<=^|[.!?]$SP|,$SP)(?:you know|i mean),\s*""", I)
        private val OpeningRunRegex = Regex(
            """(?<=^|[.!?]$SP|,$SP|\n)(?:(?:$OpeningFiller|toh?)(?![$WC'-]),?\s+)+$OpeningFiller(?![$WC'-]),?\s+(?=[\p{L}\p{N}])(?!toh?$BND|i\s+(?:said|told|mentioned|meant|was\s+saying)$BND)""",
            I,
        )
        private val LikeBeforeExampleRegex = Regex("""(?<=^|[.!?]$SP|,$SP)like,?\s+(?=for example$BND|jaise$BND)""", I)

        // The same 2–4 words said twice in a row (“for example, for example”). Single words are left alone: Hindi
        // doubles words on purpose (“dheere dheere”).
        private val RepeatedPhraseRegex = Regex("""$BND((?:[\p{L}\p{N}']+\s+){1,3}[\p{L}\p{N}']+),?\s+\1$BND""", I)
        private val SentenceSplitRegex = Regex("""(?<=[.!?])[ \t]+""")
        private val FillerSentenceRegex = Regex("""^(?:(?:ok|okay|th(?:i|ee)k\s+hai(?:\s+na)?|ach+ha|acha|accha|achcha|hmm+|alright|chalo)[\s,]*)+[.!?]*$""", I)

        private val ScratchRegex = Regex("""[,.;]?\s*${BND}scratch that$BND[,.!]?""", I)
        private val NewParagraphRegex = Regex("""[,.]?\s*${BND}new paragraph$BND[,.]?\s*""", I)
        private val NewLineRegex = Regex("""[,.]?\s*${BND}new line$BND[,.]?\s*""", I)
        private val SymbolRegex = Regex("""[,.]?\s*$BND(comma|full stop|question mark|exclamation mark|exclamation point|colon|semicolon)$BND[,.]?""", I)
        private val TrailingPeriodRegex = Regex("""[,]?\s*${BND}period[.!?]?\s*$""", I)
        private val SpacesRegex = Regex("""[ \t]{2,}""")
        private val SpaceBeforePunctuationRegex = Regex("""[ \t]+([,.;:!?])""")
        private val DoublePunctuationRegex = Regex("""([,.;:!?])[,.;:]+""")
        private val SpaceAroundNewlineRegex = Regex("""[ \t]*\n[ \t]*""")
        private val SentenceStartRegex = Regex("""((?:^|[.!?]\s+|\n))(\p{Ll})""")
        private val FirstWordRegex = Regex("""^[\p{L}']+""")

        /**
         * Drops sounds and discourse fillers that carry no meaning, in English and Hinglish: “um/uh”; tags such as
         * “thik hai na?” and “…, thik hai?”; “basically”; “you know” / “I mean” between commas; a run of two or more
         * fillers opening a sentence or clause (“Haan to like …”, “Okay so …”); “like” before “for example”; a phrase
         * said twice in a row; and sentences that are only fillers (“Ok.”) when something else was said.
         */
        internal fun removeFillers(input: String): String {
            var text = FillerRegex.replace(input, " ")
            text = removeThikHaiNa(text)
            text = CommaTagRegex.replace(text, ". ")
            text = BasicallyRegex.replace(text, " ")
            text = YouKnowRegex.replace(text, "")
            text = SpacesRegex.replace(text, " ").trim()
            text = OpeningRunRegex.replace(text, "")
            text = LikeBeforeExampleRegex.replace(text, "")
            repeat(3) {
                val deduped = RepeatedPhraseRegex.replace(text) { m ->
                    if (isDeliberateRepeat(m.groupValues[1])) m.value else m.groupValues[1]
                }
                if (deduped == text) return@repeat
                text = deduped
            }

            val sentences = SentenceSplitRegex.split(text)
            val kept = sentences.filter { !FillerSentenceRegex.containsMatchIn(it.trim()) }
            return if (kept.isNotEmpty() && kept.size < sentences.size) kept.joinToString(" ") else text
        }

        /**
         * “thik hai na” as a tag: inside a sentence, as a sentence of its own, after a comma, or after a word that
         * ends a clause. Kept when it is the question itself (“Papa ki tabiyat thik hai na?”) or all that was said.
         */
        private fun removeThikHaiNa(text: String): String = TagRegex.replace(text) { m ->
            val before = text.substring(0, m.range.first).trimEnd()
            val prevWord = PreviousWordRegex.find(before)?.value.orEmpty()
            if (prevWord.lowercase(Locale.ROOT) in ProtectingWords) return@replace m.value

            val p = m.groupValues[1]
            val after = text.substring(m.range.last + 1).trimStart()
            if ((p == "" || p == ",") && after.isNotEmpty() && after[0].isLetterOrDigit()) return@replace ", "

            val ownSentence = before.isEmpty() || before.last() in ".!?\n"
            if (ownSentence) {
                return@replace if (before.isEmpty() && after.isEmpty()) m.value else " "
            }

            if (before.last() == ',' || prevWord.lowercase(Locale.ROOT) in ClauseEnders) ". " else m.value
        }

        /** Repeats that are meant: spoken punctuation (“new line new line”) and digits (“1 2 1 2”). */
        private fun isDeliberateRepeat(phrase: String): Boolean =
            phrase.split(' ').filter { it.isNotEmpty() }.all { w ->
                val lower = w.lowercase(Locale.ROOT)
                lower in SpokenSymbolWords || lower in NumberWords || w.all { it.isDigit() }
            }

        /** “Let's meet at 2, scratch that, 3 pm” → “Let's meet at 3 pm”: drops the clause before the marker. */
        private fun applyScratchThat(input: String): String {
            var text = input
            while (true) {
                val match = ScratchRegex.find(text) ?: return text
                val before = text.substring(0, match.range.first).trimEnd()
                val boundary = before.indexOfLast { it in ".!?,;:\n" }
                var kept = if (boundary < 0) "" else before.substring(0, boundary + 1)
                if (kept.endsWith(',')) kept = kept.dropLast(1)
                text = kept + " " + text.substring(match.range.last + 1).trimStart(' ', ',', '.')
            }
        }

        private fun applySpokenPunctuation(input: String): String {
            var text = NewParagraphRegex.replace(input, "\n\n")
            text = NewLineRegex.replace(text, "\n")
            text = SymbolRegex.replace(text) { m ->
                when (m.groupValues[1].lowercase(Locale.ROOT)) {
                    "comma" -> ","
                    "full stop" -> "."
                    "question mark" -> "?"
                    "exclamation mark", "exclamation point" -> "!"
                    "colon" -> ":"
                    "semicolon" -> ";"
                    else -> m.value
                }
            }
            return TrailingPeriodRegex.replace(text, ".")
        }

        private fun tidy(input: String): String {
            var text = SpacesRegex.replace(input, " ")
            text = SpaceBeforePunctuationRegex.replace(text, "$1")
            text = DoublePunctuationRegex.replace(text, "$1")
            text = SpaceAroundNewlineRegex.replace(text, "\n")
            text = text.trim(' ', ',')
            text = text.trimStart('.', ' ')

            // Capitalise the first letter of each sentence and line.
            return SentenceStartRegex.replace(text) { m -> m.groupValues[1] + m.groupValues[2].uppercase(Locale.ROOT) }
        }

        private fun lowercaseContinuation(text: String): String {
            val firstWord = FirstWordRegex.find(text)?.value.orEmpty()
            return if (firstWord.length > 1 && firstWord[0].isUpperCase() && firstWord.substring(1).all { it.isLowerCase() } &&
                firstWord.lowercase(Locale.ROOT) in ContinuationWords
            ) {
                text[0].lowercaseChar() + text.substring(1)
            } else {
                text
            }
        }
    }
}
