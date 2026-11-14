package app.thumbfree

import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.text.Editable
import android.text.InputType
import android.text.TextUtils
import android.text.TextWatcher
import android.text.format.DateUtils
import android.view.View
import android.view.WindowInsets
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import app.thumbfree.core.History
import app.thumbfree.core.HistoryEntry

/** Your recent dictations, kept on this phone: tap one to copy it, hold for more. */
class HistoryActivity : Activity() {
    private val app get() = thumbFree
    private val store get() = app.history

    private lateinit var list: LinearLayout
    private lateinit var note: TextView
    private lateinit var search: EditText
    private lateinit var clearButton: Button

    private val changed: () -> Unit = { render() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Dictations can be private: keep them out of the recent-apps preview.
        if (Build.VERSION.SDK_INT >= 33) setRecentsScreenshotEnabled(false)
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

        column.addView(text("History", 30f, bold = true))
        column.addView(
            text("Your dictations from the last 30 days, kept on this phone only. Tap one to copy it; hold it to share or delete.", 14f, muted = true)
                .spaced(4, 12),
        )
        val toggle = Switch(this).apply {
            text = "Keep history"
            textSize = 16f
            setTextColor(color(R.color.tf_text))
            isChecked = app.prefs.keepHistory
            setOnCheckedChangeListener { view, on ->
                if (on) {
                    app.prefs.keepHistory = true
                    render()
                } else if (store.entries().isEmpty()) {
                    app.prefs.keepHistory = false
                    render()
                } else {
                    AlertDialog.Builder(this@HistoryActivity)
                        .setMessage("Stop keeping history? Everything in it will be deleted.")
                        .setPositiveButton("Turn off") { _, _ ->
                            app.prefs.keepHistory = false
                            store.clear()
                        }
                        .setNegativeButton("Cancel") { _, _ -> view.isChecked = true }
                        .setOnCancelListener { view.isChecked = true }
                        .show()
                }
            }
        }
        column.addView(toggle.spaced(0, 8))
        search = EditText(this).apply {
            hint = "Search"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            isSingleLine = true
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit
                override fun afterTextChanged(s: Editable?) = render()
            })
        }
        column.addView(search.spaced(0, 8))
        note = text("", 14f, muted = true).spaced(4, 8)
        column.addView(note)
        list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        column.addView(list)
        clearButton = Button(this).apply {
            text = "Clear history"
            isAllCaps = false
            setOnClickListener {
                AlertDialog.Builder(this@HistoryActivity)
                    .setMessage("Delete every dictation in History?")
                    .setPositiveButton("Delete") { _, _ -> store.clear() }
                    .setNegativeButton("Cancel", null)
                    .show()
            }
        }
        column.addView(clearButton.spaced(12, 0))
    }

    override fun onResume() {
        super.onResume()
        store.addListener(changed)
        render()
    }

    override fun onPause() {
        store.removeListener(changed)
        super.onPause()
    }

    private fun render() {
        if (!::list.isInitialized) return
        val all = store.entries()
        val shown = History.search(all, search.text.toString())
        list.removeAllViews()
        val now = System.currentTimeMillis()
        for (entry in shown) list.addView(row(entry, now))
        note.text = when {
            !app.prefs.keepHistory -> "History is off. New dictations aren't kept."
            all.isEmpty() -> "Nothing yet. Your dictations will appear here."
            shown.isEmpty() -> "No dictation contains “${search.text.toString().trim()}”."
            else -> ""
        }
        note.visibility = if (note.text.isEmpty()) View.GONE else View.VISIBLE
        search.visibility = if (all.size > 3) View.VISIBLE else View.GONE
        clearButton.visibility = if (all.isEmpty()) View.GONE else View.VISIBLE
    }

    private fun row(entry: HistoryEntry, now: Long) = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        background = rounded(color(R.color.tf_surface), 14f)
        setPadding(dpi(14), dpi(12), dpi(14), dpi(12))
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dpi(8) }
        addView(text(entry.text, 16f).apply {
            maxLines = 6
            ellipsize = TextUtils.TruncateAt.END
        })
        val label = store.appLabel(entry.app)
        val time = DateUtils.getRelativeTimeSpanString(entry.time, now, DateUtils.MINUTE_IN_MILLIS)
        addView(text(if (label.isEmpty()) "$time" else "$label · $time", 13f, muted = true).spaced(6, 0))
        isClickable = true
        isFocusable = true
        contentDescription = "${entry.text}. Tap to copy."
        setOnClickListener { copy(entry) }
        setOnLongClickListener {
            AlertDialog.Builder(this@HistoryActivity)
                .setItems(arrayOf("Copy", "Share…", "Delete")) { _, which ->
                    when (which) {
                        0 -> copy(entry)
                        1 -> startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, entry.text), null))
                        2 -> store.delete(entry)
                    }
                }
                .show()
            true
        }
    }

    private fun copy(entry: HistoryEntry) {
        getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Dictation", entry.text))
        // Android 13+ shows its own "copied" confirmation.
        if (Build.VERSION.SDK_INT < 33) Toast.makeText(this, "Copied", Toast.LENGTH_SHORT).show()
    }

    private fun text(value: String, size: Float, bold: Boolean = false, muted: Boolean = false) = TextView(this).apply {
        text = value
        textSize = size
        setTextColor(color(if (muted) R.color.tf_muted else R.color.tf_text))
        if (bold) typeface = Typeface.DEFAULT_BOLD
    }

    private fun <T : View> T.spaced(topDp: Int, bottomDp: Int): T = apply {
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            topMargin = dpi(topDp)
            bottomMargin = dpi(bottomDp)
        }
    }
}
