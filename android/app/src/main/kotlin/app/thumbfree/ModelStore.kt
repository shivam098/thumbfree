package app.thumbfree

import android.content.Context
import android.os.Handler
import android.os.Looper
import app.thumbfree.core.ModelCatalog
import app.thumbfree.core.ModelFile
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
 * Speech models in the app's private storage. A model is one or more files; each download resumes after a dropped
 * connection and is checked against its pinned SHA-256 before use (a mismatch deletes it).
 */
class ModelStore(context: Context) {
    data class Progress(val done: Long, val total: Long, val running: Boolean, val error: String? = null)

    private val dir = File(context.filesDir, "models").apply { mkdirs() }
    private val progress = ConcurrentHashMap<String, Progress>()
    private val cancelled = ConcurrentHashMap.newKeySet<String>()
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private val main = Handler(Looper.getMainLooper())

    init {
        thread(name = "models-cleanup") { removeLeftovers() }
    }

    fun file(model: ModelFile) = File(dir, model.path)

    /** The folder holding a model's files. */
    fun folder(spec: ModelSpec): File = file(spec.files.first()).parentFile ?: dir

    private fun isInstalled(model: ModelFile) = file(model).let { it.isFile && it.length() == model.bytes }

    fun isInstalled(spec: ModelSpec) = spec.files.all { isInstalled(it) }

    fun progress(spec: ModelSpec): Progress? = progress[spec.id]

    fun addListener(listener: () -> Unit) = listeners.add(listener)

    fun removeListener(listener: () -> Unit) = listeners.remove(listener)

    fun delete(spec: ModelSpec) {
        cancel(spec)
        for (f in spec.files) {
            file(f).delete()
            partFile(f).delete()
        }
        folder(spec).takeIf { it != dir }?.delete() // only if empty
        progress.remove(spec.id)
        notifyChanged()
    }

    fun cancel(spec: ModelSpec) {
        cancelled += spec.id
    }

    fun download(spec: ModelSpec) {
        if (progress[spec.id]?.running == true || isInstalled(spec)) return
        cancelled -= spec.id
        progress[spec.id] = Progress(downloaded(spec), spec.bytes, running = true)
        notifyChanged()
        thread(name = "download-${spec.id}") {
            progress[spec.id] = try {
                val space = dir.usableSpace
                if (space < spec.bytes - downloaded(spec) + 50_000_000) {
                    throw IOException("Not enough free space: ${spec.bytes / 1_000_000} MB needed.")
                }
                for (f in spec.files) if (!isInstalled(f)) fetch(spec, f)
                Progress(spec.bytes, spec.bytes, running = false)
            } catch (e: IOException) {
                Progress(downloaded(spec), spec.bytes, running = false, error = e.message ?: "Download failed")
            }
            notifyChanged()
        }
    }

    /** Bytes already on the phone for [spec], finished or partial. */
    private fun downloaded(spec: ModelSpec) = spec.files.sumOf { if (isInstalled(it)) it.bytes else partFile(it).length() }

    private fun partFile(model: ModelFile) = File(dir, model.path + ".part")

    private fun fetch(spec: ModelSpec, model: ModelFile) {
        val part = partFile(model)
        var attempt = 0
        while (true) {
            try {
                fetchOnce(spec, model, part)
                break
            } catch (e: IOException) {
                if (spec.id in cancelled || ++attempt >= 5) throw e
                Thread.sleep(2_000L * attempt)
            }
        }

        if (!sha256(part).equals(model.sha256, ignoreCase = true)) {
            part.delete()
            throw IOException("The download was damaged. Try again.")
        }
        if (!part.renameTo(file(model))) throw IOException("Couldn't save the model.")
    }

    private fun fetchOnce(spec: ModelSpec, model: ModelFile, part: File) {
        val have = if (part.exists()) part.length() else 0L
        if (have >= model.bytes) return
        part.parentFile?.mkdirs() // every attempt: the leftover cleanup may remove an empty folder
        val conn = URL(model.url).openConnection() as HttpURLConnection
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
                            progress[spec.id] = Progress(downloaded(spec), spec.bytes, running = true)
                            notifyChanged()
                        }
                    }
                }
            }
            if (done < model.bytes) throw IOException("The connection dropped.")
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

    /** Deletes models this version no longer uses (for example the Whisper English models from 0.1 and 0.2). */
    private fun removeLeftovers() {
        val known = ModelCatalog.knownPaths.flatMap { listOf(it, "$it.part") }.toSet()
        dir.walkBottomUp().forEach { f ->
            if (f == dir) return@forEach
            val path = f.relativeTo(dir).invariantSeparatorsPath
            if (f.isFile && path !in known) f.delete()
            if (f.isDirectory) f.delete() // only removes empty folders
        }
    }

    private fun notifyChanged() = main.post { listeners.forEach { it() } }
}
