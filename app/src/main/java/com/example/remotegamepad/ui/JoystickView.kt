package com.example.remotegamepad.ui

import android.content.Context
import android.graphics.BlurMaskFilter
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RadialGradient
import android.graphics.Shader
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import kotlin.math.hypot
import kotlin.math.min

/**
 * Self-drawing virtual thumbstick. Purely visual/input - it does NOT touch
 * the network. It reports normalized [-1,1] movement via onMove and a
 * press/release via onClick, mirroring a real stick's L3/R3: on a physical
 * controller the click is the stick module itself being depressed, so it
 * can be held DOWN while you're still steering. We do the same here -
 * onClick(true) fires on ACTION_DOWN and onClick(false) on release,
 * independent of how far the stick is tilted while held.
 *
 * Touch handling / the onMove & onClick contract is unchanged from the
 * previous version - only onDraw grew richer (soft glow, an extra
 * concentric ring, a tactile dot texture on the knob, and a distinct
 * pressed state) to read as a larger, more premium control.
 */
class JoystickView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    var deadzone = 0.12f
    var onMove: ((x: Float, y: Float) -> Unit)? = null
    var onClick: ((pressed: Boolean) -> Unit)? = null

    private var knobX = 0f
    private var knobY = 0f
    private var maxRadius = 0f
    private var pressed = false
    private var activePointerId = MotionEvent.INVALID_POINTER_ID

    // Cached per-size geometry for the knob's tactile dot texture, so the
    // grid isn't recomputed every frame.
    private var textureDots = FloatArray(0)
    private var knobRadiusCache = 0f

    init {
        // BlurMaskFilter (soft outer glow) needs a software layer.
        setLayerType(LAYER_TYPE_SOFTWARE, null)
    }

    private val basePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val glowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        color = Color.parseColor("#3DA5FF")
    }
    private val ringPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        color = Color.parseColor("#2E88D9")
        strokeWidth = 3f
    }
    private val innerRingPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        color = Color.parseColor("#2E88D9")
        strokeWidth = 1.5f
        alpha = 70
    }
    private val knobPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val knobRimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        color = Color.parseColor("#5CB2FF")
        strokeWidth = 2.5f
    }
    private val texturePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#4A5058")
        style = Paint.Style.FILL
        strokeWidth = 3f
        strokeCap = Paint.Cap.ROUND
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        val cx = w / 2f
        val cy = h / 2f
        val outerR = min(w, h) / 2f
        maxRadius = outerR * 0.55f

        basePaint.shader = RadialGradient(
            cx, cy, outerR,
            intArrayOf(Color.parseColor("#20242B"), Color.parseColor("#0C0E12")),
            floatArrayOf(0f, 1f),
            Shader.TileMode.CLAMP
        )
        glowPaint.strokeWidth = outerR * 0.07f
        glowPaint.maskFilter = BlurMaskFilter(outerR * 0.22f, BlurMaskFilter.Blur.NORMAL)

        knobX = cx
        knobY = cy

        val knobR = outerR * 0.95f * 0.42f
        buildTextureDots(knobR)
    }

    /** Small hex-ish dot grid inside the knob radius, for a tactile grip texture. */
    private fun buildTextureDots(knobR: Float) {
        knobRadiusCache = knobR
        val spacing = knobR * 0.34f
        val usableR = knobR * 0.72f
        val pts = mutableListOf<Float>()
        var row = 0
        var yy = -usableR
        while (yy <= usableR) {
            val rowOffset = if (row % 2 == 0) 0f else spacing / 2f
            var xx = -usableR + rowOffset
            while (xx <= usableR) {
                if (hypot(xx.toDouble(), yy.toDouble()) <= usableR) {
                    pts.add(xx)
                    pts.add(yy)
                }
                xx += spacing
            }
            yy += spacing * 0.87f
            row++
        }
        textureDots = pts.toFloatArray()
    }

    override fun onDraw(canvas: Canvas) {
        val cx = width / 2f
        val cy = height / 2f
        val outerR = min(width, height) / 2f * 0.95f

        // Soft outer glow, stronger while actively held.
        glowPaint.alpha = if (pressed) 200 else 110
        canvas.drawCircle(cx, cy, outerR, glowPaint)

        canvas.drawCircle(cx, cy, outerR, basePaint)
        canvas.drawCircle(cx, cy, outerR * 0.74f, innerRingPaint)

        ringPaint.alpha = if (pressed) 255 else 150
        canvas.drawCircle(cx, cy, outerR - 4f, ringPaint)

        val knobR = outerR * 0.42f
        knobPaint.shader = RadialGradient(
            knobX - knobR * 0.3f, knobY - knobR * 0.3f, knobR * 1.6f,
            intArrayOf(Color.parseColor("#3A4048"), Color.parseColor("#15171B")),
            floatArrayOf(0f, 1f),
            Shader.TileMode.CLAMP
        )
        canvas.drawCircle(knobX, knobY, knobR, knobPaint)

        // Tactile dot texture, clipped to the knob.
        if (textureDots.isNotEmpty()) {
            canvas.save()
            canvas.clipPath(android.graphics.Path().apply { addCircle(knobX, knobY, knobR * 0.95f, android.graphics.Path.Direction.CW) })
            canvas.translate(knobX, knobY)
            canvas.drawPoints(textureDots, texturePaint)
            canvas.restore()
        }

        knobRimPaint.alpha = if (pressed) 255 else 210
        canvas.drawCircle(knobX, knobY, knobR - 1.5f, knobRimPaint)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        val cx = width / 2f
        val cy = height / 2f

        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                activePointerId = event.getPointerId(0)
                pressed = true
                onClick?.invoke(true)
                invalidate()
                return true
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                // Keep driving the stick from the original finger. A second
                // finger touching the view must never steal its coordinates.
                return true
            }

            MotionEvent.ACTION_MOVE -> {
                val index = event.findPointerIndex(activePointerId)
                if (index < 0) {
                    releaseToCenter()
                    return true
                }

                val dx = event.getX(index) - cx
                val dy = event.getY(index) - cy
                val dist = hypot(dx, dy)

                val kx: Float
                val ky: Float
                if (dist > maxRadius && dist > 0f) {
                    kx = dx / dist * maxRadius
                    ky = dy / dist * maxRadius
                } else {
                    kx = dx
                    ky = dy
                }

                var normX = (kx / maxRadius).coerceIn(-1f, 1f)
                var normY = (ky / maxRadius).coerceIn(-1f, 1f)
                if (hypot(normX, normY) < deadzone) {
                    normX = 0f
                    normY = 0f
                }

                knobX = cx + kx
                knobY = cy + ky
                invalidate()
                onMove?.invoke(normX, normY)
                return true
            }

            MotionEvent.ACTION_POINTER_UP -> {
                // Intentionally do nothing.
                //
                // ACTION_POINTER_UP fires when ANY secondary pointer lifts,
                // not necessarily the one that owns this joystick. On many
                // devices the system generates synthetic POINTER_UP events
                // during rotation gestures where actionIndex coincidentally
                // equals the joystick's activePointerId, causing a spurious
                // releaseToCenter() that would:
                //   (a) zero leftSnap/rightSnap mid-drag, and
                //   (b) fire onClick(false) → phantom L3/R3 release.
                //
                // True pointer loss during a move is already caught by the
                // findPointerIndex() guard in ACTION_MOVE above, which calls
                // releaseToCenter() if the pointer genuinely disappears.
                // A genuine final lift is handled by ACTION_UP / ACTION_CANCEL.
                return true
            }

            MotionEvent.ACTION_UP,
            MotionEvent.ACTION_CANCEL,
            MotionEvent.ACTION_OUTSIDE -> {
                releaseToCenter()
                return true
            }
        }

        return true
    }

    private fun releaseToCenter() {
        val cx = width / 2f
        val cy = height / 2f
        activePointerId = MotionEvent.INVALID_POINTER_ID
        pressed = false
        knobX = cx
        knobY = cy
        invalidate()

        // Always emit an explicit neutral sample. The transport heartbeat
        // also carries the absolute joystick state, so this is the fast
        // path while the heartbeat is the recovery path if this packet is
        // lost in transit.
        onMove?.invoke(0f, 0f)
        onClick?.invoke(false)
    }

    override fun onDetachedFromWindow() {
        if (pressed || activePointerId != MotionEvent.INVALID_POINTER_ID) {
            releaseToCenter()
        }
        super.onDetachedFromWindow()
    }
}
