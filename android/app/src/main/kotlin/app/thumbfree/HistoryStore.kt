package app.thumbfree

import android.content.Context
import android.content.pm.PackageManager
import android.os.Handler
import android.os.Looper
import app.thumbfree.core.History
import app.thumbfree.core.HistoryEntry
import java.io.File
import java.util.concurrent.Executors

/**
 * History: the text of your recent dictations, kept only on this phone (app-private storage, excluded from backups,
 * never uploaded) so you can copy one again. 30 days, at most 500, never from password or incognito fields. Can be
 * turned off (which deletes it) or cleared.
 */
class HistoryStore(private val context: Context, private val enabled: () -> Boolean) {
    private val file = File(context.noBackupFilesDir, "history.txt")
    private val io = Executors.newSingleThreadExecutor { r -> Thread(r, "history").apply { isDaemon = true } }
    private val main = Handler(Looper.getMainLooper())
    private val listeners = mutableListOf<() -> Unit>()

    private val lock = Any()
    private var cache: List<HistoryEntry>? = null

    /** Newest first, without anything past 30 days. Reads the file the first time; rewrites it when entries expire. */
    fun entries(): List<HistoryEntry> = synchronized(lock) {
        val loaded = cache ?: read()
        val kept = History.prune(loaded, System.currentTimeMillis())
        cache = kept
        if (kept.size != loaded.size) background { synchronized(lock) { write() } }
        kept
    }

    /** Adds a dictation typed (or meant) for [app]. Any thread; does nothing when History is off. */
    fun add(text: String, app: String?) {
        if (text.isBlank() || !enabled()) return
        val entry = HistoryEntry(System.currentTimeMillis(), app.orEmpty(), text)
        background {
            synchronized(lock) {
                cache = History.prune(listOf(entry) + entries(), entry.time)
                write()
            }
        }
    }

    fun delete(entry: HistoryEntry) = background {
        synchronized(lock) {
            cache = entries().filterNot { it == entry }
            write()
        }
    }

    fun clear() = background {
        synchronized(lock) {
            cache = emptyList()
            file.delete()
        }
    }

    /** Runs [block] on the History thread. A failed write (a full disk) is never fatal: the keyboard shares this process. */
    private fun background(block: () -> Unit) = io.execute {
        try {
            block()
        } catch (e: Exception) {
            android.util.Log.w("ThumbFree", "History: ${e.javaClass.simpleName}")
        } finally {
            changed()
        }
    }

    fun addListener(listener: () -> Unit) = listeners.add(listener)
    fun removeListener(listener: () -> Unit) = listeners.remove(listener)

    /** The app's name, or a readable form of its package name when Android doesn't let us see it. */
    fun appLabel(packageName: String): String {
        if (packageName.isEmpty()) return ""
        if (packageName == context.packageName) return "ThumbFree"
        return try {
            val pm = context.packageManager
            pm.getApplicationLabel(pm.getApplicationInfo(packageName, 0)).toString()
        } catch (e: PackageManager.NameNotFoundException) {
            Known[packageName] ?: packageName.substringAfterLast('.').replaceFirstChar { it.uppercase() }
        }
    }

    private fun read(): List<HistoryEntry> =
        runCatching { if (file.isFile) file.readLines(Charsets.UTF_8).mapNotNull { History.decode(it) } else emptyList() }.getOrDefault(emptyList())

    private fun write() {
        val tmp = File(file.parentFile, "history.tmp")
        tmp.writeText(cache.orEmpty().joinToString("\n") { History.encode(it) }, Charsets.UTF_8)
        if (!tmp.renameTo(file)) {
            tmp.copyTo(file, overwrite = true)
            tmp.delete()
        }
    }

    private fun changed() = main.post { listeners.toList().forEach { it() } }

    private companion object {
        val Known = mapOf(
            "com.whatsapp" to "WhatsApp",
            "com.google.android.gm" to "Gmail",
            "com.android.chrome" to "Chrome",
            "com.google.android.apps.messaging" to "Messages",
            "com.microsoft.teams" to "Teams",
            "com.microsoft.office.outlook" to "Outlook",
            "com.slack" to "Slack",
            "org.telegram.messenger" to "Telegram",
            "com.instagram.android" to "Instagram",
            "com.google.android.keep" to "Keep",
        )
    }
}
