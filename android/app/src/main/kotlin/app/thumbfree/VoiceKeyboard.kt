package app.thumbfree

import android.content.Intent
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
import app.thumbfree.core.FieldInfo
import app.thumbfree.core.Insertion
import app.thumbfree.core.Language

/**
 * The ThumbFree voice keyboard. Hold the mic and speak; release to write the cleaned-up text where the cursor is.
 * A quick tap starts hands-free dictation (tap again to finish); sliding up before releasing cancels.
 * The microphone is on only during dictation, and audio never leaves the phone.
 */
class VoiceKeyboard : InputMethodService(), Dictation.Listener {
    private enum class State { Idle, Holding, HandsFree, Finishing }

    private enum class Tone { Muted, Active, Ok, Warn }

    private val main = Handler(Looper.getMainLooper())
    private var state = State.Idle
    private var field = FieldInfo(password = false, search = false, multiLine = true, private = false)
    private var email = false
    private var editorAction = EditorInfo.IME_ACTION_UNSPECIFIED
    private var noEnterAction = false
    private var lastText: String? = null

    private val dictation by lazy { Dictation(app, this) }
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
        email = Compose.isEmail(info.packageName, info.privateImeOptions)
        editorAction = info.imeOptions and EditorInfo.IME_MASK_ACTION
        noEnterAction = (info.imeOptions and EditorInfo.IME_FLAG_NO_ENTER_ACTION) != 0
        refresh()
    }

    override fun onFinishInputView(finishingInput: Boolean) {
        leaveField()
        super.onFinishInputView(finishingInput)
    }

    override fun onDestroy() {
        leaveField()
        main.removeCallbacksAndMessages(null)
        super.onDestroy()
    }

    /**
     * The text box is going away. A hold in progress is cancelled (the finger left), but hands-free dictation and a
     * transcript still being written are kept: never typed elsewhere, but saved in a notification to copy.
     */
    private fun leaveField() {
        when (state) {
            State.Holding -> dictation.cancel()
            State.HandsFree, State.Finishing -> {
                val private = field.private
                dictation.detach { result -> saveOrphan(result, private) }
            }
            State.Idle -> Unit
        }
        if (::mic.isInitialized) mic.recording = false
        setState(State.Idle)
    }

    private fun saveOrphan(result: Dictation.Transcript?, private: Boolean) {
        val text = result?.raw?.takeIf { it.isNotEmpty() }?.let { Compose.standalone(prefs, it) }
        if (!text.isNullOrEmpty() && !private) app.notifier.undelivered(text, "Your dictation wasn't typed: the text box closed.")
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
                    wantsCancel -> cancel()
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
        when (dictation.start()) {
            null -> Unit
            Dictation.Problem.NoPermission -> {
                show("Allow the microphone in ThumbFree first", Tone.Warn)
                openSetup()
                return false
            }
            Dictation.Problem.NoModel -> {
                show("Download the “${prefs.model().label}” speech model in ThumbFree first", Tone.Warn)
                openSetup()
                return false
            }
            Dictation.Problem.MicUnavailable -> {
                show("Couldn't start the microphone. Is another app using it?", Tone.Warn)
                return false
            }
            Dictation.Problem.Busy -> {
                show("Still writing the last dictation…", Tone.Muted)
                return false
            }
        }
        mic.recording = true
        haptic()
        val spec = dictation.model
        show(if (prefs.livePreview && spec != null && !spec.livePreview) "Listening… (no live preview with this model)" else "Listening…", Tone.Active)
        return true
    }

    private fun finish() {
        if (!dictation.recording) return
        setState(State.Finishing)
        mic.recording = false
        haptic()
        show("Writing…", Tone.Active)
        dictation.finish()
    }

    private fun cancel(silent: Boolean = false) {
        dictation.cancel()
        mic.recording = false
        setState(State.Idle)
        if (!silent) {
            haptic()
            show("Cancelled", Tone.Muted)
        }
    }

    override fun onLevel(level: Float) {
        if (::mic.isInitialized && dictation.recording) mic.level = level
    }

    override fun onPreview(text: String) {
        if (state == State.Holding || state == State.HandsFree) show(text.ifEmpty { "Listening…" }, Tone.Active)
    }

    override fun onWarning(message: String) = show(message, Tone.Warn)

    override fun onTranscript(result: Dictation.Transcript?) {
        // The recording may have stopped by itself (another app took the microphone).
        if (::mic.isInitialized) mic.recording = false
        setState(State.Idle)
        when {
            result == null -> show("Couldn't turn that into text. Please try again.", Tone.Warn)
            result.raw.isEmpty() -> show("Didn't catch anything. Hold the mic and speak.", Tone.Muted)
            else -> insert(result)
        }
    }

    private fun insert(result: Dictation.Transcript) {
        val ic = currentInputConnection ?: return show("Tap a text box first.", Tone.Warn)
        if (field.password) return show("Dictation is off in password fields", Tone.Warn)
        val before = ic.getTextBeforeCursor(200, 0)
        val text = Compose.text(prefs, result.raw, before, field, email)
        if (text.isEmpty()) return show("Didn't catch anything.", Tone.Muted)
        ic.commitText(Insertion.withSpacing(before, text), 1)
        if (!field.private) {
            lastText = text
            if (prefs.shareSamples) app.samples.save(result, text, prefs.language.name)
        }
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
        val spec = prefs.model()
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
            if (field.password) show("Dictation is off in password fields", Tone.Muted) else show("Hold the mic and speak · offline", Tone.Muted)
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

    companion object {
        private const val TAP_MS = 300L
    }
}
