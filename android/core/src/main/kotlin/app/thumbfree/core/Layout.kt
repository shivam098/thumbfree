package app.thumbfree.core

/** Where dictated text is going, for [Layout]. */
data class LayoutContext(
    /** The field accepts line breaks (lists and emails need them). */
    val multiLine: Boolean,
    /** Only whitespace before the cursor: the dictation starts the text (needed for email layout). */
    val atStart: Boolean,
    /** An email app or an email body field: a greeting or a sign-off alone is enough for email layout. */
    val email: Boolean = false,
)

/**
 * Shapes formatted dictation the way it would be typed: an email gets its greeting and sign-off on their own lines,
 * a spoken list becomes a bulleted or numbered list, and a corrected time or day keeps only the correction
 * ("Friday at 3 pm… actually 4 pm" → "Friday at 4 pm"). Runs after [DictationFormatter]; conservative by design, so
 * ordinary sentences come back unchanged.
 */
object Layout {
    fun apply(text: String, context: LayoutContext): String {
        val corrected = SelfCorrection.apply(text)
        if (!context.multiLine || corrected.contains('\n')) return corrected
        return (if (context.atStart) email(corrected, context.email) else null)
            ?: list(corrected)
            ?: corrected
    }

    // ---- email ----

    private const val NAME_WORD = """[\p{L}][\p{L}'’.-]*"""
    private const val CAP_NAME_WORD = """\p{Lu}[\p{L}'’.-]*"""

    private val GreetingRegex = Regex(
        """^((?i:hi|hello|hey|dear|good morning|good afternoon|good evening|greetings)(?:\s+$NAME_WORD){0,3}?)\s*[,!:—–-]\s*(\S.*)$""",
        RegexOption.DOT_MATCHES_ALL,
    )

    private val SignOffRegex = Regex(
        """(?:^|(?<=[.!?])\s+)((?i:thanks(?: again| so much| a lot)?|thank you(?: so much| again)?|many thanks|regards|best regards|kind regards|warm regards|warmly|best|best wishes|all the best|cheers|sincerely|yours sincerely|yours truly|take care|talk soon))(?:\s*[,.!])?(?:\s+($CAP_NAME_WORD(?:\s+$CAP_NAME_WORD){0,2}))?\s*[.!]?\s*$""",
    )

    /** Null unless the text looks like an email (see [LayoutContext.email]). */
    private fun email(text: String, emailApp: Boolean): String? {
        val greeting = GreetingRegex.find(text)
        var body = greeting?.groupValues?.get(2) ?: text
        val signOff = SignOffRegex.find(body)
        val signName = signOff?.groupValues?.get(2).orEmpty().trimEnd('.')
        val looksLikeEmail = if (emailApp) greeting != null || signOff != null else greeting != null && signName.isNotEmpty()
        if (!looksLikeEmail) return null

        val parts = mutableListOf<String>()
        greeting?.let { parts += it.groupValues[1].trim() + "," }
        if (signOff != null) body = body.substring(0, signOff.range.first)
        body = body.trim()
        if (body.isNotEmpty()) parts += capitalize(body)
        if (signOff != null) {
            val phrase = capitalize(signOff.groupValues[1].trim())
            parts += if (signName.isNotEmpty()) "$phrase,\n$signName" else phrase + (signOff.value.trim().lastOrNull()?.takeIf { it == '!' || it == '.' } ?: '.')
        }
        if (parts.size < 2) return null
        return parts.joinToString("\n\n")
    }

    // ---- lists ----

    // Only unambiguous commands: "bullet point …", "new/next bullet …", or "bullet …" opening the text. Never "bite the
    // bullet", "bullet train" or "bullet-proof".
    private val BulletRegex = Regex(
        """(?:[,.;:]?\s*(?<![\p{L}])(?i:(?:new |next |another )?bullet point|(?:new|next|another) bullet)|^(?i:bullet))(?![\p{L}-])[,.;:]?\s*""",
    )

    private val OrdinalRegex = Regex(
        """(?:^|(?<=[.,;:!?])\s*(?:(?i:and)\s+)?|\s+(?i:and)\s+)(?<![\p{L}])((?i:first(?:ly)?|second(?:ly)?|third(?:ly)?|fourth|fifth|sixth|seventh|eighth|ninth|tenth|finally|lastly))(?![\p{L}])(?:\s*[,:])?\s+""",
    )
    private val OrdinalOrder = listOf("first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth")

    private val CueRegex = Regex(
        """^(?i:(?:my|our|the|a|your|today's|tomorrow's|this week's|here's (?:my|the|a|our)|here is (?:my|the|a|our))\s+)?(?i:to[- ]?dos?|things (?:to|I need to|we need to|I have to|we have to)|shopping|grocer(?:y|ies)|checklist|agenda|action items?|tasks?|reminders?|list|need to buy|steps|ingredients|packing)(?![\p{L}])""",
    )
    private val CopulaRegex = Regex("""(?<![\p{L}])(?i:is|was|are|were|seems|seemed|looks|looked|felt)(?![\p{L}])""")
    private val ItemSplitRegex = Regex("""\s*(?:[,;]|\.(?=\s|$))\s*""")
    private val LeadingJoinerRegex = Regex("""^(?i:and|then|also|and then|plus)\s+""")
    private val AndRegex = Regex("""\s+(?i:and)\s+""")

    /** Null unless the text is a spoken list. */
    private fun list(text: String): String? = bullets(text) ?: ordinals(text) ?: commaList(text)

    /** "Shopping list bullet point milk bullet point eggs" → "Shopping list:\n- Milk.\n- Eggs." */
    private fun bullets(text: String): String? {
        val parts = BulletRegex.split(text)
        if (parts.size < 2) return null
        val items = parts.drop(1).map { item(it) }.filter { it.isNotEmpty() }
        if (items.size < 2) return null
        return render(header(parts[0]), items.map { "- $it" })
    }

    /** "My plan: first, book flights, second, find a hotel, and third, pack" → a numbered list. */
    private fun ordinals(text: String): String? {
        val marks = OrdinalRegex.findAll(text).toList()
        if (marks.size < 3) return null
        val words = marks.map { it.groupValues[1].lowercase().removeSuffix("ly") }
        if (words[0] != "first") return null
        for (k in 1 until words.size) {
            val ok = words[k] == OrdinalOrder.getOrNull(k) || (k == words.size - 1 && (words[k] == "final" || words[k] == "last"))
            if (!ok) return null
        }
        val items = marks.indices.map { k ->
            val end = if (k + 1 < marks.size) marks[k + 1].range.first else text.length
            item(text.substring(marks[k].range.last + 1, end))
        }
        if (items.any { it.isEmpty() || words(it) > 20 }) return null
        return render(header(text.substring(0, marks[0].range.first)), items.mapIndexed { k, it -> "${k + 1}. $it" })
    }

    /** "Things to do today: order groceries, go on a walk and call mom" → a bulleted list. */
    private fun commaList(text: String): String? {
        val colon = text.indexOf(':').takeIf { it in 1..100 }
        val (head, rest) = if (colon != null) {
            text.substring(0, colon) to text.substring(colon + 1)
        } else {
            val cut = ItemSplitRegex.find(text) ?: return null
            text.substring(0, cut.range.first) to text.substring(cut.range.last + 1)
        }
        if (words(head) > 10 || !CueRegex.containsMatchIn(head.trim()) || CopulaRegex.containsMatchIn(head)) return null

        val pieces = ItemSplitRegex.split(rest.trim()).map { it.trim() }.filter { it.isNotEmpty() }.toMutableList()
        if (pieces.isEmpty()) return null
        // "go on a walk and call mom" at the end of the list: two items when both sides are real phrases.
        val last = pieces.last()
        val and = AndRegex.findAll(last).lastOrNull()
        if (and != null && !LeadingJoinerRegex.containsMatchIn(last)) {
            val left = last.substring(0, and.range.first)
            val right = last.substring(and.range.last + 1)
            if (words(left) >= 2 && words(right) >= 2) {
                pieces[pieces.size - 1] = left
                pieces += right
            }
        }
        val items = pieces.map { item(it) }.filter { it.isNotEmpty() }
        // With a colon two items make a list; without one, three (so "Tasks, then lunch" stays a sentence).
        if (items.size < (if (colon != null) 2 else 3) || items.any { words(it) > 12 }) return null
        return render(header(head), items.map { "- $it" })
    }

    private fun render(header: String, lines: List<String>) =
        (if (header.isEmpty()) "" else "$header\n") + lines.joinToString("\n")

    private fun header(raw: String): String {
        val h = raw.trim().trimEnd(',', '.', ';', ':', ' ').trim()
        return if (h.isEmpty()) "" else capitalize(h) + ":"
    }

    private fun item(raw: String): String {
        var s = raw.trim().trimEnd(',', ';', ':', ' ')
        s = LeadingJoinerRegex.replace(s, "")
        s = s.trimEnd('.', ',', ';', ' ')
        if (s.isEmpty()) return ""
        s = capitalize(s)
        return if (s.last() in "!?") s else "$s."
    }

    private fun words(s: String) = s.trim().split(Regex("\\s+")).count { it.isNotEmpty() }

    private fun capitalize(s: String) = if (s.isNotEmpty() && s[0].isLowerCase()) s[0].uppercaseChar() + s.substring(1) else s
}

/**
 * "at 3 pm… actually 4 pm" → "at 4 pm", "on Monday, sorry, Tuesday" → "on Tuesday". Only times and days are corrected
 * (and a bare number into a time), where a spoken correction is unambiguous.
 */
object SelfCorrection {
    private const val WEEKDAYS = "monday|tuesday|wednesday|thursday|friday|saturday|sunday"
    private const val DAY = "$WEEKDAYS|today|tomorrow|tonight"
    private const val TIME = """\d{1,2}(?::\d{2})?(?:\s?(?:[aApP]\.?\s?[mM](?![\p{L}])\.?|o'clock))?"""
    private val ValueRegex = """(?i:$DAY)|$TIME"""

    // Never inside a decimal ("2.5, actually 3"): no digit-and-point just before or after a value.
    private val CorrectionRegex = Regex(
        """(?<![\p{L}\p{Nd}:])(?<!\p{Nd}[.,])($ValueRegex)(\s*(?:\.\.\.|…|[,.;!?—–-])?\s*)((?i:actually|sorry|I mean|no wait|or rather|make (?:it|that)))(?:\s*[,.]|\s*\.\.\.|\s*…)?\s+(?:(?i:at|on|by)\s+)?($ValueRegex)(?![\p{L}\p{Nd}]|[.,]\p{Nd})""",
    )
    private val MeridiemRegex = Regex("""\s?(?:[ap]\.?\s?m\.?|o'clock)$""", RegexOption.IGNORE_CASE)
    private val ClauseEndRegex = Regex("""^\s*(?:[.,!?;:]|$)""")
    private val Days = DAY.split('|').toSet()
    private val Weekdays = WEEKDAYS.split('|').toSet()

    fun apply(text: String): String = CorrectionRegex.replace(text) { m ->
        val first = m.groupValues[1]
        val separator = m.groupValues[2]
        val marker = m.groupValues[3]
        val second = m.groupValues[4]
        // "Friday. Actually, tomorrow I'm busy" starts a new thought; "3 PM. Actually 4 PM?" only corrects the time.
        val newSentence = (separator.any { it in ".!?" } && "..." !in separator && '…' !in separator) ||
            (first.endsWith('.') && marker[0].isUpperCase())
        if (newSentence && !ClauseEndRegex.containsMatchIn(text.substring(m.range.last + 1))) {
            m.value
        } else {
            replacement(first, second, newSentence) ?: m.value
        }
    }

    private fun replacement(first: String, second: String, newSentence: Boolean): String? {
        val a = kind(first)
        val b = kind(second)
        return when {
            a == Kind.Day && b == Kind.Day -> if (second.lowercase() in Weekdays) second.replaceFirstChar { it.uppercaseChar() } else second.lowercase()
            a == Kind.Time && b == Kind.Time -> second
            a == Kind.Number && b == Kind.Time -> second
            // "3 pm, actually 4" → "4 pm"
            a == Kind.Time && b == Kind.Number && MeridiemRegex.containsMatchIn(first) -> second + MeridiemRegex.find(first)!!.value
            // "2, actually 3" only within a sentence ("Chapter 2. Actually 3." is not a correction).
            a == Kind.Number && b == Kind.Number && !newSentence -> second
            else -> null
        }
    }

    private enum class Kind { Day, Time, Number }

    private fun kind(value: String) = when {
        value.lowercase() in Days -> Kind.Day
        value.contains(':') || MeridiemRegex.containsMatchIn(value) -> Kind.Time
        else -> Kind.Number
    }
}
