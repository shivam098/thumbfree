package app.thumbfree

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import app.thumbfree.core.Language
import app.thumbfree.core.ModelCatalog
import app.thumbfree.core.ModelSpec
import app.thumbfree.whisper.WhisperModel

/** Getting started (three steps), speech models, options and the personal dictionary. */
class SetupActivity : Activity() {
    private val app get() = thumbFree
    private val prefs get() = app.prefs

    private lateinit var micStep: Step
    private lateinit var enableStep: Step
    private lateinit var pickStep: Step
    private val modelRows = mutableListOf<ModelRow>()
    private lateinit var languageNote: TextView
    private lateinit var englishButton: Button
    private lateinit var hinglishButton: Button
    private lateinit var dictionary: EditText

    private val modelsChanged: () -> Unit = { refresh() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpi(20), dpi(20), dpi(20), dpi(28))
        }
        val scroll = ScrollView(this).apply {
            isFillViewport = true
            addView(column)
            setOnApplyWindowInsetsListener { v, insets ->
                val (top, bottom) = if (Build.VERSION.SDK_INT >= 30) {
                    val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
                    bars.top to bars.bottom
                } else {
                    @Suppress("DEPRECATION")
                    insets.systemWindowInsetTop to insets.systemWindowInsetBottom
                }
                v.setPadding(0, top, 0, bottom)
                insets
            }
        }
        setContentView(scroll)

        column.addView(text("ThumbFree", 30f, bold = true))
        column.addView(text("Type anywhere with your voice. Speech is turned into text on this phone; nothing is uploaded.", 15f, muted = true).spaced(4, 16))

        column.addView(heading("Get started"))
        micStep = step(column, "1", "Allow the microphone", "Used only while you hold the mic key.", "Allow") { askMicrophone() }
        enableStep = step(column, "2", "Turn on the ThumbFree keyboard", "Switch on “ThumbFree voice” in the list.", "Open settings") {
            startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS))
        }
        pickStep = step(column, "3", "Switch to it", "Or tap the keyboard icon at the bottom of any keyboard.", "Choose keyboard") {
            getSystemService(InputMethodManager::class.java).showInputMethodPicker()
        }

        column.addView(heading("Language"))
        val languages = LinearLayout(this)
        englishButton = button("English") { setLanguage(Language.English) }
        hinglishButton = button("Hindi + English") { setLanguage(Language.Hinglish) }
        languages.addView(englishButton, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply { marginEnd = dpi(8) })
        languages.addView(hinglishButton, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        column.addView(languages)
        languageNote = text("", 13f, muted = true).spaced(6, 4)
        column.addView(languageNote)
        column.addView(switch("Accurate English", "Bigger model: fewer mistakes, a bit slower", prefs.accurateEnglish) {
            prefs.accurateEnglish = it
            refresh()
        })

        column.addView(heading("Speech models"))
        column.addView(text("Download once; they work offline after that.", 13f, muted = true).spaced(0, 8))
        for (spec in ModelCatalog.all) modelRows += ModelRow(spec).also { column.addView(it.view) }

        column.addView(heading("Writing"))
        column.addView(switch("Remove filler words", "um, uh, “you know”, “thik hai na”…", prefs.removeFillers) { prefs.removeFillers = it })
        column.addView(switch("Spoken punctuation", "Say “comma”, “full stop”, “new line”", prefs.spokenPunctuation) { prefs.spokenPunctuation = it })
        column.addView(switch("Live preview", "Show words as you speak (uses more battery)", prefs.livePreview) { prefs.livePreview = it })
        column.addView(switch("Return to my keyboard", "Switch back after each dictation", prefs.switchBack) { prefs.switchBack = it })
        column.addView(switch("Vibrate", "A small tap when recording starts and stops", prefs.haptics) { prefs.haptics = it })

        column.addView(heading("Personal dictionary"))
        column.addView(text("One per line: what it hears = what to type. For example: journals = genres", 13f, muted = true).spaced(0, 6))
        dictionary = EditText(this).apply {
            setText(prefs.dictionary)
            hint = "thumb free = ThumbFree"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            minLines = 3
            gravity = Gravity.TOP or Gravity.START
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit
                override fun afterTextChanged(s: Editable?) {
                    prefs.dictionary = s?.toString().orEmpty()
                }
            })
        }
        column.addView(dictionary)

        column.addView(heading("Try it"))
        column.addView(EditText(this).apply {
            hint = "Tap here, switch to ThumbFree, hold the mic and talk"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
            minLines = 3
            gravity = Gravity.TOP or Gravity.START
        })

        column.addView(heading("About"))
        val engine = runCatching { WhisperModel.engineVariant }.getOrDefault("unavailable")
        val version = packageManager.getPackageInfo(packageName, 0).versionName
        column.addView(
            text(
                "Version $version · speech engine: whisper.cpp ($engine)\n" +
                    "Audio is kept in memory only while you dictate and is never saved or sent anywhere. " +
                    "The internet is used only to download the speech models.",
                13f, muted = true,
            ),
        )
        column.addView(button("Source code") { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://github.com/shivam098/thumbfree"))) }.spaced(8, 0))
    }

    override fun onResume() {
        super.onResume()
        app.models.addListener(modelsChanged)
        refresh()
    }

    override fun onPause() {
        app.models.removeListener(modelsChanged)
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) refresh() // back from the keyboard picker
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        refresh()
    }

    private fun askMicrophone() {
        if (shouldShowRequestPermissionRationale(Manifest.permission.RECORD_AUDIO) || !prefsAsked()) {
            getSharedPreferences("state", MODE_PRIVATE).edit().putBoolean("asked_mic", true).apply()
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 1)
        } else {
            // Denied for good: Android won't ask again, so open the app's settings page.
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
        }
    }

    private fun prefsAsked() = getSharedPreferences("state", MODE_PRIVATE).getBoolean("asked_mic", false)

    private fun setLanguage(language: Language) {
        prefs.language = language
        val spec = ModelCatalog.forLanguage(language, prefs.accurateEnglish)
        if (!app.models.isInstalled(spec)) app.models.download(spec)
        refresh()
    }

    private fun refresh() {
        val mic = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        val imm = getSystemService(InputMethodManager::class.java)
        val enabled = imm.enabledInputMethodList.any { it.packageName == packageName }
        val current = Settings.Secure.getString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD).orEmpty()
        val picked = current.startsWith("$packageName/")
        micStep.set(done = mic, available = true)
        enableStep.set(done = enabled, available = true)
        pickStep.set(done = picked, available = enabled)

        val hinglish = prefs.language == Language.Hinglish
        englishButton.isSelected = !hinglish
        hinglishButton.isSelected = hinglish
        styleChoice(englishButton, !hinglish)
        styleChoice(hinglishButton, hinglish)
        val spec = ModelCatalog.forLanguage(prefs.language, prefs.accurateEnglish)
        languageNote.text = when {
            app.models.isInstalled(spec) -> "Using “${spec.label}”."
            else -> "Needs “${spec.label}” (${mb(spec.bytes)}). Download it below."
        }
        modelRows.forEach { it.refresh(it.spec == spec) }
    }

    // ---- pieces ----

    private inner class Step(val badge: TextView, val action: Button) {
        fun set(done: Boolean, available: Boolean) {
            badge.text = if (done) "✓" else badge.tag as String
            badge.background = rounded(color(if (done) R.color.tf_ok else R.color.tf_accent), 14f)
            action.visibility = if (done) View.GONE else View.VISIBLE
            action.isEnabled = available
        }
    }

    private fun step(parent: LinearLayout, number: String, title: String, detail: String, actionLabel: String, onAction: () -> Unit): Step {
        val row = card()
        row.gravity = Gravity.CENTER_VERTICAL
        val badge = TextView(this).apply {
            tag = number
            text = number
            gravity = Gravity.CENTER
            setTextColor(0xFFFFFFFF.toInt())
            typeface = Typeface.DEFAULT_BOLD
        }
        row.addView(badge, LinearLayout.LayoutParams(dpi(28), dpi(28)).apply { marginEnd = dpi(12) })
        val texts = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        texts.addView(text(title, 16f, bold = true))
        texts.addView(text(detail, 13f, muted = true))
        row.addView(texts, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        val action = button(actionLabel) { onAction() }
        row.addView(action)
        parent.addView(row)
        return Step(badge, action)
    }

    private inner class ModelRow(val spec: ModelSpec) {
        val view = card().apply { orientation = LinearLayout.VERTICAL }
        private val title = text(spec.label, 16f, bold = true)
        private val status = text("", 13f, muted = true)
        private val bar = ProgressBar(this@SetupActivity, null, android.R.attr.progressBarStyleHorizontal).apply { max = 1000 }
        private val action = button("") { onAction() }

        init {
            val top = LinearLayout(this@SetupActivity).apply { gravity = Gravity.CENTER_VERTICAL }
            val texts = LinearLayout(this@SetupActivity).apply { orientation = LinearLayout.VERTICAL }
            texts.addView(title)
            texts.addView(status)
            top.addView(texts, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            top.addView(action)
            view.addView(top)
            view.addView(bar)
        }

        private fun onAction() {
            val store = app.models
            when {
                store.progress(spec)?.running == true -> store.cancel(spec)
                store.isInstalled(spec) -> store.delete(spec)
                else -> store.download(spec)
            }
            this@SetupActivity.refresh()
        }

        fun refresh(inUse: Boolean) {
            val store = app.models
            val p = store.progress(spec)
            title.text = if (inUse) "${spec.label} · in use" else spec.label
            when {
                p?.running == true -> {
                    status.text = "Downloading… ${mb(p.done)} of ${mb(p.total)}"
                    bar.visibility = View.VISIBLE
                    bar.progress = (p.done * 1000 / p.total.coerceAtLeast(1)).toInt()
                    action.text = "Cancel"
                }
                store.isInstalled(spec) -> {
                    status.text = "Downloaded · ${mb(spec.bytes)}"
                    bar.visibility = View.GONE
                    action.text = "Delete"
                }
                else -> {
                    status.text = p?.error?.let { "$it · ${mb(spec.bytes)}" } ?: mb(spec.bytes)
                    bar.visibility = View.GONE
                    action.text = if (p != null && p.done > 0) "Resume" else "Download"
                }
            }
        }
    }

    private fun card() = LinearLayout(this).apply {
        background = rounded(color(R.color.tf_surface), 14f)
        setPadding(dpi(14), dpi(12), dpi(14), dpi(12))
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dpi(8) }
    }

    private fun heading(label: String) = text(label, 13f, bold = true).apply {
        setTextColor(color(R.color.tf_accent))
        isAllCaps = true
        letterSpacing = 0.06f
    }.spaced(20, 8)

    private fun text(value: String, size: Float, bold: Boolean = false, muted: Boolean = false) = TextView(this).apply {
        text = value
        textSize = size
        setTextColor(color(if (muted) R.color.tf_muted else R.color.tf_text))
        if (bold) typeface = Typeface.DEFAULT_BOLD
    }

    private fun button(label: String, onClick: () -> Unit) = Button(this).apply {
        text = label
        isAllCaps = false
        setOnClickListener { onClick() }
    }

    private fun styleChoice(button: Button, chosen: Boolean) {
        button.background = rounded(color(if (chosen) R.color.tf_accent else R.color.tf_surface), 12f)
        button.setTextColor(if (chosen) 0xFFFFFFFF.toInt() else color(R.color.tf_text))
    }

    private fun switch(title: String, detail: String, checked: Boolean, onChange: (Boolean) -> Unit) = card().apply {
        gravity = Gravity.CENTER_VERTICAL
        val texts = LinearLayout(this@SetupActivity).apply { orientation = LinearLayout.VERTICAL }
        texts.addView(text(title, 16f))
        texts.addView(text(detail, 13f, muted = true))
        addView(texts, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        val toggle = Switch(this@SetupActivity).apply {
            isChecked = checked
            contentDescription = title
            setOnCheckedChangeListener { _, value -> onChange(value) }
        }
        addView(toggle)
        setOnClickListener { toggle.toggle() }
    }

    private fun <T : View> T.spaced(topDp: Int, bottomDp: Int): T = apply {
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            topMargin = dpi(topDp)
            bottomMargin = dpi(bottomDp)
        }
    }

    private fun mb(bytes: Long) = "${(bytes + 500_000) / 1_000_000} MB"
}
