package com.example.remotegamepad.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View

/**
 * LT/LB/RT/RB. The drawn shape is a rounded trapezoid roughly filling the
 * view, but the ENTIRE view bounds are the hitbox (per "generous touch
 * areas, not precise taps") - touch is handled for the whole rectangle,
 * not just the visible trapezoid pixels.
 */
class ShoulderButtonView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    var label: String = ""
        set(value) { field = value; invalidate() }
    var mirrored: Boolean = false
    var onPress: ((pressed: Boolean) -> Unit)? = null

    private var pressed = false

    private val facePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val rimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 2.5f
        color = Color.parseColor("#2E88D9")
    }
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.CENTER
        isFakeBoldText = true
        color = Color.parseColor("#E6E8EB")
    }

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        val inset = w * 0.04f
        val skew = w * 0.14f

        val path = Path()
        val rect = RectF(inset, inset, w - inset, h - inset)
        // Slight parallelogram lean so left/right feel mirrored, like a
        // real shoulder button's angled face.
        if (!mirrored) {
            path.moveTo(rect.left + skew, rect.top)
            path.lineTo(rect.right, rect.top)
            path.lineTo(rect.right - skew * 0.4f, rect.bottom)
            path.lineTo(rect.left, rect.bottom)
        } else {
            path.moveTo(rect.left, rect.top)
            path.lineTo(rect.right - skew, rect.top)
            path.lineTo(rect.right, rect.bottom)
            path.lineTo(rect.left + skew * 0.4f, rect.bottom)
        }
        path.close()

        val top = if (pressed) Color.parseColor("#171A1F") else Color.parseColor("#262B33")
        val bottom = if (pressed) Color.parseColor("#0A0C10") else Color.parseColor("#14161A")
        facePaint.shader = LinearGradient(0f, rect.top, 0f, rect.bottom, top, bottom, Shader.TileMode.CLAMP)

        canvas.drawPath(path, facePaint)
        rimPaint.alpha = if (pressed) 255 else 120
        canvas.drawPath(path, rimPaint)

        textPaint.alpha = if (pressed) 255 else 200
        textPaint.textSize = h * 0.30f
        val metrics = textPaint.fontMetrics
        val cx = w / 2f
        val cy = h / 2f - (metrics.ascent + metrics.descent) / 2f
        canvas.drawText(label, cx, cy, textPaint)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {
                pressed = true
                invalidate()
                onPress?.invoke(true)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                pressed = false
                invalidate()
                onPress?.invoke(false)
            }
        }
        return true
    }
}
