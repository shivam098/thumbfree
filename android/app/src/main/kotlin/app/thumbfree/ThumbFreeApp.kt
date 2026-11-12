package app.thumbfree

import android.app.Application
import android.content.Context
import app.thumbfree.core.DictationOptions
import app.thumbfree.core.Language
import app.thumbfree.core.ModelCatalog
import app.thumbfree.core.PersonalDictionary
import kotlin.properties.ReadWriteProperty
import kotlin.reflect.KProperty

class ThumbFreeApp : Application() {
    val prefs by lazy { Prefs(this) }
    val models by lazy { ModelStore(this) }
    val engine by lazy { SpeechEngine() }
}

val Context.thumbFree: ThumbFreeApp get() = applicationContext as ThumbFreeApp

/** Settings, stored on this phone only (excluded from backups). */
class Prefs(context: Context) {
    private val sp = context.getSharedPreferences("settings", Context.MODE_PRIVATE)

    var language: Language
        get() = if (sp.getString("language", "en") == "hinglish") Language.Hinglish else Language.English
        set(value) = sp.edit().putString("language", if (value == Language.Hinglish) "hinglish" else "en").apply()

    var accurateEnglish by flag("accurate_english", false)
    var accurateHinglish by flag("accurate_hinglish", false)
    var removeFillers by flag("remove_fillers", true)
    var spokenPunctuation by flag("spoken_punctuation", true)
    var livePreview by flag("live_preview", true)
    var switchBack by flag("switch_back", false)
    var haptics by flag("haptics", true)

    var dictionary: String
        get() = sp.getString("dictionary", "") ?: ""
        set(value) = sp.edit().putString("dictionary", value).apply()

    /** "Words I use often", one per line. */
    var vocabulary: String
        get() = sp.getString("vocabulary", "") ?: ""
        set(value) = sp.edit().putString("vocabulary", value).apply()

    fun accurate(language: Language) = if (language == Language.Hinglish) accurateHinglish else accurateEnglish

    /** The speech model for the current language and accuracy choice. */
    fun model(language: Language = this.language) = ModelCatalog.forLanguage(language, accurate(language))

    fun dictationOptions() = DictationOptions(
        removeFillerWords = removeFillers,
        spokenPunctuation = spokenPunctuation,
        replacements = PersonalDictionary.parse(dictionary),
    )

    private fun flag(key: String, default: Boolean) = object : ReadWriteProperty<Any?, Boolean> {
        override fun getValue(thisRef: Any?, property: KProperty<*>) = sp.getBoolean(key, default)
        override fun setValue(thisRef: Any?, property: KProperty<*>, value: Boolean) = sp.edit().putBoolean(key, value).apply()
    }
}
