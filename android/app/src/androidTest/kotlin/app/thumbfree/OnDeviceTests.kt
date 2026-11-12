package app.thumbfree

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import app.thumbfree.core.DictationFormatter
import app.thumbfree.core.DictationOptions
import app.thumbfree.core.LivePreview
import app.thumbfree.core.ModelCatalog
import app.thumbfree.core.SpeechAudio
import app.thumbfree.whisper.WhisperModel
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Runs on a phone or emulator: Android's regex engine (ICU) must give the same text as the reference formatter. */
@RunWith(AndroidJUnit4::class)
class FormatterOnDeviceTest {
    @Test
    fun matches_the_reference_formatter() {
        val formatter = DictationFormatter(
            DictationOptions(
                replacements = linkedMapOf("git hub" to "GitHub", "kube" to "Kubernetes"),
                snippets = linkedMapOf("my signature" to "Thanks,\nShivam"),
            ),
        )
        val assets = InstrumentationRegistry.getInstrumentation().context.assets
        val rows = assets.open("formatter.tsv").bufferedReader(Charsets.UTF_8).readLines().filter { it.isNotEmpty() && !it.startsWith("#") }
        assertTrue("expected the vector file, found ${rows.size} rows", rows.size > 100)
        val failures = rows.mapNotNull { row ->
            val (continues, search, input, expected) = row.split('\t')
            val actual = formatter.format(unescape(input), continues == "1", search == "1")
            if (actual == unescape(expected)) null else "[$continues$search] $input\n  expected: $expected\n  actual:   $actual"
        }
        assertEquals(failures.take(20).joinToString("\n"), 0, failures.size)
    }

    private fun unescape(s: String): String {
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
}

/** Where CI pushes models for these tests (adb shell can write here; the app can read it). */
private val deviceModels = File("/data/local/tmp/thumbfree")

/** 16-bit PCM mono 16 kHz WAV from the test assets → floats. */
internal fun wav(name: String): FloatArray {
    val bytes = InstrumentationRegistry.getInstrumentation().context.assets.open(name).readBytes()
    val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
    var pos = 12
    while (pos + 8 <= bytes.size) {
        val id = String(bytes, pos, 4, Charsets.US_ASCII)
        val size = buffer.getInt(pos + 4)
        if (id == "data") {
            val count = minOf(size, bytes.size - pos - 8) / 2
            return FloatArray(count) { buffer.getShort(pos + 8 + it * 2) / 32768f }
        }
        pos += 8 + size + (size and 1)
    }
    error("no data chunk in $name")
}

private fun timed(label: String, audio: FloatArray, block: () -> String?): String? {
    val started = System.nanoTime()
    val text = block()
    println("ThumbFree $label: ${(System.nanoTime() - started) / 1_000_000} ms for ${audio.size / 16} ms of audio: $text")
    return text
}

/**
 * whisper.cpp (used for Hindi + English) on the device. Transcription runs when CI has pushed base.en to
 * /data/local/tmp/thumbfree/ggml-base.en-q8_0.bin; otherwise only the library check runs.
 */
@RunWith(AndroidJUnit4::class)
class WhisperOnDeviceTest {
    private val modelFile = File(deviceModels, "ggml-base.en-q8_0.bin")
    private val engine = SpeechEngine { deviceModels }

    @Test
    fun native_library_loads() {
        val info = WhisperModel.systemInfo
        println("ThumbFree engine ${WhisperModel.engineVariant}: $info")
        assertTrue(info, info.isNotBlank())
    }

    @Test
    fun transcribes_speech() {
        assumeTrue("no model at $modelFile", modelFile.canRead())
        val audio = wav("send-logs.wav")
        assertTrue(SpeechAudio.hasSpeech(audio))
        WhisperRecognizer.load(modelFile, engine.threads).use { model ->
            val text = timed("whisper final pass", audio) { engine.transcribe(model, audio) }!!
            assertTrue(text, text.lowercase().contains("send me the latest build logs"))

            val preview = LivePreview({ engine.preview(model, it) }).update(audio)
            assertTrue("preview: $preview", preview!!.lowercase().contains("build logs"))

            model.abort()
            assertEquals(null, model.transcribe(audio, preview = false))
            model.resume()
            assertTrue(model.transcribe(audio, preview = false).orEmpty().contains("build", ignoreCase = true))
        }
    }

    @Test
    fun word_list_hint_guides_spelling_without_breaking_dictation() {
        assumeTrue("no model at $modelFile", modelFile.canRead())
        val words = listOf("GitHub Copilot", "vibe coding", "genres", "Kubernetes")
        WhisperRecognizer.load(modelFile, engine.threads).use { model ->
            val genres = timed("whisper with word list", wav("genres.wav")) { engine.transcribe(model, wav("genres.wav"), words, prompt = true) }!!
            assertTrue(genres, genres.lowercase().contains("videos of different genres"))
            val logs = engine.transcribe(model, wav("send-logs.wav"), words, prompt = true)!!
            assertTrue(logs, logs.lowercase().contains("send me the latest build logs"))
            // Silence is never sent to Whisper, so it can't come back as the word list.
            assertEquals("", engine.transcribe(model, FloatArray(16_000 * 2), words, prompt = true))
        }
    }
}

/**
 * Parakeet TDT 110M through sherpa-onnx (the English model). Transcription runs when CI has pushed the model files to
 * /data/local/tmp/thumbfree/parakeet-tdt-110m/.
 */
@RunWith(AndroidJUnit4::class)
class ParakeetOnDeviceTest {
    private val spec = ModelCatalog.EnglishFast
    private val dir = File(deviceModels, "parakeet-tdt-110m")
    private val engine = SpeechEngine { dir }

    @Test
    fun transcribes_with_punctuation_and_fixes_listed_words() {
        assumeTrue("no model at $dir", File(dir, "encoder.int8.onnx").canRead())
        val started = System.nanoTime()
        val model = ParakeetRecognizer.load(dir, engine.threads)
        println("ThumbFree parakeet load: ${(System.nanoTime() - started) / 1_000_000} ms")
        model.use {
            val logs = timed("parakeet final pass", wav("send-logs.wav")) {
                engine.transcribe(it, wav("send-logs.wav"), maxPieceSamples = spec.maxPieceSamples)
            }!!
            assertTrue(logs, logs.contains("send me the latest build logs", ignoreCase = true))
            assertTrue("Parakeet punctuates: $logs", logs.trimEnd().endsWith("."))

            val genres = engine.transcribe(it, wav("genres.wav"))!!
            assertTrue(genres, genres.contains("videos of different genres", ignoreCase = true))

            val words = listOf("GitHub Copilot", "Kubernetes")
            val audio = wav("dictation.wav")
            val raw = timed("parakeet 10 s dictation", audio) { engine.transcribe(it, audio, words, maxPieceSamples = spec.maxPieceSamples) }!!
            val text = DictationFormatter().format(raw)
            println("ThumbFree formatted: $text")
            assertTrue(text, text.contains("GitHub Copilot"))
            assertTrue(text, text.contains("pull request,"))
            assertFalse(text, text.contains(" um ", ignoreCase = true))

            val preview = LivePreview({ a -> engine.preview(it, a, words) }).update(audio)
            assertTrue("preview: $preview", preview!!.contains("pull request", ignoreCase = true))

            it.abort()
            assertEquals(null, it.transcribe(audio, preview = false))
            it.resume()
            assertTrue(it.transcribe(wav("send-logs.wav"), preview = false).orEmpty().contains("build", ignoreCase = true))
        }
    }
}