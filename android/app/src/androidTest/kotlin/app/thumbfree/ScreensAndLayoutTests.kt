package app.thumbfree

import android.content.Context
import android.content.Intent
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import app.thumbfree.core.DictationFormatter
import app.thumbfree.core.Layout
import app.thumbfree.core.LayoutContext
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Emails, lists and corrections with Android's regex engine (ICU): tests/vectors/layout.tsv. */
@RunWith(AndroidJUnit4::class)
class LayoutOnDeviceTest {
    @Test
    fun matches_the_layout_vectors() {
        val assets = InstrumentationRegistry.getInstrumentation().context.assets
        val rows = assets.open("layout.tsv").bufferedReader(Charsets.UTF_8).readLines().filter { it.isNotEmpty() && !it.startsWith("#") }
        assertTrue("expected the layout vectors, found ${rows.size}", rows.size > 20)
        val failures = rows.mapNotNull { row ->
            val (flags, input, expected) = row.split('\t')
            val formatted = DictationFormatter().format(unescape(input))
            val actual = Layout.apply(formatted, LayoutContext('m' in flags, 's' in flags, 'e' in flags))
            if (actual == unescape(expected)) null else "[$flags] $input\n  expected: $expected\n  actual:   ${actual.replace("\n", "\\n")}"
        }
        assertEquals(failures.joinToString("\n"), 0, failures.size)
    }

    private fun unescape(s: String) = s.replace("\\n", "\n").replace("\\t", "\t")
}

/** Every screen of the first-run guide and Settings opens without crashing (views are built in code). */
@RunWith(AndroidJUnit4::class)
class ScreensOnDeviceTest {
    private val context: Context get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Test
    fun every_guide_step_opens() {
        val steps = listOf("Welcome", "Microphone", "Model", "Keyboard", "TryEmail", "TryList", "Bubble", "Notifications", "Profession", "Data")
        for (step in steps) {
            val intent = Intent(context, OnboardingActivity::class.java).putExtra(OnboardingActivity.EXTRA_STEP, step)
            ActivityScenario.launch<OnboardingActivity>(intent).use { scenario ->
                scenario.onActivity { activity -> assertTrue(step, !activity.isFinishing) }
            }
        }
    }

    @Test
    fun settings_open_after_the_guide() {
        context.thumbFree.prefs.onboarded = true
        ActivityScenario.launch(SetupActivity::class.java).use { scenario ->
            scenario.onActivity { activity -> assertTrue(!activity.isFinishing) }
        }
    }

    @Test
    fun first_launch_shows_the_guide() {
        context.thumbFree.prefs.onboarded = false
        ActivityScenario.launch(SetupActivity::class.java).use { scenario ->
            // SetupActivity hands over to the guide and closes itself.
            assertEquals("DESTROYED", scenario.state.name)
        }
        context.thumbFree.prefs.onboarded = true
    }
}
