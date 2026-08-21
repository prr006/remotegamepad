package com.example.remotegamepad.ui

import android.content.Context
import android.graphics.BlurMaskFilter
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
import kotlin.math.hypot
import kotlin.math.min

/**
 * Self-drawing D-pad. Reports each of UP/DOWN/LEFT/RIGHT independently via
 * onDirection(name, pressed) so diagonals (e.g. UP+RIGHT held together) work
 * naturally - the caller just forwards each edge straight to the existing
 * "<NAME>_DOWN" / "<NAME>_UP" protocol messages.
 *
 * The whole view is one large invisible hitbox (per the "generous hitbox"
 * requirement) - direction is resolved from touch angle relative to
 * center, not from tapping small individual arrow rectangles. Touch
 * handling is unchanged from the previous version; only onDraw / the
 * shape it draws changed - this is now a classic rounded cross ("+")
 * instead of four separate trapezoid arms fanned out inside a circle.
 */
class DPadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    var onDirection: ((name: String, pressed: Boolean) -> Unit)? = null

    // Deadzone near center where nothing is considered pressed.
    private var centerDeadzoneFrac = 0.18f

    private val activeDirs = mutableSetOf<String>()

    // Cross geometry, rebuilt in onSizeChanged so it scales with the view.
    private var crossPath = Path()
    private var crossBounds = RectF()
    private var centerX = 0f
    private var centerY = 0f
    private var armReach = 0f       // distance from center to tip of each arm
    private var armHalfWidth = 0f   // half-thickness of the cross bar

    init {
        // BlurMaskFilter (used for the soft outer glow) only renders with
        // a software layer.
        setLayerType(LAYER_TYPE_SOFTWARE, null)
    }

    private val facePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val glowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        color = Color.parseColor("#3DA5FF")
    }
    private val rimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        color = Color.parseColor("#5CB2FF")
        strokeWidth = 2.5f
    }
    private val seamPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        color = Color.parseColor("#3A4048")
        strokeWidth = 1.5f
        alpha = 150
    }
    private val activeOverlayPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#2E88D9")
        alpha = 130
    }
    private val arrowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.CENTER
        isFakeBoldText = true
        color = Color.parseColor("#C7CDD6")
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        centerX = w / 2f
        centerY = h / 2f
        val half = min(w, h) / 2f * 0.96f

        // Bar thickness vs. total reach - tuned so the plus reads as a
        // single connected cross with generously sized arms, matching a
        // classic touchscreen D-pad rather than four thin fingers.
        armReach = half
        armHalfWidth = half * 0.36f
        val cornerRadius = armHalfWidth * 0.55f

        val vertical = RectF(centerX - armHalfWidth, centerY - armReach, centerX + armHalfWidth, centerY + armReach)
        val horizontal = RectF(centerX - armReach, centerY - armHalfWidth, centerX + armReach, centerY + armHalfWidth)

        val vPath = Path().apply { addRoundRect(vertical, cornerRadius, cornerRadius, Path.Direction.CW) }
        val hPath = Path().apply { addRoundRect(horizontal, cornerRadius, cornerRadius, Path.Direction.CW) }

        crossPath = Path().apply {
            op(vPath, hPath, Path.Op.UNION)
        }
        crossBounds = RectF().apply { crossPath.computeBounds(this, true) }

        facePaint.shader = LinearGradient(
            0f, crossBounds.top, 0f, crossBounds.bottom,
            Color.parseColor("#22262E"), Color.parseColor("#0A0C10"),
            Shader.TileMode.CLAMP
        )
        glowPaint.strokeWidth = armHalfWidth * 0.18f
        glowPaint.maskFilter = BlurMaskFilter(armHalfWidth * 0.5f, BlurMaskFilter.Blur.NORMAL)
    }

    override fun onDraw(canvas: Canvas) {
        if (crossPath.isEmpty) return

        // Soft outer glow first, underneath everything else.
        glowPaint.alpha = 130
        canvas.drawPath(crossPath, glowPaint)

        // Matte dark cross body.
        canvas.drawPath(crossPath, facePaint)

        // Per-arm press highlight, clipped to that arm's own quadrant so
        // only the pressed direction lights up.
        for (dir in activeDirs) {
            canvas.save()
            canvas.clipPath(quadrantPath(dir))
            canvas.drawPath(crossPath, activeOverlayPaint)
            canvas.restore()
        }

        // Crisp rim + faint seams separating the four arms, for a
        // premium beveled-touchscreen look rather than a flat sticker.
        rimPaint.alpha = if (activeDirs.isEmpty()) 170 else 255
        canvas.drawPath(crossPath, rimPaint)
        drawSeams(canvas)

        drawArrow(canvas, "UP", centerX, centerY - armReach * 0.58f)
        drawArrow(canvas, "DOWN", centerX, centerY + armReach * 0.58f)
        drawArrow(canvas, "LEFT", centerX - armReach * 0.58f, centerY)
        drawArrow(canvas, "RIGHT", centerX + armReach * 0.58f, centerY)
    }

    /** Triangular wedge from center to the tip of one arm, used to clip the press-highlight. */
    private fun quadrantPath(dir: String): Path {
        val far = armReach * 1.4f
        return Path().apply {
            moveTo(centerX, centerY)
            when (dir) {
                "UP" -> { lineTo(centerX - far, centerY - far); lineTo(centerX + far, centerY - far) }
                "DOWN" -> { lineTo(centerX - far, centerY + far); lineTo(centerX + far, centerY + far) }
                "LEFT" -> { lineTo(centerX - far, centerY - far); lineTo(centerX - far, centerY + far) }
                "RIGHT" -> { lineTo(centerX + far, centerY - far); lineTo(centerX + far, centerY + far) }
            }
            close()
        }
    }

    private fun drawSeams(canvas: Canvas) {
        // Short diagonal seam lines from the center hub out toward each of
        // the four inner concave notches - reads as a subtle bevel between
        // adjacent arms without needing four separately-filled shapes.
        val n = armHalfWidth
        canvas.drawLine(centerX - n, centerY - n, centerX - n * 2.2f, centerY - n * 2.2f, seamPaint)
        canvas.drawLine(centerX + n, centerY - n, centerX + n * 2.2f, centerY - n * 2.2f, seamPaint)
        canvas.drawLine(centerX - n, centerY + n, centerX - n * 2.2f, centerY + n * 2.2f, seamPaint)
        canvas.drawLine(centerX + n, centerY + n, centerX + n * 2.2f, centerY + n * 2.2f, seamPaint)
    }

    private fun drawArrow(canvas: Canvas, dir: String, x: Float, y: Float) {
        val glyph = when (dir) {
            "UP" -> "\u25B2"
            "DOWN" -> "\u25BC"
            "LEFT" -> "\u25C0"
            "RIGHT" -> "\u25B6"
            else -> ""
        }
        arrowPaint.alpha = if (dir in activeDirs) 255 else 205
        arrowPaint.textSize = armHalfWidth * 0.85f
        val metrics = arrowPaint.fontMetrics
        val textY = y - (metrics.ascent + metrics.descent) / 2f
        canvas.drawText(glyph, x, textY, arrowPaint)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                updateFromTouch(event.x, event.y)
            }
            MotionEvent.ACTION_UP,
            MotionEvent.ACTION_CANCEL,
            MotionEvent.ACTION_POINTER_UP -> {
                // ACTION_POINTER_UP fires when one of several fingers lifts;
                // if any finger on the D-pad lifts, release all directions
                // rather than leaving a direction stuck held.
                releaseAll()
            }
        }
        return true
    }

    /**
     * Per-axis threshold rather than a single angle slice: if the touch is
     * clearly off-center on X and/or Y, both axes can fire at once, which
     * is what gives natural diagonal presses (UP+RIGHT etc.) instead of
     * forcing a single 45-degree-wide direction choice.
     */
    private fun updateFromTouch(x: Float, y: Float) {
        val cx = width / 2f
        val cy = height / 2f
        val dx = x - cx
        val dy = y - cy
        val dist = hypot(dx, dy)
        val outerR = min(width, height) / 2f
        val axisThreshold = outerR * centerDeadzoneFrac

        val next = mutableSetOf<String>()
        if (dist >= axisThreshold) {
            if (dx <= -axisThreshold) next.add("LEFT")
            if (dx >= axisThreshold) next.add("RIGHT")
            if (dy <= -axisThreshold) next.add("UP")
            if (dy >= axisThreshold) next.add("DOWN")
        }
        applyDirections(next)
    }

    private fun applyDirections(next: Set<String>) {
        for (dir in next) {
            if (activeDirs.add(dir)) onDirection?.invoke(dir, true)
        }
        val released = activeDirs.filter { it !in next }
        for (dir in released) {
            activeDirs.remove(dir)
            onDirection?.invoke(dir, false)
        }
        if (next.isNotEmpty() || released.isNotEmpty()) invalidate()
    }

    private fun releaseAll() {
        applyDirections(emptySet())
    }
}
