package app.thumbfree

import android.app.Application
import android.content.Context
import android.os.Handler
import android.os.Looper
import app.thumbfree.core.DictationOptions
import app.thumbfree.core.Language
import app.thumbfree.core.ModelCatalog
import app.thumbfree.core.ModelSpec
import app.thumbfree.core.PersonalDictionary
import app.thumbfree.core.Tier
import kotlin.properties.ReadWriteProperty
import kotlin.reflect.KProperty

class ThumbFreeApp : Application() {
    val prefs by lazy { Prefs(this) { spec -> models.isInstalled(spec) } }
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
class Prefs(context: Context, private val isInstalled: (ModelSpec) -> Boolean = { false }) {
    private val sp = context.getSharedPreferences("settings", Context.MODE_PRIVATE)

    var language: Language
        get() = if (sp.getString("language", "en") == "hinglish") Language.Hinglish else Language.English
        set(value) = sp.edit().putString("language", if (value == Language.Hinglish) "hinglish" else "en").apply()

    /**
     * English model: Live types as you speak, Standard and Accurate type when you finish. Before 0.6 this was an
     * "accurate" switch; people who already have the Standard model keep it, new people start on Live.
     */
    var englishTier: Tier
        get() = tier("english_tier") ?: when {
            sp.getBoolean("accurate_english", false) -> Tier.Accurate
            isInstalled(ModelCatalog.EnglishFast) -> Tier.Standard
            else -> Tier.Live
        }.also { englishTier = it } // decided once, so finishing the guide doesn't change it
        set(value) = sp.edit().putString("english_tier", value.name).apply()

    /** Hindi + English has no live model yet: Standard (live text by re-reading the newest audio) or Accurate. */
    var hinglishTier: Tier
        get() = (tier("hinglish_tier") ?: if (sp.getBoolean("accurate_hinglish", false)) Tier.Accurate else Tier.Standard)
            .let { if (it == Tier.Live) Tier.Standard else it }
        set(value) = sp.edit().putString("hinglish_tier", value.name).apply()

    /**
     * Words appear in the text box as you speak (underlined until you finish). Off, streaming models still show them on
     * the keyboard, and the others don't re-read the audio while you speak. Replaces 0.5's "Live preview" switch.
     */
    var liveTyping: Boolean
        get() = sp.getBoolean("live_typing", sp.getBoolean("live_preview", true))
        set(value) = sp.edit().putBoolean("live_typing", value).apply()
    var removeFillers by flag("remove_fillers", true)
    var spokenPunctuation by flag("spoken_punctuation", true)
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

    fun tier(language: Language) = if (language == Language.Hinglish) hinglishTier else englishTier

    fun setTier(language: Language, tier: Tier) {
        if (language == Language.Hinglish) hinglishTier = tier else englishTier = tier
    }

    /** The speech model for the language and its chosen tier. */
    fun model(language: Language = this.language) = ModelCatalog.forLanguage(language, tier(language))

    private fun tier(key: String) = sp.getString(key, null)?.let { name -> Tier.entries.firstOrNull { it.name == name } }

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
