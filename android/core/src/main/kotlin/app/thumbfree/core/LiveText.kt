package app.thumbfree.core

/**
 * Text that arrives while you speak (live typing). A streaming recognizer gives each phrase as it ends ([finished])
 * plus the words of the phrase in progress ([partial]).
 */
object LiveText {
    private val TrailingMark = Regex("""[.,?!;:]+$""")
    private val Ends = setOf('.', '?', '!')

    /**
     * Takes only the punctuation from [punctuated] (the punctuation model's output) and keeps the recognizer's own
     * words and capitals ([original]); the model re-cases words badly ("YOUTUBE"). Sentence starts are capitalized.
     * If the two don't have the same words, [original] is returned unchanged.
     */
    fun mergePunctuation(original: String, punctuated: String): String {
        val words = original.trim().split(Regex("\\s+")).filter { it.isNotEmpty() }
        val marked = punctuated.trim().split(Regex("\\s+")).filter { it.isNotEmpty() }
        if (words.isEmpty() || words.size != marked.size) return original.trim()
        val out = StringBuilder()
        var sentenceStart = true
        for (i in words.indices) {
            val bare = TrailingMark.replace(words[i], "")
            if (!bare.equals(TrailingMark.replace(marked[i], ""), ignoreCase = true)) return original.trim()
            val mark = TrailingMark.find(marked[i])?.value.orEmpty()
            val word = if (sentenceStart && bare.isNotEmpty() && bare[0].isLowerCase()) bare[0].uppercaseChar() + bare.substring(1) else bare
            if (out.isNotEmpty()) out.append(' ')
            out.append(word).append(mark)
            sentenceStart = mark.lastOrNull() in Ends
        }
        return out.toString()
    }

    /** The text so far: finished phrases, then the phrase in progress. */
    fun join(finished: List<String>, partial: String): String =
        (finished + partial.trim()).filter { it.isNotEmpty() }.joinToString(" ")
}
