package app.thumbfree

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.DataOutputStream
import java.io.File
import java.io.FileOutputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.concurrent.thread
import org.json.JSONObject

/**
 * "Help improve ThumbFree" (off unless you turn it on). Each dictation's audio, the raw transcript and the text that
 * was typed are kept in the app's private storage, never uploaded. You decide if and where to send them: "Share" makes
 * a zip and opens Android's share sheet. Never kept for password or incognito fields.
 */
class SampleStore(private val context: Context) {
    private val dir = File(context.filesDir, "samples")

    data class Summary(val count: Int, val bytes: Long)

    fun summary(): Summary {
        val files = dir.listFiles().orEmpty().filter { it.isDirectory }
        return Summary(files.size, files.sumOf { d -> d.listFiles().orEmpty().sumOf { it.length() } })
    }

    fun save(transcript: Dictation.Transcript, typed: String, language: String) {
        if (transcript.raw.isEmpty()) return
        thread(name = "save-sample") {
            runCatching {
                val stamp = SimpleDateFormat("yyyyMMdd-HHmmss-SSS", Locale.ROOT).format(Date())
                val sample = File(dir, stamp).apply { mkdirs() }
                writeWav(File(sample, "audio.wav"), transcript.audio)
                val meta = JSONObject()
                    .put("model", transcript.spec.id)
                    .put("language", language)
                    .put("seconds", transcript.seconds.toDouble())
                    .put("transcript", transcript.raw)
                    .put("typed", typed)
                    .put("app_version", context.packageManager.getPackageInfo(context.packageName, 0).versionName)
                File(sample, "meta.json").writeText(meta.toString(2))
                trim()
            }
        }
    }

    fun deleteAll() {
        dir.deleteRecursively()
        File(context.cacheDir, "share").deleteRecursively()
    }

    /** Zips every sample and returns a share intent for it (null when there's nothing to share). Not on the main thread. */
    fun shareIntent(): Intent? {
        val samples = dir.listFiles().orEmpty().filter { it.isDirectory }.sortedBy { it.name }
        if (samples.isEmpty()) return null
        val out = File(context.cacheDir, "share").apply { deleteRecursively(); mkdirs() }
        val zip = File(out, "thumbfree-samples-${SimpleDateFormat("yyyyMMdd", Locale.ROOT).format(Date())}.zip")
        ZipOutputStream(FileOutputStream(zip)).use { z ->
            for (sample in samples) for (f in sample.listFiles().orEmpty()) {
                z.putNextEntry(ZipEntry("${sample.name}/${f.name}"))
                f.inputStream().use { it.copyTo(z) }
                z.closeEntry()
            }
        }
        val uri = SampleProvider.uri(context, zip.name)
        return Intent(Intent.ACTION_SEND)
            .setType("application/zip")
            .putExtra(Intent.EXTRA_STREAM, uri)
            .putExtra(Intent.EXTRA_SUBJECT, "ThumbFree dictation samples")
            .putExtra(Intent.EXTRA_TEXT, "${samples.size} ThumbFree dictations (audio, transcript and typed text) to help improve recognition.")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    }

    /** Keeps the newest samples within 300 recordings and 150 MB. */
    private fun trim() {
        val samples = dir.listFiles().orEmpty().filter { it.isDirectory }.sortedByDescending { it.name }
        var bytes = 0L
        samples.forEachIndexed { i, d ->
            bytes += d.listFiles().orEmpty().sumOf { it.length() }
            if (i >= 300 || bytes > 150_000_000) d.deleteRecursively()
        }
    }

    private fun writeWav(file: File, audio: FloatArray) {
        DataOutputStream(FileOutputStream(file).buffered()).use { out ->
            fun int(v: Int) = out.writeInt(Integer.reverseBytes(v))
            fun short(v: Int) = out.writeShort(java.lang.Short.reverseBytes(v.toShort()).toInt())
            val data = audio.size * 2
            out.writeBytes("RIFF"); int(36 + data); out.writeBytes("WAVE")
            out.writeBytes("fmt "); int(16); short(1); short(1); int(Recorder.RATE); int(Recorder.RATE * 2); short(2); short(16)
            out.writeBytes("data"); int(data)
            for (s in audio) short((s.coerceIn(-1f, 1f) * 32767).toInt())
        }
    }
}

/** Serves the zip made by [SampleStore.shareIntent] to the app the person picks in the share sheet. */
class SampleProvider : ContentProvider() {
    override fun onCreate() = true

    private fun file(uri: Uri): File? {
        val name = uri.lastPathSegment ?: return null
        val f = File(File(context!!.cacheDir, "share"), name)
        return f.takeIf { it.isFile && it.parentFile?.name == "share" && !name.contains('/') }
    }

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor? =
        file(uri)?.let { ParcelFileDescriptor.open(it, ParcelFileDescriptor.MODE_READ_ONLY) }

    override fun getType(uri: Uri) = "application/zip"

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, args: Array<out String>?, sort: String?): Cursor? {
        val f = file(uri) ?: return null
        return MatrixCursor(arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)).apply { addRow(arrayOf(f.name, f.length())) }
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, args: Array<out String>?) = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, args: Array<out String>?) = 0

    companion object {
        fun uri(context: Context, name: String): Uri = Uri.parse("content://${context.packageName}.samples/$name")
    }
}
