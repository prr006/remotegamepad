package com.example.remotegamepad.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RadialGradient
import android.graphics.Shader
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View

/**
 * Tactile circular button used for A/B/X/Y and the small View/Home/Menu
 * buttons. Visual state changes happen synchronously in onTouchEvent /
 * onDraw only - no animators, no property transitions - so press feedback
 * is exactly as fast as the touch event itself.
 */
class RoundButtonView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    var label: String = ""
        set(value) { field = value; invalidate() }
    var accentColor: Int = Color.parseColor("#8B93A1")
        set(value) { field = value; invalidate() }
    var labelSizeSp: Float = 20f
    var onPress: ((pressed: Boolean) -> Unit)? = null

    private var pressed = false

    private val facePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val rimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 2.5f
    }
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.CENTER
        isFakeBoldText = true
    }

    override fun onDraw(canvas: Canvas) {
        val cx = width / 2f
        val cy = height / 2f
        val r = (minOf(width, height) / 2f) * 0.92f

        val top = if (pressed) Color.parseColor("#171A1F") else Color.parseColor("#262B33")
        val bottom = if (pressed) Color.parseColor("#0A0C10") else Color.parseColor("#14161A")
        facePaint.shader = RadialGradient(
            cx - r * 0.3f, cy - r * 0.3f, r * 1.6f,
            intArrayOf(top, bottom),
            floatArrayOf(0f, 1f),
            Shader.TileMode.CLAMP
        )
        canvas.drawCircle(cx, cy, r, facePaint)

        rimPaint.color = if (pressed) accentColor else Color.parseColor("#3A4048")
        rimPaint.alpha = if (pressed) 255 else 160
        canvas.drawCircle(cx, cy, r - 1.5f, rimPaint)

        if (label.isNotEmpty()) {
            textPaint.color = accentColor
            textPaint.alpha = if (pressed) 255 else 210
            textPaint.textSize = labelSizeSp * resources.displayMetrics.scaledDensity
            val metrics = textPaint.fontMetrics
            val textY = cy - (metrics.ascent + metrics.descent) / 2f
            canvas.drawText(label, cx, textY, textPaint)
        }
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                pressed = true
                invalidate()
                onPress?.invoke(true)
            }
            // Release only on the clean final lift (ACTION_UP) or a system
            // cancellation (ACTION_CANCEL). ACTION_POINTER_UP is intentionally
            // excluded: it fires when a different finger lifts while this
            // button's own pointer is still down, and acting on it would
            // incorrectly release a button the user is still holding.
            // Guard on `pressed` so a stray event that arrives when the button
            // was never touched doesn't send a phantom onPress(false).
            MotionEvent.ACTION_UP,
            MotionEvent.ACTION_CANCEL -> {
                if (pressed) {
                    pressed = false
                    invalidate()
                    onPress?.invoke(false)
                }
            }
        }
        return true
    }
}
