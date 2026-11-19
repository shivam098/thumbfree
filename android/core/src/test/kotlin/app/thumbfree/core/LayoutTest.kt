package app.thumbfree.core

import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.fail

/** tests/vectors/layout.tsv through the same path as the keyboard: formatter, then layout (also run on a device). */
class LayoutTest {
    @Test
    fun matches_the_layout_vectors() {
        val dir = System.getProperty("thumbfree.vectors") ?: "../../tests/vectors"
        val rows = File(dir, "layout.tsv").readLines(Charsets.UTF_8).filter { it.isNotEmpty() && !it.startsWith("#") }
        check(rows.size > 20) { "expected the layout vectors, found ${rows.size} rows" }
        val failures = rows.mapNotNull { row ->
            val (flags, input, expected) = row.split('\t').also { require(it.size == 3) { "bad row: $row" } }
            val actual = LayoutVectors.run(flags, input)
            if (actual == LayoutVectors.unescape(expected)) null else "  [$flags] $input\n     expected: $expected\n     actual:   ${LayoutVectors.escape(actual)}"
        }
        if (failures.isNotEmpty()) fail("${failures.size} of ${rows.size} layout vectors differ:\n" + failures.joinToString("\n"))
    }

    @Test
    fun profession_words_are_added_once() {
        assertEquals("genres\nGitHub\nGitHub Copilot\npull request\nKubernetes\nDocker\nAPI\nJSON\nTypeScript\nJavaScript\nPython\nPostgreSQL\nVS Code",
            Professions.addTo("genres\ngithub", "Developer").replace("genres\ngithub", "genres\nGitHub"))
        assertEquals("genres", Professions.addTo("genres", "Other"))
        val twice = Professions.addTo(Professions.addTo("", "Sales"), "Sales")
        assertEquals(Professions.addTo("", "Sales"), twice)

        // Long all-lowercase entries are reachable from ordinary words by the near-miss correction.
        val jargon = setOf("ibuprofen", "paracetamol", "amoxicillin", "affidavit", "indemnity", "plaintiff", "defendant", "syllabus", "go-to-market", "pull request", "term sheet")
        for ((role, words) in Professions.all) for (w in words) {
            val letters = w.count { it.isLetterOrDigit() }
            if (letters >= 8 && w.none { it.isUpperCase() }) kotlin.test.assertTrue(w in jargon, "$role: \"$w\" could rewrite ordinary words")
        }
    }

    /** The keyboard and the bubble type a laid-out list on a new line; a corrected time with a spoken "new line" isn't a list. */
    @Test
    fun only_a_laid_out_list_or_email_starts_on_a_new_line() {
        assertEquals("\nMy plan:\n1. Book the flights.\n2. Find a hotel.\n3. Pack the bags.",
            Layout.shape("My plan: first, book the flights, second, find a hotel, and third, pack the bags.", "Notes.", multiLine = true, email = false))
        assertEquals("Meet at 4 pm.\nBring the slides.",
            Layout.shape("Meet at 3 pm, actually 4 pm.\nBring the slides.", "Notes.", multiLine = true, email = false))
        assertEquals("Meet at 4 pm.\nBring the slides.",
            Layout.shape("Meet at 3 pm, actually 4 pm.\nBring the slides.", "Notes.\n", multiLine = true, email = false))
    }
}

/** Shared by the JVM test and the on-device test. */
object LayoutVectors {
    fun run(flags: String, input: String): String {
        val formatted = DictationFormatter().format(unescape(input))
        return Layout.apply(formatted, LayoutContext(multiLine = 'm' in flags, atStart = 's' in flags, email = 'e' in flags))
    }

    fun unescape(s: String): String {
        val sb = StringBuilder()
        var i = 0
        while (i < s.length) {
            if (s[i] == '\\' && i + 1 < s.length) {
                sb.append(when (s[i + 1]) { 'n' -> '\n'; 't' -> '\t'; 'r' -> '\r'; else -> s[i + 1] })
                i += 2
            } else {
                sb.append(s[i++])
            }
        }
        return sb.toString()
    }

    fun escape(s: String) = s.replace("\\", "\\\\").replace("\t", "\\t").replace("\r", "\\r").replace("\n", "\\n")
}
