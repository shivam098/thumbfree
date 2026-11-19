package app.thumbfree

import app.thumbfree.core.DictationFormatter
import app.thumbfree.core.FieldInfo
import app.thumbfree.core.Insertion
import app.thumbfree.core.Layout

/** Turns a raw transcript into the text to type into a field: clean-up, dictionary, then email/list layout. */
object Compose {
    /** Email apps, where a greeting or a sign-off alone is enough to lay the text out as an email. */
    private val EmailApps = setOf(
        "com.google.android.gm", "com.microsoft.office.outlook", "com.samsung.android.email.provider",
        "com.yahoo.mobile.client.android.mail", "ch.protonmail.android", "me.proton.android.mail", "com.readdle.spark",
        "com.fsck.k9", "net.thunderbird.android", "com.android.email", "com.zoho.mail", "com.superhuman.mail",
    )

    /** A field can ask for email layout with privateImeOptions (the first-run practice box does). */
    const val EMAIL_OPTION = "app.thumbfree.email"

    fun isEmail(packageName: String?, privateImeOptions: String?) =
        packageName in EmailApps || privateImeOptions?.contains(EMAIL_OPTION) == true

    fun text(prefs: Prefs, raw: String, before: CharSequence?, field: FieldInfo, email: Boolean): String {
        var text = DictationFormatter(prefs.dictationOptions()).format(raw, Insertion.continuesSentence(before), field.search)
        if (text.isEmpty()) return ""
        if (prefs.smartFormatting && !field.search) text = Layout.shape(text, before, field.multiLine, email)
        return Insertion.forField(text, field)
    }

    /** For a dictation that lost its text box: formatted on its own, for copying. */
    fun standalone(prefs: Prefs, raw: String) = DictationFormatter(prefs.dictationOptions()).format(raw)

    /**
     * The words so far, typed live (underlined) while you speak: cleaned up like the final text but not laid out, as an
     * email or list only makes sense once you've finished.
     */
    fun live(prefs: Prefs, raw: String, before: CharSequence?, field: FieldInfo) =
        Insertion.forField(DictationFormatter(prefs.dictationOptions()).format(raw, Insertion.continuesSentence(before), field.search), field)
}
