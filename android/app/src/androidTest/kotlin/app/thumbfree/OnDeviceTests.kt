package app.thumbfree

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import app.thumbfree.core.DecodeOptions
import app.thumbfree.core.DictationFormatter
import app.thumbfree.core.DictationOptions
import app.thumbfree.core.LivePreview
import app.thumbfree.core.SpeechAudio
import app.thumbfree.whisper.WhisperModel
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertEquals
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

/**
 * The native speech engine on the device. Transcription runs when a model has been pushed to
 * /data/local/tmp/thumbfree/ggml-base.en-q8_0.bin (CI does this); otherwise only the library check runs.
 */
@RunWith(AndroidJUnit4::class)
class WhisperOnDeviceTest {
    private val modelFile = File("/data/local/tmp/thumbfree/ggml-base.en-q8_0.bin")

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
        val engine = SpeechEngine()
        WhisperModel.load(modelFile).use { model ->
            val started = System.nanoTime()
            val text = engine.transcribe(model, audio)!!
            println("ThumbFree final pass: ${(System.nanoTime() - started) / 1_000_000} ms for ${audio.size / 16} ms of audio: $text")
            assertTrue(text, text.lowercase().contains("send me the latest build logs"))

            val preview = LivePreview({ engine.preview(model, it) }).update(audio)
            assertTrue("preview: $preview", preview!!.lowercase().contains("build logs"))

            model.abort()
            assertEquals(null, model.transcribe(audio, DecodeOptions(engine.threads, 768)))
            model.resume()
            assertTrue(model.transcribe(audio, DecodeOptions(engine.threads, 768)).orEmpty().contains("build", ignoreCase = true))
        }
    }

    /** 16-bit PCM mono 16 kHz WAV → floats. */
    private fun wav(name: String): FloatArray {
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
}
