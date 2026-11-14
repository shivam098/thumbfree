package app.thumbfree.core

/** One dictation in History: when, which app it was for (package name, may be empty) and the text. */
data class HistoryEntry(val time: Long, val app: String, val text: String)

/**
 * History is stored on the device as one line per dictation, `time<TAB>app<TAB>text`, with backslash, tab and line
 * breaks escaped. Kept in the core so every platform reads and prunes it the same way.
 */
object History {
    /** Entries older than this are removed. */
    const val MAX_AGE_MS = 30L * 24 * 60 * 60 * 1000

    /** At most this many are kept (the newest). */
    const val MAX_ENTRIES = 500

    fun encode(entry: HistoryEntry): String = "${entry.time}\t${escape(entry.app)}\t${escape(entry.text)}"

    /** Null for a damaged line (it's skipped, never fatal). */
    fun decode(line: String): HistoryEntry? {
        val parts = line.split('\t')
        if (parts.size != 3) return null
        val time = parts[0].toLongOrNull() ?: return null
        val text = unescape(parts[2])
        return if (text.isBlank()) null else HistoryEntry(time, unescape(parts[1]), text)
    }

    /** Newest first, without expired entries, at most [MAX_ENTRIES]. */
    fun prune(entries: List<HistoryEntry>, now: Long): List<HistoryEntry> =
        entries.filter { now - it.time <= MAX_AGE_MS }.sortedByDescending { it.time }.take(MAX_ENTRIES)

    /** Entries whose text contains every word of [query] (case-insensitive). */
    fun search(entries: List<HistoryEntry>, query: String): List<HistoryEntry> {
        val words = query.trim().lowercase().split(Regex("\\s+")).filter { it.isNotEmpty() }
        if (words.isEmpty()) return entries
        return entries.filter { e -> val t = e.text.lowercase(); words.all { it in t } }
    }

    private fun escape(s: String) = buildString {
        for (c in s) when (c) {
            '\\' -> append("\\\\")
            '\t' -> append("\\t")
            '\n' -> append("\\n")
            '\r' -> append("\\r")
            else -> append(c)
        }
    }

    private fun unescape(s: String) = buildString {
        var i = 0
        while (i < s.length) {
            val c = s[i]
            if (c == '\\' && i + 1 < s.length) {
                append(when (s[i + 1]) { 't' -> '\t'; 'n' -> '\n'; 'r' -> '\r'; else -> s[i + 1] })
                i += 2
            } else {
                append(c)
                i++
            }
        }
    }
}
