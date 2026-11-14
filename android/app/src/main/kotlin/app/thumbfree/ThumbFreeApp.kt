package app.thumbfree

import android.app.Application
import android.content.Context
import android.os.Handler
import android.os.Looper
import app.thumbfree.core.DictationOptions
import app.thumbfree.core.Language
import app.thumbfree.core.ModelCatalog
import app.thumbfree.core.PersonalDictionary
import kotlin.properties.ReadWriteProperty
import kotlin.reflect.KProperty

class ThumbFreeApp : Application() {
    val prefs by lazy { Prefs(this) }
    val models by lazy { ModelStore(this) }
    val engine by lazy { SpeechEngine { spec -> models.folder(spec) } }
    val notifier by lazy { Notifier(this) }
    val samples by lazy { SampleStore(this) }

    private val main = Handler(Looper.getMainLooper())
    private val unload = Runnable { if (!Dictation.anyBusy) engine.unload() }

    /** A dictation is starting: keep the model loaded. */
    fun holdModel() = main.removeCallbacks(unload)

    /** Frees the model's memory after a few idle minutes. */
    fun releaseModelLater() {
        main.removeCallbacks(unload)
        main.postDelayed(unload, IDLE_UNLOAD_MS)
    }

    companion object {
        private const val IDLE_UNLOAD_MS = 3 * 60_000L
    }
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

    /** Emails, lists and "…actually…" corrections. */
    var smartFormatting by flag("smart_formatting", true)

    /** The first-run guide was finished or skipped. */
    var onboarded by flag("onboarded", false)

    /** Show the floating mic when the bubble service is on (a quick way to pause it). */
    var bubble by flag("bubble", true)

    /** Keep recordings on this phone so you can choose to share them ("Help improve ThumbFree"). */
    var shareSamples by flag("share_samples", false)

    var profession: String?
        get() = sp.getString("profession", null)
        set(value) = sp.edit().putString("profession", value).apply()

    /** The bubble's place: vertical position (px from the top) and screen side. */
    var bubbleY: Int
        get() = sp.getInt("bubble_y", -1)
        set(value) = sp.edit().putInt("bubble_y", value).apply()
    var bubbleLeft by flag("bubble_left", false)

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
