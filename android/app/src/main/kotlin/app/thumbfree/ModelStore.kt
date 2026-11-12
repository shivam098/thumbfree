package app.thumbfree

import android.content.Context
import android.os.Handler
import android.os.Looper
import app.thumbfree.core.ModelSpec
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.concurrent.thread

/**
 * Speech models in the app's private storage. Downloads resume after a dropped connection and are checked against the
 * pinned SHA-256 before use; a mismatch deletes the file.
 */
class ModelStore(context: Context) {
    data class Progress(val done: Long, val total: Long, val running: Boolean, val error: String? = null)

    private val dir = File(context.filesDir, "models").apply { mkdirs() }
    private val progress = ConcurrentHashMap<String, Progress>()
    private val cancelled = ConcurrentHashMap.newKeySet<String>()
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private val main = Handler(Looper.getMainLooper())

    fun file(spec: ModelSpec) = File(dir, spec.file)

    fun isInstalled(spec: ModelSpec) = file(spec).let { it.isFile && it.length() == spec.bytes }

    fun progress(spec: ModelSpec): Progress? = progress[spec.id]

    fun addListener(listener: () -> Unit) = listeners.add(listener)

    fun removeListener(listener: () -> Unit) = listeners.remove(listener)

    fun delete(spec: ModelSpec) {
        cancel(spec)
        file(spec).delete()
        partFile(spec).delete()
        progress.remove(spec.id)
        notifyChanged()
    }

    fun cancel(spec: ModelSpec) {
        cancelled += spec.id
    }

    fun download(spec: ModelSpec) {
        if (progress[spec.id]?.running == true || isInstalled(spec)) return
        cancelled -= spec.id
        progress[spec.id] = Progress(partFile(spec).length(), spec.bytes, running = true)
        notifyChanged()
        thread(name = "download-${spec.id}") {
            progress[spec.id] = try {
                fetch(spec)
                Progress(spec.bytes, spec.bytes, running = false)
            } catch (e: IOException) {
                Progress(partFile(spec).length(), spec.bytes, running = false, error = e.message ?: "Download failed")
            }
            notifyChanged()
        }
    }

    private fun partFile(spec: ModelSpec) = File(dir, spec.file + ".part")

    private fun fetch(spec: ModelSpec) {
        val part = partFile(spec)
        if (dir.usableSpace < spec.bytes - part.length() + 50_000_000) {
            throw IOException("Not enough free space: ${spec.bytes / 1_000_000} MB needed.")
        }
        var attempt = 0
        while (true) {
            try {
                fetchOnce(spec, part)
                break
            } catch (e: IOException) {
                if (spec.id in cancelled || ++attempt >= 5) throw e
                Thread.sleep(2_000L * attempt)
            }
        }

        if (!sha256(part).equals(spec.sha256, ignoreCase = true)) {
            part.delete()
            throw IOException("The download was damaged. Try again.")
        }
        if (!part.renameTo(file(spec))) throw IOException("Couldn't save the model.")
    }

    private fun fetchOnce(spec: ModelSpec, part: File) {
        val have = if (part.exists()) part.length() else 0L
        if (have >= spec.bytes) return
        val conn = URL(spec.url).openConnection() as HttpURLConnection
        conn.instanceFollowRedirects = true
        conn.connectTimeout = 20_000
        conn.readTimeout = 30_000
        conn.setRequestProperty("User-Agent", "ThumbFree")
        if (have > 0) conn.setRequestProperty("Range", "bytes=$have-")
        try {
            val code = conn.responseCode
            if (code != HttpURLConnection.HTTP_OK && code != HttpURLConnection.HTTP_PARTIAL) throw IOException("The server answered $code.")
            val append = code == HttpURLConnection.HTTP_PARTIAL
            var done = if (append) have else 0L
            var reported = 0L
            conn.inputStream.use { input ->
                FileOutputStream(part, append).use { out ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        if (spec.id in cancelled) throw IOException("Cancelled")
                        val n = input.read(buffer)
                        if (n < 0) break
                        out.write(buffer, 0, n)
                        done += n
                        if (done - reported > 512 * 1024) {
                            reported = done
                            progress[spec.id] = Progress(done, spec.bytes, running = true)
                            notifyChanged()
                        }
                    }
                }
            }
            if (done < spec.bytes) throw IOException("The connection dropped.")
        } finally {
            conn.disconnect()
        }
    }

    private fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(256 * 1024)
            while (true) {
                val n = input.read(buffer)
                if (n < 0) break
                digest.update(buffer, 0, n)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    private fun notifyChanged() = main.post { listeners.forEach { it() } }
}
