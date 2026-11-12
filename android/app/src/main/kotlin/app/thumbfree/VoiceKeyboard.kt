package app.thumbfree

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.inputmethodservice.InputMethodService
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.text.TextUtils
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.WindowInsets
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.LinearLayout
import android.widget.TextView
import app.thumbfree.core.DictationFormatter
import app.thumbfree.core.FieldInfo
import app.thumbfree.core.Insertion
import app.thumbfree.core.Language
import app.thumbfree.core.LivePreview
import app.thumbfree.core.ModelCatalog
import java.io.File

/**
 * The ThumbFree voice keyboard. Hold the mic and speak; release to write the cleaned-up text where the cursor is.
 * A quick tap starts hands-free dictation (tap again to finish); sliding up before releasing cancels.
 * The microphone is on only during dictation, and audio never leaves the phone.
 */
class VoiceKeyboard : InputMethodService() {
    private enum class State { Idle, Holding, HandsFree, Finishing }

    private enum class Tone { Muted, Active, Ok, Warn }

    private val main = Handler(Looper.getMainLooper())
    private var state = State.Idle
    private var field = FieldInfo(password = false, search = false, multiLine = true, private = false)
    private var editorAction = EditorInfo.IME_ACTION_UNSPECIFIED
    private var noEnterAction = false
    private var lastText: String? = null

    private var recorder: Recorder? = null
    private var preview: LivePreview? = null
    private var sessionModel: File? = null
    @Volatile private var previewAllowed = false
    private var previewBusy = false
    private var session = 0
    private var downAt = 0L
    private var ignoreUp = false
    private var wantsCancel = false

    private lateinit var status: TextView
    private lateinit var hint: TextView
    private lateinit var mic: MicButton
    private lateinit var languageKey: TextView
    private lateinit var enterKey: TextView

    private val app get() = thumbFree
    private val prefs get() = app.prefs

    override fun onEvaluateFullscreenMode() = false

    override fun onCreateInputView(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(color(R.color.tf_bg))
            setPadding(dpi(10), dpi(6), dpi(10), dpi(10))
            // Android 15 draws keyboards edge to edge; keep the keys above the navigation bar.
            setOnApplyWindowInsetsListener { v, insets ->
                val bottom = if (Build.VERSION.SDK_INT >= 30) {
                    insets.getInsets(WindowInsets.Type.navigationBars()).bottom
                } else {
                    @Suppress("DEPRECATION") insets.systemWindowInsetBottom
                }
                v.setPadding(dpi(10), dpi(6), dpi(10), dpi(10) + bottom)
                insets
            }
        }

        val top = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        status = TextView(this).apply {
            textSize = 15f
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.START
            minHeight = dpi(44)
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dpi(6), 0, dpi(6), 0)
        }
        top.addView(status, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        top.addView(key("⚙", "ThumbFree settings") { openSetup() }.apply { textSize = 16f }, LinearLayout.LayoutParams(dpi(44), dpi(36)))
        root.addView(top)

        mic = MicButton(this)
        mic.setOnTouchListener { _, e -> onMicTouch(e) }
        mic.setOnClickListener { onMicClick() } // TalkBack double-tap
        root.addView(mic, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dpi(116)))

        hint = TextView(this).apply {
            textSize = 12f
            gravity = Gravity.CENTER
            setTextColor(color(R.color.tf_muted))
        }
        root.addView(hint)

        val row = LinearLayout(this)
        fun add(v: View, weight: Float) = row.addView(
            v,
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, weight).apply {
                marginStart = dpi(3)
                marginEnd = dpi(3)
            },
        )
        add(key("⌨", "Switch keyboard") { switchKeyboard() }.apply { setOnLongClickListener { showPicker(); true } }, 1f)
        languageKey = key("EN", "Dictation language") { toggleLanguage() }.apply { textSize = 14f }
        add(languageKey, 1.3f)
        add(key("↺", "Insert the last dictation again") { insertAgain() }, 1f)
        add(key("space", "Space") { commit(" ") }.apply { textSize = 14f }, 2.6f)
        val back = key("⌫", "Delete") { deleteOne() }
        back.setOnTouchListener { v, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    v.isPressed = true
                    deleteOne()
                    main.postDelayed(repeatDelete, 400)
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    v.isPressed = false
                    main.removeCallbacks(repeatDelete)
                }
            }
            true
        }
        add(back, 1.2f)
        enterKey = key("⏎", "Enter") { enter() }
        add(enterKey, 1.2f)
        root.addView(row, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dpi(48)).apply { topMargin = dpi(8) })

        setState(state)
        refresh()
        return root
    }

    override fun onStartInputView(info: EditorInfo, restarting: Boolean) {
        super.onStartInputView(info, restarting)
        field = FieldInfo.of(info.inputType, info.imeOptions)
        editorAction = info.imeOptions and EditorInfo.IME_MASK_ACTION
        noEnterAction = (info.imeOptions and EditorInfo.IME_FLAG_NO_ENTER_ACTION) != 0
        refresh()
    }

    override fun onFinishInputView(finishingInput: Boolean) {
        cancel(silent = true)
        dropPending()
        super.onFinishInputView(finishingInput)
    }

    override fun onDestroy() {
        cancel(silent = true)
        dropPending()
        main.removeCallbacksAndMessages(null)
        app.engine.unload()
        super.onDestroy()
    }

    // ---- dictation ----

    private fun onMicTouch(e: MotionEvent): Boolean {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                ignoreUp = false
                when (state) {
                    State.HandsFree -> {
                        finish()
                        ignoreUp = true
                    }
                    State.Idle -> {
                        downAt = e.eventTime
                        wantsCancel = false
                        if (start()) setState(State.Holding) else ignoreUp = true
                    }
                    else -> ignoreUp = true
                }
            }
            MotionEvent.ACTION_MOVE -> if (state == State.Holding) {
                val cancelZone = e.y < -mic.height * 0.25f
                if (cancelZone != wantsCancel) {
                    wantsCancel = cancelZone
                    show(if (cancelZone) "Release to cancel" else "Listening…", if (cancelZone) Tone.Warn else Tone.Active)
                }
            }
            MotionEvent.ACTION_UP -> if (!ignoreUp && state == State.Holding) {
                when {
                    wantsCancel -> cancel(silent = false)
                    e.eventTime - downAt < TAP_MS -> {
                        setState(State.HandsFree)
                        show("Hands-free: speak, then tap the mic to finish", Tone.Active)
                    }
                    else -> finish()
                }
            }
            MotionEvent.ACTION_CANCEL -> if (state == State.Holding) cancel(silent = true)
        }
        return true
    }

    private fun onMicClick() {
        when (state) {
            State.Idle -> if (start()) setState(State.HandsFree)
            State.HandsFree, State.Holding -> finish()
            State.Finishing -> Unit
        }
    }

    private fun start(): Boolean {
        if (state != State.Idle) return false
        if (field.password) {
            show("Dictation is off in password fields", Tone.Warn)
            return false
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            show("Allow the microphone in ThumbFree first", Tone.Warn)
            openSetup()
            return false
        }
        val spec = ModelCatalog.forLanguage(prefs.language, prefs.accurateEnglish)
        if (!app.models.isInstalled(spec)) {
            show("Download the “${spec.label}” speech model in ThumbFree first", Tone.Warn)
            openSetup()
            return false
        }

        val rec = Recorder { level -> main.post { if (recorder != null) mic.level = level } }
        if (!rec.start()) {
            show("Couldn't start the microphone. Is another app using it?", Tone.Warn)
            return false
        }

        session++
        recorder = rec
        previewBusy = false
        previewAllowed = prefs.livePreview
        main.removeCallbacks(unload)
        mic.recording = true
        haptic()
        show("Listening…", Tone.Active)

        val file = app.models.file(spec)
        sessionModel = file
        val s = session
        app.engine.run {
            try {
                app.engine.model(file).resume()
            } catch (e: Exception) {
                main.post {
                    if (s == session) {
                        cancel(silent = true)
                        show("Couldn't load the speech model. Download it again in ThumbFree.", Tone.Warn)
                    }
                }
            }
        }
        preview = LivePreview({ audio -> if (!previewAllowed) null else app.engine.loaded()?.let { app.engine.preview(it, audio) } })
        main.postDelayed(previewTick, PREVIEW_MS)
        return true
    }

    private val previewTick = object : Runnable {
        override fun run() {
            val rec = recorder ?: return
            val p = preview ?: return
            if (state != State.Holding && state != State.HandsFree) return
            if (rec.silentSoFar && rec.samples > Recorder.RATE) {
                show("No sound from the microphone. Is another app using it?", Tone.Warn)
            } else if (previewAllowed && !previewBusy && rec.samples - p.committedSamples >= Recorder.RATE / 2) {
                previewBusy = true
                val pending = rec.copy(p.committedSamples)
                val s = session
                app.engine.run {
                    val text = if (previewAllowed) runCatching { p.update(pending) }.getOrNull() else null
                    main.post {
                        if (s == session) {
                            previewBusy = false
                            if (text != null && (state == State.Holding || state == State.HandsFree)) {
                                show(text.ifEmpty { "Listening…" }, Tone.Active)
                            }
                        }
                    }
                }
            }
            main.postDelayed(this, PREVIEW_MS)
        }
    }

    private fun finish() {
        val rec = recorder ?: return
        setState(State.Finishing)
        previewAllowed = false
        main.removeCallbacks(previewTick)
        val audio = rec.stop()
        recorder = null
        mic.recording = false
        haptic()
        app.engine.abort() // stop a preview in progress; the final pass resumes the model
        show("Writing…", Tone.Active)

        val file = sessionModel ?: return
        val s = session
        app.engine.run {
            val text = try {
                val model = app.engine.model(file)
                model.resume()
                app.engine.transcribe(model, audio)
            } catch (e: Exception) {
                null
            }
            main.post { if (s == session) deliver(text) }
        }
    }

    private fun cancel(silent: Boolean) {
        if (state != State.Holding && state != State.HandsFree) return
        previewAllowed = false
        main.removeCallbacks(previewTick)
        recorder?.stop()
        recorder = null
        session++ // ignore results still on their way
        app.engine.abort()
        mic.recording = false
        setState(State.Idle)
        if (!silent) {
            haptic()
            show("Cancelled", Tone.Muted)
        }
        scheduleUnload()
    }

    /** The text box went away while the final pass was running: never type the result somewhere else. */
    private fun dropPending() {
        if (state != State.Finishing) return
        session++
        app.engine.abort()
        setState(State.Idle)
        scheduleUnload()
    }

    private fun deliver(raw: String?) {
        setState(State.Idle)
        scheduleUnload()
        when {
            raw == null -> show("Couldn't turn that into text. Please try again.", Tone.Warn)
            raw.isEmpty() -> show("Didn't catch anything. Hold the mic and speak.", Tone.Muted)
            else -> insert(raw)
        }
    }

    private fun insert(raw: String) {
        val ic = currentInputConnection ?: return show("Tap a text box first.", Tone.Warn)
        if (field.password) return show("Dictation is off in password fields", Tone.Warn)
        val before = ic.getTextBeforeCursor(200, 0)
        val formatter = DictationFormatter(prefs.dictationOptions())
        var text = formatter.format(raw, Insertion.continuesSentence(before), field.search)
        if (text.isEmpty()) return show("Didn't catch anything.", Tone.Muted)
        text = Insertion.forField(text, field)
        ic.commitText(Insertion.withSpacing(before, text), 1)
        if (!field.private) lastText = text
        show(text, Tone.Ok)
        if (prefs.switchBack && Build.VERSION.SDK_INT >= 28) switchToPreviousInputMethod()
    }

    // ---- keys ----

    private fun commit(text: String) {
        currentInputConnection?.commitText(text, 1)
    }

    private val repeatDelete = object : Runnable {
        override fun run() {
            deleteOne()
            main.postDelayed(this, 60)
        }
    }

    private fun deleteOne() {
        val ic = currentInputConnection ?: return
        if (!ic.getSelectedText(0).isNullOrEmpty()) ic.commitText("", 1) else sendDownUpKeyEvents(KeyEvent.KEYCODE_DEL)
    }

    private fun enter() {
        val ic = currentInputConnection ?: return
        if (!noEnterAction && editorAction != EditorInfo.IME_ACTION_NONE && editorAction != EditorInfo.IME_ACTION_UNSPECIFIED) {
            ic.performEditorAction(editorAction)
        } else if (field.multiLine) {
            ic.commitText("\n", 1)
        } else {
            sendDownUpKeyEvents(KeyEvent.KEYCODE_ENTER)
        }
    }

    private fun insertAgain() {
        val text = lastText ?: return show("Nothing dictated yet", Tone.Muted)
        val ic = currentInputConnection ?: return
        ic.commitText(Insertion.withSpacing(ic.getTextBeforeCursor(200, 0), Insertion.forField(text, field)), 1)
    }

    private fun toggleLanguage() {
        if (state != State.Idle) return
        prefs.language = if (prefs.language == Language.English) Language.Hinglish else Language.English
        refresh()
        val spec = ModelCatalog.forLanguage(prefs.language, prefs.accurateEnglish)
        if (!app.models.isInstalled(spec)) {
            show("Download “${spec.label}” in ThumbFree to use it", Tone.Warn)
        } else {
            show(if (prefs.language == Language.Hinglish) "Hindi + English, typed in Roman letters" else "English", Tone.Muted)
        }
    }

    private fun switchKeyboard() {
        if (Build.VERSION.SDK_INT >= 28 && switchToPreviousInputMethod()) return
        showPicker()
    }

    private fun showPicker() = getSystemService(InputMethodManager::class.java).showInputMethodPicker()

    private fun openSetup() = startActivity(Intent(this, SetupActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))

    // ---- ui ----

    private fun refresh() {
        if (!::status.isInitialized) return
        languageKey.text = if (prefs.language == Language.Hinglish) "HI+EN" else "EN"
        enterKey.text = when {
            noEnterAction -> "⏎"
            editorAction == EditorInfo.IME_ACTION_SEARCH -> "🔍"
            editorAction == EditorInfo.IME_ACTION_SEND -> "➤"
            editorAction == EditorInfo.IME_ACTION_GO || editorAction == EditorInfo.IME_ACTION_NEXT -> "→"
            editorAction == EditorInfo.IME_ACTION_DONE -> "✓"
            else -> "⏎"
        }
        mic.alpha = if (field.password) 0.4f else 1f
        if (state == State.Idle) {
            if (field.password) show("Dictation is off in password fields", Tone.Muted) else show("Hold the mic and speak", Tone.Muted)
        }
    }

    private fun setState(value: State) {
        state = value
        if (!::hint.isInitialized) return
        hint.text = when (value) {
            State.Idle -> "Hold to talk · tap for hands-free"
            State.Holding -> "Release to write · slide up to cancel"
            State.HandsFree -> "Tap the mic when you're done"
            State.Finishing -> "Writing…"
        }
    }

    private fun show(text: String, tone: Tone) {
        if (!::status.isInitialized) return
        status.text = text
        status.setTextColor(
            color(
                when (tone) {
                    Tone.Muted -> R.color.tf_muted
                    Tone.Active -> R.color.tf_text
                    Tone.Ok -> R.color.tf_ok
                    Tone.Warn -> R.color.tf_warn
                },
            ),
        )
    }

    private fun haptic() {
        if (prefs.haptics && ::mic.isInitialized) mic.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
    }

    private val unload = Runnable { if (state == State.Idle) app.engine.unload() }

    private fun scheduleUnload() {
        main.removeCallbacks(unload)
        main.postDelayed(unload, IDLE_UNLOAD_MS)
    }

    companion object {
        private const val TAP_MS = 300L
        private const val PREVIEW_MS = 900L
        private const val IDLE_UNLOAD_MS = 3 * 60_000L
    }
}
