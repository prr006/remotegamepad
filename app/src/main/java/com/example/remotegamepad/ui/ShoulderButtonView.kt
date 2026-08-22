package com.example.remotegamepad.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View

/**
 * LT/LB/RT/RB.
 *
 * Touch handling is UNCHANGED - the entire view bounds remain the hitbox
 * (generous touch area, not a precise tap target).
 *
 * The drawn shape changed: the previous version drew an angled trapezoid
 * ("shoulder button" look). The supplied JSX reference draws LT/LB/RB/RT
 * as plain circles, the same size and style as its D-pad/ABXY buttons -
 * so this now draws a flat circle with a thin outline to match, reusing
 * the same visual language as RoundButtonView. `mirrored` is kept as a
 * public property (GamepadActivity still sets it) but is now unused by
 * onDraw, since a circle has no left/right orientation to mirror.
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

    private val rimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 2.5f
        color = Color.parseColor("#666666")
    }
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.CENTER
        isFakeBoldText = true
        color = Color.parseColor("#888888")
    }

    override fun onDraw(canvas: Canvas) {
        val cx = width / 2f
        val cy = height / 2f
        val r = (minOf(width, height) / 2f) * 0.92f

        rimPaint.alpha = if (pressed) 178 else 255
        canvas.drawCircle(cx, cy, r, rimPaint)

        textPaint.alpha = if (pressed) 178 else 255
        textPaint.textSize = r * 0.55f
        val metrics = textPaint.fontMetrics
        val textY = cy - (metrics.ascent + metrics.descent) / 2f
        canvas.drawText(label, cx, textY, textPaint)
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
