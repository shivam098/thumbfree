package app.thumbfree

import android.Manifest
import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.view.accessibility.AccessibilityManager
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import app.thumbfree.core.Language
import app.thumbfree.core.Professions

/**
 * The first-run guide: why ThumbFree is different (offline), the microphone, the speech model, the keyboard, two
 * practice rounds (an email and a list), the bubble, notifications, an optional "what do you do", and the data choice.
 * Every step can be skipped; everything can be changed later in Settings.
 */
class OnboardingActivity : Activity() {
    private enum class Step { Welcome, Microphone, Model, Keyboard, TryEmail, TryList, Bubble, Notifications, Profession, Data }

    private val app get() = thumbFree
    private val prefs get() = app.prefs
    private var step = Step.Welcome

    private lateinit var progress: LinearLayout
    private lateinit var content: LinearLayout
    private lateinit var primary: Button
    private lateinit var secondary: Button
    private val modelsChanged: () -> Unit = { if (step == Step.Model) render() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        step = intent.getStringExtra(EXTRA_STEP)?.let { name -> Step.entries.firstOrNull { it.name == name } }
            ?: savedInstanceState?.getString("step")?.let { Step.valueOf(it) } ?: Step.Welcome

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpi(20), dpi(12), dpi(20), dpi(16))
            setOnApplyWindowInsetsListener { v, insets ->
                val (top, bottom) = if (Build.VERSION.SDK_INT >= 30) {
                    val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
                    bars.top to bars.bottom
                } else {
                    @Suppress("DEPRECATION")
                    insets.systemWindowInsetTop to insets.systemWindowInsetBottom
                }
                v.setPadding(dpi(20), dpi(12) + top, dpi(20), dpi(16) + bottom)
                insets
            }
        }
        val top = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        progress = LinearLayout(this)
        top.addView(progress, LinearLayout.LayoutParams(0, dpi(4), 1f))
        top.addView(
            TextView(this).apply {
                text = "Skip"
                textSize = 15f
                setTextColor(color(R.color.tf_muted))
                setPadding(dpi(16), dpi(10), 0, dpi(10))
                setOnClickListener { done() }
            },
        )
        root.addView(top)

        content = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        root.addView(
            ScrollView(this).apply {
                isFillViewport = true
                addView(content)
            },
            LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f),
        )
        primary = bigButton(filled = true)
        secondary = bigButton(filled = false)
        root.addView(primary, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dpi(56)).apply { topMargin = dpi(8) })
        root.addView(secondary, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dpi(52)).apply { topMargin = dpi(8) })
        setContentView(root)
        render()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putString("step", step.name)
    }

    override fun onResume() {
        super.onResume()
        app.models.addListener(modelsChanged)
        render()
    }

    override fun onPause() {
        app.models.removeListener(modelsChanged)
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // Back from a settings screen or the keyboard picker.
        if (hasFocus && step in setOf(Step.Keyboard, Step.Bubble)) render()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (step == Step.Microphone && micGranted()) next() else if (step == Step.Notifications) next() else render()
    }

    @Deprecated("Back goes to the previous step")
    override fun onBackPressed() {
        if (step.ordinal == 0) @Suppress("DEPRECATION") super.onBackPressed() else go(Step.entries[step.ordinal - 1])
    }

    private fun next() = if (step.ordinal + 1 < Step.entries.size) go(Step.entries[step.ordinal + 1]) else done()

    private fun go(to: Step) {
        step = to
        render()
    }

    private fun done() {
        prefs.onboarded = true
        startActivity(Intent(this, SetupActivity::class.java))
        finish()
    }

    // ---- steps ----

    private fun render() {
        if (!::content.isInitialized) return
        progress.removeAllViews()
        Step.entries.forEach { s ->
            progress.addView(
                View(this).apply { background = rounded(color(if (s.ordinal <= step.ordinal) R.color.tf_accent else R.color.tf_track), 2f) },
                LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f).apply { marginEnd = dpi(4) },
            )
        }
        content.removeAllViews()
        secondary.visibility = View.GONE
        primary.isEnabled = true
        when (step) {
            Step.Welcome -> welcome()
            Step.Microphone -> microphone()
            Step.Model -> model()
            Step.Keyboard -> keyboard()
            Step.TryEmail -> practice(
                "Now try writing an email",
                "Hey Tim, would love to connect. Are you available on Friday at 3 pm… actually 4 pm? Thanks, Ben.",
                email = true,
                success = { it.contains("\n\n") && it.contains("4") },
                well = "Well done. ThumbFree formats your emails and keeps only your correction.",
            )
            Step.TryList -> practice(
                "Now make a list",
                "Things to do today: order groceries, go on a walk, and call mom.",
                email = false,
                success = { it.contains("\n- ") },
                well = "Great. ThumbFree makes the list for you.",
            )
            Step.Bubble -> bubble()
            Step.Notifications -> notifications()
            Step.Profession -> profession()
            Step.Data -> data()
        }
    }

    private fun welcome() {
        title("Type with your voice.\nNo internet needed.")
        line("ThumbFree turns speech into clean, punctuated text in any app, entirely on this phone.")
        point("🔒", "Your voice never leaves this phone", "Speech is recognised right here. No account, no cloud, no recordings sent anywhere.")
        point("✈️", "Works offline", "On a plane, in the metro, with no signal: it's just as fast.")
        point("✍️", "Writes like you meant to", "No ums or “thik hai na”, punctuation added, emails and lists laid out for you.")
        point("🇮🇳", "English and Hinglish", "Hindi + English, typed in Roman letters.")
        primary("Get started") { next() }
    }

    private fun microphone() {
        title("Let ThumbFree hear you")
        line("The microphone is on only while you dictate: while you hold the mic, or until you tap it again. Audio stays in memory and is turned into text on this phone.")
        if (micGranted()) {
            done("Microphone allowed")
            primary("Next") { next() }
        } else {
            primary("Allow microphone") {
                val asked = getSharedPreferences("state", MODE_PRIVATE).getBoolean("asked_mic", false)
                if (asked && !shouldShowRequestPermissionRationale(Manifest.permission.RECORD_AUDIO)) {
                    startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.fromParts("package", packageName, null)))
                } else {
                    getSharedPreferences("state", MODE_PRIVATE).edit().putBoolean("asked_mic", true).apply()
                    requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 1)
                }
            }
            secondary("Not now") { next() }
        }
    }

    private fun model() {
        title("Download the speech model")
        line("This is what makes ThumbFree work offline. It downloads once, then never needs the internet again.")
        val choices = LinearLayout(this)
        fun choice(label: String, language: Language) = bigButton(filled = prefs.language == language).apply {
            text = label
            setOnClickListener {
                prefs.language = language
                render()
            }
        }
        choices.addView(choice("English", Language.English), LinearLayout.LayoutParams(0, dpi(52), 1f).apply { marginEnd = dpi(8) })
        choices.addView(choice("Hindi + English", Language.Hinglish), LinearLayout.LayoutParams(0, dpi(52), 1f))
        content.addView(choices, spaced(16, 12))

        val spec = prefs.model()
        val p = app.models.progress(spec)
        when {
            app.models.isInstalled(spec) -> {
                done("“${spec.label}” is ready")
                primary("Next") { next() }
            }
            p?.running == true -> {
                line("Downloading ${mb(p.done)} of ${mb(p.total)}…")
                content.addView(ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
                    max = 1000
                    this.progress = (p.done * 1000 / p.total.coerceAtLeast(1)).toInt()
                })
                line("You can carry on while it downloads.", muted = true)
                primary("Continue") { next() }
            }
            else -> {
                line("${mb(spec.bytes)}. Wi-Fi recommended.${p?.error?.let { "\n$it" } ?: ""}", muted = true)
                primary(if (p != null && p.done > 0) "Resume download" else "Download (${mb(spec.bytes)})") {
                    app.models.download(spec)
                    render()
                }
                secondary("Later") { next() }
            }
        }
    }

    private fun keyboard() {
        title("Turn on the ThumbFree keyboard")
        line("A voice keyboard you can switch to in any app. (Prefer your usual keyboard? The bubble, a few steps on, works with any keyboard.)")
        val imm = getSystemService(InputMethodManager::class.java)
        val enabled = imm.enabledInputMethodList.any { it.packageName == packageName }
        val current = Settings.Secure.getString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD).orEmpty().startsWith("$packageName/")
        task("1", "Switch on “ThumbFree voice”", enabled, "Open settings") { startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)) }
        task("2", "Switch to it (or tap the keyboard icon on any keyboard)", current, "Choose") { imm.showInputMethodPicker() }
        primary("Next") { next() }
    }

    private fun practice(heading: String, say: String, email: Boolean, success: (String) -> Boolean, well: String) {
        title(heading)
        line("Tap the box, switch to the ThumbFree keyboard (or use the bubble) and say:", muted = true)
        line("“$say”").apply { setTypeface(typeface, Typeface.ITALIC) }
        val result = TextView(this).apply {
            textSize = 16f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(color(R.color.tf_ok))
        }
        val box = EditText(this).apply {
            minLines = 5
            gravity = Gravity.TOP or Gravity.START
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
            if (email) privateImeOptions = Compose.EMAIL_OPTION
            hint = if (email) "Email" else "Notes"
            background = rounded(color(R.color.tf_surface), 14f)
            setPadding(dpi(14), dpi(12), dpi(14), dpi(12))
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit
                override fun afterTextChanged(s: Editable?) {
                    result.text = if (success(s?.toString().orEmpty())) well else ""
                }
            })
        }
        content.addView(box, spaced(14, 8))
        content.addView(result)
        content.addView(smallButton("Switch keyboard") { getSystemService(InputMethodManager::class.java).showInputMethodPicker() }, spaced(8, 0))
        box.requestFocus()
        primary("Next") { next() }
    }

    private fun bubble() {
        title("The bubble: dictate with any keyboard")
        line("Keep Gboard, SwiftKey or any keyboard you like. Whenever you type, a small ThumbFree mic floats beside it: tap to talk, tap again and your words appear. Drag it anywhere; long-press to hide it.")
        line(
            "To put text into other apps, Android asks for accessibility access. ThumbFree uses it only to find the text " +
                "box you're typing in and type your dictation there. It doesn't read, keep or send anything else on your screen.",
            muted = true,
        )
        if (bubbleEnabled()) {
            done("The bubble is on")
            primary("Next") { next() }
        } else {
            task("1", "Open “Installed apps” (or “Downloaded apps”), choose “ThumbFree bubble” and turn it on", false, null) {}
            task("2", "Leave “ThumbFree bubble shortcut” off", false, null) {}
            primary("Open settings") { openBubbleSettings() }
            secondary("Skip") { next() }
        }
    }

    private fun notifications() {
        title("Don't speak into the void")
        line("If a call or another app takes the microphone while you're talking, ThumbFree tells you straight away. And if a dictation can't be typed, it's kept in a notification for you to copy, so nothing you said is lost.")
        if (Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) {
            done("Notifications allowed")
            primary("Next") { next() }
        } else {
            primary("Allow notifications") { requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 2) }
            secondary("Skip") { next() }
        }
    }

    private fun profession() {
        title("What do you do?")
        line("Optional. ThumbFree adds your field's names and jargon to “Words I use often”, so they're spelled right from the start. This stays on your phone; you can edit the list in Settings.", muted = true)
        for (role in Professions.all.keys) {
            val chosen = prefs.profession == role
            content.addView(
                choiceCard(role, null, chosen) {
                    prefs.profession = role
                    prefs.vocabulary = Professions.addTo(prefs.vocabulary, role)
                    render()
                },
                spaced(6, 0),
            )
        }
        primary("Next") { next() }
        secondary("Skip") { next() }
    }

    private fun data() {
        title("Your voice stays yours")
        line("Choose how ThumbFree handles your recordings.", muted = true)
        content.addView(
            choiceCard("Keep everything private 🔒", "Recordings are never kept. Nothing is ever sent anywhere.", !prefs.shareSamples) {
                prefs.shareSamples = false
                render()
            },
            spaced(16, 0),
        )
        content.addView(
            choiceCard(
                "Help improve ThumbFree",
                "Keep my recordings, transcripts and the typed text on this phone so I can choose to send them to the " +
                    "ThumbFree team. Only dictations made with the ThumbFree keyboard, never from password or incognito " +
                    "fields. Nothing is sent automatically, and you can delete them anytime in Settings.",
                prefs.shareSamples,
            ) {
                prefs.shareSamples = true
                render()
            },
            spaced(10, 0),
        )
        primary("Finish") { done() }
    }

    // ---- pieces ----

    private fun micGranted() = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    private fun bubbleEnabled(): Boolean {
        val am = getSystemService(AccessibilityManager::class.java)
        return am.getEnabledAccessibilityServiceList(android.accessibilityservice.AccessibilityServiceInfo.FEEDBACK_ALL_MASK)
            .any { it.resolveInfo.serviceInfo.packageName == packageName }
    }

    private fun openBubbleSettings() = openBubbleSettings(this)

    private fun title(text: String) = content.addView(
        TextView(this).apply {
            this.text = text
            textSize = 30f
            typeface = Typeface.create(Typeface.SERIF, Typeface.NORMAL)
            setTextColor(color(R.color.tf_text))
            setLineSpacing(0f, 1.05f)
        },
        spaced(28, 12),
    )

    private fun line(text: String, muted: Boolean = false) = TextView(this).apply {
        this.text = text
        textSize = 16f
        setTextColor(color(if (muted) R.color.tf_muted else R.color.tf_text))
        setLineSpacing(0f, 1.15f)
        content.addView(this, spaced(6, 6))
    }

    private fun point(icon: String, head: String, body: String) {
        val row = LinearLayout(this)
        row.addView(TextView(this).apply {
            text = icon
            textSize = 22f
        }, LinearLayout.LayoutParams(dpi(44), LinearLayout.LayoutParams.WRAP_CONTENT))
        val texts = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        texts.addView(TextView(this).apply {
            text = head
            textSize = 17f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(color(R.color.tf_text))
        })
        texts.addView(TextView(this).apply {
            text = body
            textSize = 15f
            setTextColor(color(R.color.tf_muted))
        })
        row.addView(texts, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        content.addView(row, spaced(14, 0))
    }

    private fun done(text: String) = line("✓  $text").apply {
        setTextColor(color(R.color.tf_ok))
        typeface = Typeface.DEFAULT_BOLD
    }

    private fun task(number: String, text: String, done: Boolean, action: String?, onAction: () -> Unit) {
        val row = LinearLayout(this).apply {
            gravity = Gravity.CENTER_VERTICAL
            background = rounded(color(R.color.tf_surface), 14f)
            setPadding(dpi(14), dpi(12), dpi(14), dpi(12))
        }
        row.addView(TextView(this).apply {
            this.text = if (done) "✓" else number
            gravity = Gravity.CENTER
            setTextColor(color(R.color.tf_on_accent))
            typeface = Typeface.DEFAULT_BOLD
            background = rounded(color(if (done) R.color.tf_ok else R.color.tf_accent), 14f)
        }, LinearLayout.LayoutParams(dpi(28), dpi(28)).apply { marginEnd = dpi(12) })
        row.addView(TextView(this).apply {
            this.text = text
            textSize = 15f
            setTextColor(color(R.color.tf_text))
        }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        if (action != null && !done) row.addView(smallButton(action) { onAction() })
        content.addView(row, spaced(10, 0))
    }

    private fun choiceCard(head: String, body: String?, chosen: Boolean, onClick: () -> Unit) = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        background = rounded(color(if (chosen) R.color.tf_accent_soft else R.color.tf_surface), 16f)
        setPadding(dpi(18), dpi(14), dpi(18), dpi(14))
        isClickable = true
        setOnClickListener { onClick() }
        addView(TextView(this@OnboardingActivity).apply {
            text = if (chosen) "✓  $head" else head
            textSize = 17f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(color(R.color.tf_text))
        })
        if (body != null) addView(TextView(this@OnboardingActivity).apply {
            text = body
            textSize = 15f
            setTextColor(color(R.color.tf_muted))
        })
    }

    private fun bigButton(filled: Boolean) = Button(this).apply {
        isAllCaps = false
        textSize = 17f
        background = rounded(color(if (filled) R.color.tf_accent else R.color.tf_surface), 16f)
        setTextColor(if (filled) color(R.color.tf_on_accent) else color(R.color.tf_text))
    }

    private fun smallButton(label: String, onClick: () -> Unit) = Button(this).apply {
        text = label
        isAllCaps = false
        setOnClickListener { onClick() }
    }

    private fun primary(label: String, onClick: () -> Unit) {
        primary.text = label
        primary.setOnClickListener { onClick() }
    }

    private fun secondary(label: String, onClick: () -> Unit) {
        secondary.visibility = View.VISIBLE
        secondary.text = label
        secondary.setOnClickListener { onClick() }
    }

    private fun spaced(topDp: Int, bottomDp: Int) =
        LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            topMargin = dpi(topDp)
            bottomMargin = dpi(bottomDp)
        }

    private fun mb(bytes: Long) = "${(bytes + 500_000) / 1_000_000} MB"

    companion object {
        /** Opens on a given step (tests and "Set up the bubble" in Settings). */
        const val EXTRA_STEP = "step"

        /** The bubble's own settings page where Android offers one, else the accessibility list. */
        fun openBubbleSettings(activity: Activity) {
            val component = ComponentName(activity, BubbleService::class.java).flattenToString()
            val details = Intent("android.settings.ACCESSIBILITY_DETAILS_SETTINGS").putExtra(Intent.EXTRA_COMPONENT_NAME, component)
            try {
                activity.startActivity(details)
            } catch (e: android.content.ActivityNotFoundException) {
                activity.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            }
        }
    }
}
