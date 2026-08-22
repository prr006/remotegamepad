package com.example.remotegamepad.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View

/**
 * Tactile circular button used for A/B/X/Y, D-pad arrows, L3/R3, and the
 * small View/Home/Menu buttons. Visual state changes happen synchronously
 * in onTouchEvent / onDraw only - no animators, no property transitions -
 * so press feedback is exactly as fast as the touch event itself.
 *
 * onDraw matches the JSX reference's flat CircleButton look - fully
 * transparent fill, a thin single-stroke outline, and a subtle dim-on-press
 * instead of a filled gradient face. accentColor still distinguishes a
 * pressed button (e.g. A/B/X/Y each keep their own accent) exactly as
 * before - only the resting-state gradient disc was removed.
 *
 * Touch handling hit-tests against the drawn circle (isInsideCircle), not
 * the full square View bounds - see that function's doc for why.
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

    private val rimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 2.5f // thin single stroke, matching the reference's 1.5px CSS border
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

        // No fill - the reference's buttons are fully transparent, just an
        // outline against the dark background.
        rimPaint.color = if (pressed) accentColor else Color.parseColor("#666666")
        // JSX dims the whole control on press (opacity 0.7) rather than
        // brightening it - mirrored here via alpha instead of a fill swap.
        rimPaint.alpha = if (pressed) 178 else 255
        canvas.drawCircle(cx, cy, r, rimPaint)

        if (label.isNotEmpty()) {
            textPaint.color = if (pressed) accentColor else Color.parseColor("#888888")
            textPaint.alpha = if (pressed) 178 else 255
            textPaint.textSize = labelSizeSp * resources.displayMetrics.scaledDensity
            val metrics = textPaint.fontMetrics
            val textY = cy - (metrics.ascent + metrics.descent) / 2f
            canvas.drawText(label, cx, textY, textPaint)
        }
    }

    /**
     * True if (x, y) falls inside the button's drawn circle (same center/
     * radius math as onDraw's `r`). The square View bounds are ~8% wider
     * than this circle on each side, and in tightly-packed layouts (the
     * D-pad cross, the ABXY diamond) neighboring buttons' square bounds
     * overlap by a few px in the diagonal gaps even though their drawn
     * circles never touch - so without this check, a touch in that gap
     * silently registers on whichever neighbor's square happens to claim
     * it first, producing a press the user never visually made.
     */
    private fun isInsideCircle(x: Float, y: Float): Boolean {
        val cx = width / 2f
        val cy = height / 2f
        val r = (minOf(width, height) / 2f) * 0.92f
        val dx = x - cx
        val dy = y - cy
        return dx * dx + dy * dy <= r * r
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                if (!isInsideCircle(event.x, event.y)) {
                    // Touch landed in this view's square bounds but outside
                    // its visible circle - most commonly the diagonal notch
                    // shared with a neighboring D-pad/ABXY button. Refuse
                    // the event so the ViewGroup offers it to the next
                    // overlapping sibling (or drops it, if the touch is in
                    // the genuine dead space between both circles) instead
                    // of this view claiming a press the user didn't make.
                    return false
                }
                pressed = true
                invalidate()
                onPress?.invoke(true)
            }
            // A finger that drags off the visible circle without lifting
            // (e.g. rolling from one D-pad arm toward another) releases
            // this button immediately, matching what the user sees rather
            // than leaving it logically stuck "pressed" until an eventual
            // ACTION_UP arrives somewhere else on screen.
            MotionEvent.ACTION_MOVE -> {
                if (pressed && !isInsideCircle(event.x, event.y)) {
                    pressed = false
                    invalidate()
                    onPress?.invoke(false)
                }
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
