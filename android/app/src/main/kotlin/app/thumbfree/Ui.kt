package app.thumbfree

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.drawable.GradientDrawable
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.TextView
import kotlin.math.min

internal fun Context.dp(value: Float) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value, resources.displayMetrics)

internal fun Context.dpi(value: Int) = dp(value.toFloat()).toInt()

internal fun Context.color(id: Int) = getColor(id)

internal fun Context.rounded(fill: Int, radiusDp: Float = 12f) = GradientDrawable().apply {
    setColor(fill)
    cornerRadius = dp(radiusDp)
}

/** A flat, rounded key. */
internal fun Context.key(label: String, description: String, onClick: (View) -> Unit) = TextView(this).apply {
    text = label
    contentDescription = description
    gravity = Gravity.CENTER
    textSize = 18f
    setTextColor(color(R.color.tf_text))
    background = rounded(color(R.color.tf_key), 10f)
    isClickable = true
    isFocusable = true
    setOnClickListener(onClick)
}

/**
 * The big round mic key. It turns red while recording, and a soft ring around it grows with your voice so you can see
 * it's hearing you.
 */
class MicButton(context: Context) : View(context) {
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val ring = Paint(Paint.ANTI_ALIAS_FLAG)
    private val icon = context.getDrawable(R.drawable.ic_mic)!!.mutate()
    private var shown = 0f

    var recording = false
        set(value) {
            field = value
            if (!value) {
                level = 0f
                shown = 0f
            }
            invalidate()
        }

    var level = 0f
        set(value) {
            field = value
            invalidate()
        }

    init {
        contentDescription = "Microphone. Hold to dictate; tap for hands-free."
        isClickable = true
        isFocusable = true
    }

    override fun onDraw(canvas: Canvas) {
        val cx = width / 2f
        val cy = height / 2f
        val radius = min(width, height) / 2f * 0.72f
        val color = context.color(if (recording) R.color.tf_recording else R.color.tf_accent)
        if (recording) {
            // Ease towards the current loudness so the ring breathes instead of flickering.
            shown += (min(level * 8f, 1f) - shown) * 0.35f
            ring.color = color
            ring.alpha = 60
            canvas.drawCircle(cx, cy, radius * (1.06f + shown * 0.32f), ring)
            if (kotlin.math.abs(shown - min(level * 8f, 1f)) > 0.01f) postInvalidateOnAnimation()
        }
        fill.color = color
        canvas.drawCircle(cx, cy, radius, fill)
        val half = (radius * 0.5f).toInt()
        icon.setBounds(cx.toInt() - half, cy.toInt() - half, cx.toInt() + half, cy.toInt() + half)
        icon.draw(canvas)
    }
}
