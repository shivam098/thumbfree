package app.thumbfree

import android.Manifest
import android.content.pm.PackageManager
import android.os.Handler
import android.os.Looper
import app.thumbfree.core.LivePreview
import app.thumbfree.core.ModelSpec
import app.thumbfree.core.Vocabulary

/**
 * One dictation: microphone, live preview, the "speaking into the void" alert and the final transcript. Shared by the
 * keyboard and the bubble. Main thread only; one dictation records at a time.
 */
class Dictation(private val app: ThumbFreeApp, private val listener: Listener) {
    interface Listener {
        fun onLevel(level: Float) {}

        /** Live text while recording ("" until something is recognised). */
        fun onPreview(text: String) {}

        /** Something to show instead of the preview (the microphone went silent, the model didn't load…). */
        fun onWarning(message: String) {}

        /** The final transcript (raw text is "" when nothing was said), or null if it couldn't be made. */
        fun onTranscript(result: Transcript?)
    }

    class Transcript(val raw: String, val audio: FloatArray, val spec: ModelSpec) {
        val seconds: Float get() = audio.size / Recorder.RATE.toFloat()
    }

    enum class Problem { NoPermission, NoModel, MicUnavailable, Busy }

    private val main = Handler(Looper.getMainLooper())
    private var recorder: Recorder? = null
    private var preview: LivePreview? = null
    private var spec: ModelSpec? = null
    private var vocabulary: List<String> = emptyList()
    @Volatile private var previewAllowed = false
    private var previewBusy = false
    private var session = 0
    private var orphanSession = -1
    private var orphan: ((Transcript?) -> Unit)? = null
    private var warnedSilent = false
    private var lastPreview = ""

    val recording: Boolean get() = recorder != null
    var finishing = false
        private set
    val busy: Boolean get() = recording || finishing

    /** The model of the current or last dictation. */
    val model: ModelSpec? get() = spec

    /** Starts recording; null when it started, else why not. */
    fun start(): Problem? {
        if (busy || (active != null && active !== this)) return Problem.Busy
        if (app.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return Problem.NoPermission
        val spec = app.prefs.model()
        if (!app.models.isInstalled(spec)) return Problem.NoModel

        val s = session + 1
        val rec = Recorder(
            onLevel = { level -> main.post { if (s == session && recorder != null) listener.onLevel(level) } },
            onEnded = {
                main.post {
                    if (s == session && recorder != null) {
                        listener.onWarning("The microphone was taken by another app. Writing what you said so far…")
                        app.notifier.interrupted()
                        finish()
                    }
                }
            },
        )
        if (!rec.start()) return Problem.MicUnavailable

        session = s
        recorder = rec
        this.spec = spec
        active = this
        vocabulary = Vocabulary.parse(app.prefs.vocabulary)
        previewBusy = false
        previewAllowed = app.prefs.livePreview && spec.livePreview
        warnedSilent = false
        lastPreview = ""
        app.holdModel()
        app.engine.run {
            try {
                app.engine.model(spec).resume()
            } catch (e: Exception) {
                main.post { if (s == session && recorder != null) listener.onWarning("Couldn't load the speech model. Download it again in ThumbFree.") }
            }
        }
        val words = vocabulary
        preview = LivePreview({ audio ->
            if (!previewAllowed) null else app.engine.loaded()?.let { app.engine.preview(it, audio, words, spec.promptVocabulary) }
        })
        main.postDelayed(tick, TICK_MS)
        return null
    }

    private val tick = object : Runnable {
        override fun run() {
            val rec = recorder ?: return
            val p = preview ?: return
            // Real rooms are never exactly silent: zeros mean Android has cut this app off the microphone.
            val silent = (rec.silentSoFar && rec.samples > Recorder.RATE) || rec.silentRun > Recorder.RATE * 3
            if (silent && !warnedSilent) {
                warnedSilent = true
                listener.onWarning("No sound from the microphone. Is another app or a call using it?")
                app.notifier.micSilent()
            } else if (!silent && warnedSilent) {
                warnedSilent = false
                app.notifier.clearMicSilent()
                listener.onPreview(lastPreview)
            }
            if (rec.full) {
                listener.onWarning("That's the 10-minute limit. Writing it now…")
                finish()
                return
            }
            if (previewAllowed && !previewBusy && rec.samples - p.committedSamples >= Recorder.RATE / 2) {
                previewBusy = true
                val pending = rec.copy(p.committedSamples)
                val s = session
                app.engine.run {
                    val text = if (previewAllowed) runCatching { p.update(pending) }.getOrNull() else null
                    main.post {
                        if (s == session) {
                            previewBusy = false
                            if (text != null && recorder != null) {
                                lastPreview = text
                                if (!warnedSilent) listener.onPreview(text)
                            }
                        }
                    }
                }
            }
            main.postDelayed(this, TICK_MS)
        }
    }

    /** Stops recording and makes the final transcript (delivered to the listener). */
    fun finish() {
        val rec = recorder ?: return
        val spec = spec ?: return
        recorder = null
        finishing = true
        previewAllowed = false
        main.removeCallbacks(tick)
        val audio = rec.stop()
        clearSilentAlert()
        app.engine.abort() // stop a preview in progress; the final pass resumes the model
        val s = session
        val words = vocabulary
        app.engine.run {
            val raw = try {
                val model = app.engine.model(spec)
                model.resume()
                app.engine.transcribe(model, audio, words, spec.promptVocabulary, spec.maxPieceSamples)
            } catch (e: Exception) {
                null
            }
            main.post { deliver(s, raw?.let { Transcript(it, audio, spec) }) }
        }
    }

    /** Stops and throws away the recording (and a transcript still being made). */
    fun cancel() {
        if (!busy) return
        previewAllowed = false
        main.removeCallbacks(tick)
        recorder?.stop()
        recorder = null
        session++
        finishing = false
        app.engine.abort()
        clearSilentAlert()
        release()
    }

    /**
     * The text box went away: finish if still recording, and hand the transcript to [handler] instead of the listener
     * (which may already be gone). Nothing is ever typed into a different field.
     */
    fun detach(handler: (Transcript?) -> Unit) {
        if (recording) finish()
        if (!finishing) return
        orphanSession = session
        orphan = handler
        session++
    }

    private fun deliver(s: Int, result: Transcript?) {
        if (s == orphanSession) {
            val handler = orphan
            orphanSession = -1
            orphan = null
            finishing = false
            release()
            handler?.invoke(result)
            return
        }
        if (s != session) return
        finishing = false
        release()
        listener.onTranscript(result)
    }

    private fun clearSilentAlert() {
        if (warnedSilent) app.notifier.clearMicSilent()
        warnedSilent = false
    }

    private fun release() {
        if (active === this) active = null
        app.releaseModelLater()
    }

    companion object {
        private const val TICK_MS = 900L

        /** The dictation using the microphone or the speech engine (main thread). */
        private var active: Dictation? = null

        val anyBusy: Boolean get() = active != null
    }
}
