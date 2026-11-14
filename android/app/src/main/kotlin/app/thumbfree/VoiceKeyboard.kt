package app.thumbfree

import android.content.Intent
import android.inputmethodservice.InputMethodService
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
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
 * The ThumbFree voice keyboard. Tap the mic and speak; tap it again and the cleaned-up text is written where the cursor
 * is. While recording you see your voice level, the time and the words so far; then it shows "Transcribing…". ✕ cancels.
 * The microphone is on only during dictation, and audio never leaves the phone.
 */
class VoiceKeyboard : InputMethodService(), Dictation.Listener {
    private enum class State { Idle, Recording, Transcribing }

    private enum class Tone { Muted, Active, Ok, Warn }

    private val main = Handler(Looper.getMainLooper())
    private var state = State.Idle
    private var field = FieldInfo(password = false, search = false, multiLine = true, private = false)
    private var email = false
    private var editorAction = EditorInfo.IME_ACTION_UNSPECIFIED
    private var noEnterAction = false
    private var lastText: String? = null

    private val dictation by lazy { Dictation(app, this) }
    private var startedAt = 0L

    private lateinit var status: TextView
    private lateinit var hint: TextView
    private lateinit var mic: MicButton
    private lateinit var languageKey: TextView
    private lateinit var enterKey: TextView
    private lateinit var cancelKey: TextView

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
        cancelKey = key("✕", "Cancel dictation") { cancel() }.apply {
            textSize = 16f
            visibility = View.GONE
        }
        top.addView(cancelKey, LinearLayout.LayoutParams(dpi(44), dpi(36)).apply { marginEnd = dpi(6) })
        top.addView(key("⚙", "ThumbFree settings") { openSetup() }.apply { textSize = 16f }, LinearLayout.LayoutParams(dpi(44), dpi(36)))
        root.addView(top)

        mic = MicButton(this)
        // Tap to start, tap again to finish: nothing to hold.
        mic.setOnClickListener { onMicTap() }
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
     * The text box is going away. A dictation still recording or being transcribed is never typed elsewhere: it's
     * finished and saved in a notification to copy.
     */
    private fun leaveField() {
        if (state == State.Recording || state == State.Transcribing) {
            val private = field.private
            clearLive()
            dictation.detach { result -> saveOrphan(result, private) }
        }
        liveComposing = false
        setState(State.Idle)
    }

    private fun saveOrphan(result: Dictation.Transcript?, private: Boolean) {
        val text = result?.raw?.takeIf { it.isNotEmpty() }?.let { Compose.standalone(prefs, it) }
        if (!text.isNullOrEmpty() && !private) app.notifier.undelivered(text, "Your dictation wasn't typed: the text box closed.")
    }

    // ---- dictation: tap to start, tap again to finish ----

    private fun onMicTap() {
        when (state) {
            State.Idle -> start()
            State.Recording -> finish()
            State.Transcribing -> show("Transcribing… one moment", Tone.Active)
        }
    }

    private fun start() {
        if (field.password) return show("Dictation is off in password fields", Tone.Warn)
        when (dictation.start()) {
            null -> Unit
            Dictation.Problem.NoPermission -> {
                show("Allow the microphone in ThumbFree first", Tone.Warn)
                return openSetup()
            }
            Dictation.Problem.NoModel -> {
                show("Download the “${prefs.model().label}” speech model in ThumbFree first", Tone.Warn)
                return openSetup()
            }
            Dictation.Problem.MicUnavailable -> return show("Couldn't start the microphone. Is another app using it?", Tone.Warn)
            Dictation.Problem.Busy -> return show("Still transcribing the last dictation…", Tone.Muted)
        }
        startedAt = SystemClock.elapsedRealtime()
        // Live typing replaces the words between here and the cursor as you speak; spacing is decided once, from here.
        liveBefore = currentInputConnection?.getTextBeforeCursor(200, 0)
        liveComposing = false
        liveSeen = false
        liveLost = false
        setState(State.Recording)
        haptic()
        val spec = dictation.model
        show(if (prefs.liveTyping && spec != null && !spec.livePreview) "Listening… (this model types when you finish)" else "Listening…", Tone.Active)
    }

    private fun finish() {
        if (!dictation.recording) return
        haptic()
        dictation.finish() // → onFinishing → Transcribing
    }

    private fun cancel() {
        if (state != State.Recording) return
        dictation.cancel()
        clearLive()
        setState(State.Idle)
        haptic()
        show("Cancelled", Tone.Muted)
    }

    override fun onLevel(level: Float) {
        if (::mic.isInitialized && state == State.Recording) mic.level = level
    }

    override fun onPreview(text: String) {
        if (state != State.Recording) return
        if (typeLive(text)) show("Listening…", Tone.Active) else show(text.ifEmpty { "Listening…" }, Tone.Active)
    }

    // ---- live typing: the words so far sit in the text box as composing (underlined) text until you finish ----

    private var liveBefore: CharSequence? = null
    private var liveComposing = false
    /** The editor has reported our composing text at least once (older reports may still arrive without it). */
    private var liveSeen = false
    /** The cursor moved away from the live text: stop touching it. */
    private var liveLost = false

    /** Puts [text] in the box as composing text; false when it can't (then the keyboard shows it). */
    private fun typeLive(text: String): Boolean {
        if (!prefs.liveTyping || field.password || liveLost) return false
        val ic = currentInputConnection ?: return false
        val live = if (text.isEmpty()) "" else Insertion.withSpacing(liveBefore, Compose.live(prefs, text, liveBefore, field))
        if (live.isEmpty() && !liveComposing) return true
        ic.setComposingText(live, 1)
        liveComposing = live.isNotEmpty()
        return true
    }

    /** Removes the live text (cancelled, or nothing was heard). */
    private fun clearLive() {
        if (liveComposing && !liveLost) currentInputConnection?.let {
            it.setComposingText("", 1)
            it.finishComposingText()
        }
        liveComposing = false
    }

    override fun onUpdateSelection(oldSelStart: Int, oldSelEnd: Int, newSelStart: Int, newSelEnd: Int, candidatesStart: Int, candidatesEnd: Int) {
        super.onUpdateSelection(oldSelStart, oldSelEnd, newSelStart, newSelEnd, candidatesStart, candidatesEnd)
        if (!liveComposing || liveLost) return
        if (candidatesStart >= 0) {
            liveSeen = true
        } else if (liveSeen) {
            // The person tapped elsewhere in the box, so the editor ended our composing text. Leave it be, and don't
            // type the dictation a second time somewhere else.
            liveLost = true
            liveComposing = false
            currentInputConnection?.finishComposingText()
        }
    }

    override fun onWarning(message: String) = show(message, Tone.Warn)

    /** Recording stopped (tapped, the 10-minute limit, or another app took the microphone): now transcribing. */
    override fun onFinishing(reason: String?) {
        setState(State.Transcribing)
        show(reason ?: "Transcribing…", if (reason == null) Tone.Active else Tone.Warn)
    }

    override fun onTranscript(result: Dictation.Transcript?) {
        setState(State.Idle)
        when {
            result == null -> {
                if (liveComposing) {
                    // Keep what was typed live rather than lose it.
                    currentInputConnection?.finishComposingText()
                    liveComposing = false
                    show("Couldn't finish the transcript; the live text was kept.", Tone.Warn)
                } else {
                    show("Couldn't turn that into text. Please try again.", Tone.Warn)
                }
            }
            result.raw.isEmpty() -> {
                clearLive()
                show("Didn't catch anything. Tap the mic and speak.", Tone.Muted)
            }
            else -> insert(result)
        }
    }
    private fun insert(result: Dictation.Transcript) {
        val ic = currentInputConnection ?: return show("Tap a text box first.", Tone.Warn)
        if (field.password) return show("Dictation is off in password fields", Tone.Warn)
        val live = liveComposing
        liveComposing = false
        if (liveLost) {
            val text = Compose.text(prefs, result.raw, liveBefore, field, email)
            if (!field.private) lastText = text
            return show("You moved the cursor, so the rest wasn't typed. Tap ↺ to insert the whole dictation.", Tone.Warn)
        }
        // With live text in the box, the text before it is what it was when you started; commitText replaces the live text.
        val before = if (live) liveBefore else ic.getTextBeforeCursor(200, 0)
        val text = Compose.text(prefs, result.raw, before, field, email)
        if (text.isEmpty()) {
            if (live) {
                ic.setComposingText("", 1)
                ic.finishComposingText()
            }
            return show("Didn't catch anything.", Tone.Muted)
        }
        ic.commitText(Insertion.withSpacing(before, text), 1)
        if (!field.private) {
            lastText = text
            if (prefs.shareSamples) app.samples.save(result, text, prefs.language.name)
        }
        show(text, Tone.Ok)
        if (prefs.switchBack && Build.VERSION.SDK_INT >= 28) switchToPreviousInputMethod()
    }

    // ---- keys ----

    /** Keys would replace or split the live text: they wait until the dictation is typed. */
    private fun liveBusy(): Boolean {
        if (!liveComposing || state == State.Idle) return false
        show("Tap the mic to finish dictating first", Tone.Muted)
        return true
    }

    private fun commit(text: String) {
        if (liveBusy()) return
        currentInputConnection?.commitText(text, 1)
    }

    private val repeatDelete = object : Runnable {
        override fun run() {
            deleteOne()
            main.postDelayed(this, 60)
        }
    }

    private fun deleteOne() {
        if (liveBusy()) return main.removeCallbacks(repeatDelete)
        val ic = currentInputConnection ?: return
        if (!ic.getSelectedText(0).isNullOrEmpty()) ic.commitText("", 1) else sendDownUpKeyEvents(KeyEvent.KEYCODE_DEL)
    }

    private fun enter() {
        if (liveBusy()) return
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
        if (liveBusy()) return
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
            if (field.password) show("Dictation is off in password fields", Tone.Muted) else show("Tap the mic and speak · offline", Tone.Muted)
        }
    }

    private fun setState(value: State) {
        state = value
        if (!::hint.isInitialized) return
        mic.recording = value == State.Recording
        mic.transcribing = value == State.Transcribing
        cancelKey.visibility = if (value == State.Recording) View.VISIBLE else View.GONE
        main.removeCallbacks(clock)
        hint.text = when (value) {
            State.Idle -> "Tap to talk"
            State.Recording -> "0:00 · tap when you're done"
            State.Transcribing -> "Transcribing…"
        }
        if (value == State.Recording) main.postDelayed(clock, 1000)
    }

    /** "0:07 · tap when you're done" while recording. */
    private val clock = object : Runnable {
        override fun run() {
            if (state != State.Recording) return
            val s = (SystemClock.elapsedRealtime() - startedAt) / 1000
            hint.text = "${s / 60}:${"%02d".format(s % 60)} · tap when you're done"
            main.postDelayed(this, 1000 - (SystemClock.elapsedRealtime() - startedAt) % 1000)
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

}
