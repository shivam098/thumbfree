package app.thumbfree

import android.accessibilityservice.AccessibilityService
import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Intent
import android.graphics.PixelFormat
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PersistableBundle
import android.provider.Settings
import android.text.TextUtils
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityWindowInfo
import android.widget.LinearLayout
import android.widget.TextView
import app.thumbfree.core.FieldInfo
import app.thumbfree.core.Insertion
import kotlin.math.abs

/**
 * The ThumbFree bubble: a floating mic that appears next to your usual keyboard whenever you're typing in another
 * app. Tap it to dictate, tap again to finish; the text is typed where your cursor is. Drag it to move it, and
 * long-press it to hide it until the next text box.
 *
 * This is an accessibility service only so it can see which text box has focus and put the dictation there. It doesn't
 * read or keep anything else on screen, and nothing leaves the phone.
 */
class BubbleService : AccessibilityService(), Dictation.Listener {
    private val main = Handler(Looper.getMainLooper())
    private val app get() = thumbFree
    private val dictation by lazy { Dictation(app, this) }

    private lateinit var windows: WindowManager
    private lateinit var params: WindowManager.LayoutParams
    private lateinit var container: LinearLayout
    private lateinit var mic: MicButton
    private lateinit var pill: TextView
    private var attached = false

    /** The text box the current dictation is for. */
    private var target: AccessibilityNodeInfo? = null
    /** Long-pressed away: hidden until focus moves to another text box. */
    private var dismissed: AccessibilityNodeInfo? = null
    private var waitingForMic = false

    override fun onServiceConnected() {
        instance = this
        windows = getSystemService(WindowManager::class.java)
        buildBubble()
        scheduleCheck()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = scheduleCheck()

    override fun onInterrupt() = Unit

    override fun onDestroy() {
        instance = null
        dictation.cancel()
        MicService.stop(this)
        main.removeCallbacksAndMessages(null)
        if (attached) runCatching { windows.removeView(container) }
        attached = false
        super.onDestroy()
    }

    // ---- when to show ----

    private fun scheduleCheck() {
        main.removeCallbacks(check)
        main.postDelayed(check, 120)
    }

    private val check = Runnable {
        val field = focusedField()
        if (dismissed != null && field != dismissed) dismissed = null
        val ownKeyboard = Settings.Secure.getString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD).orEmpty().startsWith("$packageName/")
        val wanted = dictation.busy || waitingForMic ||
            (app.prefs.bubble && field != null && !field.isPassword && !ownKeyboard && dismissed == null && keyboardShown())
        show(wanted)
    }

    /** The focused text box in another app, if any. */
    private fun focusedField(): AccessibilityNodeInfo? {
        val node = runCatching { findFocus(AccessibilityNodeInfo.FOCUS_INPUT) }.getOrNull() ?: return null
        if (!node.isEditable || node.packageName == packageName) return null
        return node
    }

    private fun keyboardShown() = runCatching { getWindows().any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD } }.getOrDefault(true)

    private fun show(visible: Boolean) {
        if (visible == attached) return
        if (visible) {
            runCatching { windows.addView(container, params) }.onSuccess { attached = true }
        } else {
            runCatching { windows.removeView(container) }
            attached = false
        }
    }

    // ---- the bubble ----

    private fun buildBubble() {
        mic = MicButton(this).apply { contentDescription = "ThumbFree: tap to dictate" }
        pill = TextView(this).apply {
            textSize = 14f
            maxLines = 3
            ellipsize = TextUtils.TruncateAt.START
            maxWidth = (resources.displayMetrics.widthPixels * 0.62f).toInt()
            setTextColor(0xFFFFFFFF.toInt())
            background = rounded(0xE6202124.toInt(), 16f)
            setPadding(dpi(12), dpi(8), dpi(12), dpi(8))
            visibility = View.GONE
        }
        container = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        arrange()
        params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            // Never take focus from the text box, and let touches outside the bubble through.
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or (if (app.prefs.bubbleLeft) Gravity.START else Gravity.END)
            x = dpi(6)
            y = app.prefs.bubbleY.takeIf { it >= 0 } ?: (resources.displayMetrics.heightPixels * 0.42f).toInt()
        }
        mic.setOnTouchListener(dragger())
        mic.setOnClickListener { onTap() } // TalkBack
    }

    /** Pill on the inner side of the mic. */
    private fun arrange() {
        container.removeAllViews()
        val size = dpi(58)
        val micParams = LinearLayout.LayoutParams(size, size)
        val pillParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            marginStart = dpi(4)
            marginEnd = dpi(4)
        }
        if (app.prefs.bubbleLeft) {
            container.addView(mic, micParams)
            container.addView(pill, pillParams)
        } else {
            container.addView(pill, pillParams)
            container.addView(mic, micParams)
        }
    }

    private fun dragger() = object : View.OnTouchListener {
        private val slop = ViewConfiguration.get(this@BubbleService).scaledTouchSlop
        private var downX = 0f
        private var downY = 0f
        private var startX = 0
        private var startY = 0
        private var dragging = false
        private val longPress = Runnable {
            dragging = true // swallow the up
            mic.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
            if (!dictation.busy) {
                dismissed = focusedField()
                show(false)
            }
        }

        override fun onTouch(v: View, e: MotionEvent): Boolean {
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = e.rawX
                    downY = e.rawY
                    startX = params.x
                    startY = params.y
                    dragging = false
                    main.postDelayed(longPress, ViewConfiguration.getLongPressTimeout().toLong())
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = e.rawX - downX
                    val dy = e.rawY - downY
                    if (!dragging && (abs(dx) > slop || abs(dy) > slop)) {
                        dragging = true
                        main.removeCallbacks(longPress)
                    }
                    if (dragging && attached) {
                        params.x = (startX + if (app.prefs.bubbleLeft) dx else -dx).toInt().coerceAtLeast(0)
                        params.y = (startY + dy).toInt().coerceAtLeast(0)
                        windows.updateViewLayout(container, params)
                    }
                }
                MotionEvent.ACTION_UP -> {
                    main.removeCallbacks(longPress)
                    if (dragging) snap(e.rawX) else onTap()
                }
                MotionEvent.ACTION_CANCEL -> main.removeCallbacks(longPress)
            }
            return true
        }
    }

    /** Snaps to the nearer side of the screen and remembers the place. */
    private fun snap(rawX: Float) {
        val left = rawX < resources.displayMetrics.widthPixels / 2f
        app.prefs.bubbleLeft = left
        app.prefs.bubbleY = params.y
        params.gravity = Gravity.TOP or (if (left) Gravity.START else Gravity.END)
        params.x = dpi(6)
        arrange()
        if (attached) windows.updateViewLayout(container, params)
    }

    private fun say(text: String?, hideAfterMs: Long = 0) {
        main.removeCallbacks(hidePill)
        if (text.isNullOrEmpty()) {
            pill.visibility = View.GONE
        } else {
            pill.text = text
            pill.visibility = View.VISIBLE
            if (hideAfterMs > 0) main.postDelayed(hidePill, hideAfterMs)
        }
    }

    private val hidePill = Runnable {
        pill.visibility = View.GONE
        scheduleCheck()
    }

    // ---- dictation ----

    private fun onTap() {
        mic.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
        when {
            dictation.recording -> finishDictation()
            dictation.busy || waitingForMic -> Unit
            else -> startDictation()
        }
    }

    private fun startDictation() {
        val field = focusedField() ?: return say("Tap a text box first", 2500)
        if (field.isPassword) return say("Dictation is off in password fields", 2500)
        if (checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != android.content.pm.PackageManager.PERMISSION_GRANTED ||
            !app.models.isInstalled(app.prefs.model())
        ) {
            say("Finish setting up ThumbFree first", 3000)
            startActivity(Intent(this, SetupActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            return
        }
        target = field
        waitingForMic = true
        say("Starting…")
        if (!MicService.start(this)) onMicReady(false)
    }

    /** Called by [MicService] once it runs in the foreground (or couldn't). */
    fun onMicReady(ok: Boolean) {
        if (!waitingForMic) {
            if (ok && !dictation.recording) MicService.stop(this)
            return
        }
        waitingForMic = false
        if (!ok) {
            MicService.stop(this)
            return say("Android didn't let ThumbFree use the microphone. Try again.", 4000)
        }
        val problem = dictation.start()
        if (problem != null) {
            MicService.stop(this)
            return say(
                when (problem) {
                    Dictation.Problem.MicUnavailable -> "Couldn't start the microphone. Is another app using it?"
                    Dictation.Problem.Busy -> "Still writing the last dictation…"
                    else -> "Finish setting up ThumbFree first"
                },
                3500,
            )
        }
        mic.recording = true
        say("Listening… tap to finish")
    }

    private fun finishDictation() {
        dictation.finish() // → onFinishing shows "Transcribing…"
        MicService.stop(this) // the transcript is made without the microphone
    }

    override fun onFinishing(reason: String?) {
        mic.recording = false
        mic.transcribing = true
        say(reason ?: "Transcribing…")
        MicService.stop(this)
    }

    override fun onLevel(level: Float) {
        if (dictation.recording) mic.level = level
    }

    override fun onPreview(text: String) {
        if (dictation.recording) say(text.ifEmpty { "Listening… tap to finish" })
    }

    override fun onWarning(message: String) = say(message)

    override fun onTranscript(result: Dictation.Transcript?) {
        MicService.stop(this)
        mic.recording = false
        mic.transcribing = false
        // Only the text box the dictation was started in: never whatever has focus now.
        val node = target?.takeIf { it.refresh() }
        target = null
        when {
            result == null -> say("Couldn't turn that into text. Please try again.", 3500)
            result.raw.isEmpty() -> say("Didn't catch anything", 2500)
            node == null || node.isPassword -> {
                val text = Compose.standalone(app.prefs, result.raw)
                app.notifier.undelivered(text, "The text box closed before your dictation was ready.")
                say("Saved in your notifications: the text box closed", 3500)
            }
            else -> type(node, result)
        }
        scheduleCheck()
    }

    private fun type(node: AccessibilityNodeInfo, result: Dictation.Transcript) {
        val existing = if (node.isShowingHintText) "" else node.text?.toString().orEmpty()
        var start = node.textSelectionStart
        var end = node.textSelectionEnd
        if (start < 0 || end < 0 || start > existing.length || end > existing.length) {
            start = existing.length
            end = existing.length
        }
        if (start > end) start = end.also { end = start }
        val before = existing.substring(0, start)
        val field = FieldInfo.of(node.inputType, 0).let { if (node.isMultiLine) it.copy(multiLine = true) else it }
        val text = Compose.text(app.prefs, result.raw, before, field, Compose.isEmail(node.packageName?.toString(), null))
        if (text.isEmpty()) return say("Didn't catch anything", 2500)
        val spaced = Insertion.withSpacing(before, text)

        if (setText(node, existing, start, end, spaced) || paste(node, spaced)) {
            // No samples from the bubble: another app's field can't say whether it's incognito.
            say(text, 2500)
        } else {
            app.notifier.undelivered(text, "This app didn't accept the text.")
            say("This app didn't accept the text. It's in your notifications.", 4000)
        }
    }

    /** Replaces the selection directly: keeps your clipboard untouched. Works in most native text boxes. */
    private fun setText(node: AccessibilityNodeInfo, existing: String, start: Int, end: Int, insert: String): Boolean {
        if (node.className?.contains("WebView") == true) return false
        if (node.actionList.none { it.id == AccessibilityNodeInfo.ACTION_SET_TEXT }) return false
        val updated = existing.substring(0, start) + insert + existing.substring(end)
        val args = Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, updated) }
        if (!node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)) return false
        val cursor = start + insert.length
        node.performAction(
            AccessibilityNodeInfo.ACTION_SET_SELECTION,
            Bundle().apply {
                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, cursor)
                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, cursor)
            },
        )
        return true
    }

    /** Pastes through the clipboard (rich editors and web pages). Your previous clipboard is put back when readable. */
    private fun paste(node: AccessibilityNodeInfo, text: String): Boolean {
        val clipboard = getSystemService(ClipboardManager::class.java)
        val previous = runCatching { clipboard.primaryClip }.getOrNull()
        val clip = ClipData.newPlainText("ThumbFree dictation", text)
        if (Build.VERSION.SDK_INT >= 33) {
            clip.description.extras = PersistableBundle().apply { putBoolean(ClipDescription.EXTRA_IS_SENSITIVE, true) }
        }
        clipboard.setPrimaryClip(clip)
        val ok = node.performAction(AccessibilityNodeInfo.ACTION_PASTE)
        if (previous != null) main.postDelayed({ clipboard.setPrimaryClip(previous) }, 600)
        return ok
    }

    companion object {
        /** The running service (main thread), so [MicService] can report back. */
        var instance: BubbleService? = null
            private set
    }
}
