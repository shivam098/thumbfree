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
    private val arc = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
    }
    private val stop = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFFFFFFF.toInt() }
    private val icon = context.getDrawable(R.drawable.ic_mic)!!.mutate()
    private val box = android.graphics.RectF()
    private var shown = 0f

    /** Listening: red, a stop square, and a ring that grows with your voice. */
    var recording = false
        set(value) {
            field = value
            if (!value) {
                level = 0f
                shown = 0f
            }
            describe()
            invalidate()
        }

    /** After you stop: a spinning arc until the text is written. */
    var transcribing = false
        set(value) {
            field = value
            describe()
            invalidate()
        }

    var level = 0f
        set(value) {
            field = value
            invalidate()
        }

    init {
        isClickable = true
        isFocusable = true
        describe()
    }

    private fun describe() {
        contentDescription = when {
            recording -> "Listening. Tap to finish."
            transcribing -> "Transcribing"
            else -> "Microphone. Tap to dictate."
        }
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
        if (transcribing) fill.alpha = 150
        canvas.drawCircle(cx, cy, radius, fill)
        if (recording) {
            val side = radius * 0.34f
            box.set(cx - side, cy - side, cx + side, cy + side)
            canvas.drawRoundRect(box, side * 0.25f, side * 0.25f, stop)
            return
        }
        val half = (radius * 0.5f).toInt()
        icon.alpha = if (transcribing) 170 else 255
        icon.setBounds(cx.toInt() - half, cy.toInt() - half, cx.toInt() + half, cy.toInt() + half)
        icon.draw(canvas)
        if (transcribing) {
            val r = radius * 1.14f
            arc.color = context.color(R.color.tf_accent)
            arc.strokeWidth = radius * 0.09f
            box.set(cx - r, cy - r, cx + r, cy + r)
            val start = (android.os.SystemClock.uptimeMillis() % 900L) / 900f * 360f
            canvas.drawArc(box, start, 110f, false, arc)
            postInvalidateOnAnimation()
        }
    }
}
