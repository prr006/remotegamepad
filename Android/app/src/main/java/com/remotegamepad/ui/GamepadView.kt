package com.remotegamepad.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import com.remotegamepad.bluetooth.GamepadState

/**
 * Redesigned gamepad controller view.
 *
 * Layout (landscape primary):
 *   Top:        L1 (left)          SELECT START (center)          R1 (right)
 *   Upper-left: D-pad (↑←↓→)
 *   Lower-left: Left analog stick (+ L3 button at bottom-left corner)
 *   Upper-right: ABXY diamond (Y top, X left, B right, A bottom)
 *   Lower-right: Right analog stick (+ R3 button at bottom-right corner)
 *
 * All positions are computed as percentages of screen dimensions.
 * No control overlaps another.
 */
class GamepadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    interface Listener {
        fun onStateChanged(state: GamepadState)
    }

    var listener: Listener? = null

    // ── Paints ──
    private val paintClear = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#000000")
        style = Paint.Style.FILL
    }

    private val paintStickRing = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#2A2A35")
        style = Paint.Style.STROKE
        strokeWidth = 6f
    }

    private val paintStickBase = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#1E1E28")
        style = Paint.Style.FILL
    }

    private val paintStickCap = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#4A4A5A")
        style = Paint.Style.FILL
    }

    private val paintStickCapPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#6A6A7A")
        style = Paint.Style.FILL
    }

    private val paintDpad = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#2D2D3A")
        style = Paint.Style.FILL
    }

    private val paintDpadPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#4E4E60")
        style = Paint.Style.FILL
    }

    private val paintDpadStroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#555566")
        style = Paint.Style.STROKE
        strokeWidth = 3f
    }

    private val paintShoulder = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#333340")
        style = Paint.Style.FILL
    }

    private val paintShoulderPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#555570")
        style = Paint.Style.FILL
    }

    private val paintShoulderStroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#666677")
        style = Paint.Style.STROKE
        strokeWidth = 3f
    }

    // ABXY distinct colours
    private val paintBtnA = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#4CAF50") } // Green
    private val paintBtnB = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#F44336") } // Red
    private val paintBtnX = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#2196F3") } // Blue
    private val paintBtnY = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#FFEB3B") } // Yellow

    private val paintBtnAPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#81C784") }
    private val paintBtnBPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#EF5350") }
    private val paintBtnXPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#64B5F6") }
    private val paintBtnYPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#FFF176") }

    private val paintCenterBtn = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#3A3A48")
        style = Paint.Style.FILL
    }

    private val paintCenterBtnPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#5A5A68")
        style = Paint.Style.FILL
    }

    private val paintTextWhite = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        textSize = 26f
        textAlign = Paint.Align.CENTER
    }

    private val paintTextDark = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#222222")
        textSize = 26f
        textAlign = Paint.Align.CENTER
    }

    private val paintTextLabel = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#888899")
        textSize = 18f
        textAlign = Paint.Align.CENTER
    }

    // ── State ──
    private data class ButtonStyle(
        val pressed: Boolean,
        val paint: Paint,
        val pressedPaint: Paint,
        val textPaint: Paint
    )

    private var state = GamepadState()
    private val pointerMap = mutableMapOf<Int, String>()

    // ── Geometry ──
    private var w = 0f
    private var h = 0f
    private var minDim = 0f

    // Left stick
    private var lsCx = 0f
    private var lsCy = 0f
    private var lsR = 0f
    private var lsKnobR = 0f
    private var lsKnobX = 0f
    private var lsKnobY = 0f

    // Right stick
    private var rsCx = 0f
    private var rsCy = 0f
    private var rsR = 0f
    private var rsKnobR = 0f
    private var rsKnobX = 0f
    private var rsKnobY = 0f

    // D-pad
    private val dpadRects = mutableMapOf<String, RectF>()

    // ABXY
    private val abxyRects = mutableMapOf<String, RectF>()

    // Shoulders + center
    private val shoulderRects = mutableMapOf<String, RectF>()

    // L3 / R3
    private val l3Rect = RectF()
    private val r3Rect = RectF()

    override fun onSizeChanged(width: Int, height: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(width, height, oldw, oldh)
        w = width.toFloat()
        h = height.toFloat()
        minDim = kotlin.math.min(w, h)

        val margin = minDim * 0.03f

        // ── Sticks (lower half, left and right) ──
        lsR = minDim * 0.14f
        lsKnobR = lsR * 0.42f
        lsCx = margin + w * 0.18f
        lsCy = h * 0.72f
        lsKnobX = lsCx
        lsKnobY = lsCy

        rsR = minDim * 0.14f
        rsKnobR = rsR * 0.42f
        rsCx = w - margin - w * 0.18f
        rsCy = h * 0.72f
        rsKnobX = rsCx
        rsKnobY = rsCy

        // ── D-pad (upper-left) ──
        val dpadCx = lsCx
        val dpadCy = h * 0.28f
        val dArmLen = minDim * 0.09f
        val dArmW = minDim * 0.055f
        dpadRects.clear()
        dpadRects["dUp"]    = RectF(dpadCx - dArmW, dpadCy - dArmLen * 2f, dpadCx + dArmW, dpadCy)
        dpadRects["dDown"]  = RectF(dpadCx - dArmW, dpadCy, dpadCx + dArmW, dpadCy + dArmLen * 2f)
        dpadRects["dLeft"]  = RectF(dpadCx - dArmLen * 2f, dpadCy - dArmW, dpadCx, dpadCy + dArmW)
        dpadRects["dRight"] = RectF(dpadCx, dpadCy - dArmW, dpadCx + dArmLen * 2f, dpadCy + dArmW)

        // ── ABXY (upper-right, diamond) ──
        val abxyCx = rsCx
        val abxyCy = h * 0.28f
        val br = minDim * 0.065f
        val offset = br * 1.35f
        abxyRects.clear()
        abxyRects["y"] = makeRect(abxyCx, abxyCy - offset, br)
        abxyRects["x"] = makeRect(abxyCx - offset, abxyCy, br)
        abxyRects["b"] = makeRect(abxyCx + offset, abxyCy, br)
        abxyRects["a"] = makeRect(abxyCx, abxyCy + offset, br)

        // ── Shoulder buttons (top row) ──
        val shW = w * 0.16f
        val shH = minDim * 0.055f
        shoulderRects.clear()
        shoulderRects["lb"] = RectF(margin, margin, margin + shW, margin + shH)
        shoulderRects["rb"] = RectF(w - margin - shW, margin, w - margin, margin + shH)

        // ── Select / Start (center top, below shoulders) ──
        val cW = w * 0.11f
        val cH = minDim * 0.045f
        val cY = margin + shH + minDim * 0.02f
        shoulderRects["select"] = RectF(w / 2f - cW - minDim * 0.015f, cY, w / 2f - minDim * 0.015f, cY + cH)
        shoulderRects["start"]  = RectF(w / 2f + minDim * 0.015f, cY, w / 2f + cW + minDim * 0.015f, cY + cH)

        // ── L3 / R3 (bottom corners) ──
        val smallR = minDim * 0.045f
        l3Rect.set(margin, h - margin - smallR * 2f, margin + smallR * 2f, h - margin)
        r3Rect.set(w - margin - smallR * 2f, h - margin - smallR * 2f, w - margin, h - margin)
    }

    private fun makeRect(cx: Float, cy: Float, radius: Float): RectF {
        return RectF(cx - radius, cy - radius, cx + radius, cy + radius)
    }

    override fun onDraw(canvas: Canvas) {
        canvas.drawPaint(paintClear)

        // ── D-pad ──
        dpadRects.forEach { (name, rect) ->
            val pressed = when (name) {
                "dUp" -> state.dUp
                "dDown" -> state.dDown
                "dLeft" -> state.dLeft
                "dRight" -> state.dRight
                else -> false
            }
            canvas.drawRoundRect(rect, 10f, 10f, if (pressed) paintDpadPressed else paintDpad)
            canvas.drawRoundRect(rect, 10f, 10f, paintDpadStroke)
        }
        // D-pad center dot
        val dpadCx = (dpadRects["dUp"]!!.left + dpadRects["dUp"]!!.right) / 2f
        val dpadCy = (dpadRects["dLeft"]!!.top + dpadRects["dLeft"]!!.bottom) / 2f
        canvas.drawCircle(dpadCx, dpadCy, minDim * 0.025f, paintDpad)

        // ── Left stick ──
        canvas.drawCircle(lsCx, lsCy, lsR, paintStickBase)
        canvas.drawCircle(lsCx, lsCy, lsR, paintStickRing)
        canvas.drawCircle(lsKnobX, lsKnobY, lsKnobR, if (state.l3) paintStickCapPressed else paintStickCap)
        canvas.drawText("L3", lsCx, lsCy + paintTextLabel.textSize / 3, paintTextLabel)

        // ── Right stick ──
        canvas.drawCircle(rsCx, rsCy, rsR, paintStickBase)
        canvas.drawCircle(rsCx, rsCy, rsR, paintStickRing)
        canvas.drawCircle(rsKnobX, rsKnobY, rsKnobR, if (state.r3) paintStickCapPressed else paintStickCap)
        canvas.drawText("R3", rsCx, rsCy + paintTextLabel.textSize / 3, paintTextLabel)

        // ── ABXY ──
        abxyRects.forEach { (name, rect) ->
            val style = when (name) {
                "a" -> ButtonStyle(state.a, paintBtnA, paintBtnAPressed, paintTextDark)
                "b" -> ButtonStyle(state.b, paintBtnB, paintBtnBPressed, paintTextDark)
                "x" -> ButtonStyle(state.x, paintBtnX, paintBtnXPressed, paintTextDark)
                "y" -> ButtonStyle(state.y, paintBtnY, paintBtnYPressed, paintTextDark)
                else -> ButtonStyle(false, paintBtnA, paintBtnAPressed, paintTextWhite)
            }
            val cx = rect.centerX()
            val cy = rect.centerY()
            val r = (rect.right - rect.left) / 2f
            canvas.drawCircle(cx, cy, r, if (style.pressed) style.pressedPaint else style.paint)
            canvas.drawText(name.uppercase(), cx, cy + style.textPaint.textSize / 3, style.textPaint)
        }

        // ── Shoulders + Center ──
        shoulderRects.forEach { (name, rect) ->
            val pressed = when (name) {
                "lb" -> state.lb
                "rb" -> state.rb
                "start" -> state.start
                "select" -> state.select
                else -> false
            }
            canvas.drawRoundRect(rect, 12f, 12f, if (pressed) paintShoulderPressed else paintShoulder)
            canvas.drawRoundRect(rect, 12f, 12f, paintShoulderStroke)
            val label = when (name) {
                "lb" -> "L1"
                "rb" -> "R1"
                "start" -> "START"
                "select" -> "SELECT"
                else -> name.uppercase()
            }
            canvas.drawText(label, rect.centerX(), rect.centerY() + paintTextWhite.textSize / 3, paintTextWhite)
        }

        // ── L3 / R3 corner buttons ──
        canvas.drawRoundRect(l3Rect, 10f, 10f, if (state.l3) paintShoulderPressed else paintShoulder)
        canvas.drawRoundRect(l3Rect, 10f, 10f, paintShoulderStroke)
        canvas.drawText("L3", l3Rect.centerX(), l3Rect.centerY() + paintTextWhite.textSize / 3, paintTextWhite)

        canvas.drawRoundRect(r3Rect, 10f, 10f, if (state.r3) paintShoulderPressed else paintShoulder)
        canvas.drawRoundRect(r3Rect, 10f, 10f, paintShoulderStroke)
        canvas.drawText("R3", r3Rect.centerX(), r3Rect.centerY() + paintTextWhite.textSize / 3, paintTextWhite)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                val idx = event.actionIndex
                val pid = event.getPointerId(idx)
                val x = event.getX(idx)
                val y = event.getY(idx)
                findControlAt(x, y)?.let { control ->
                    pointerMap[pid] = control
                    updateState(control, x, y, true)
                    invalidate()
                    return true
                }
            }
            MotionEvent.ACTION_MOVE -> {
                for (i in 0 until event.pointerCount) {
                    val pid = event.getPointerId(i)
                    val control = pointerMap[pid] ?: continue
                    updateState(control, event.getX(i), event.getY(i), true)
                }
                invalidate()
                return true
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                val idx = event.actionIndex
                val pid = event.getPointerId(idx)
                pointerMap.remove(pid)?.let { control ->
                    updateState(control, 0f, 0f, false)
                    invalidate()
                    return true
                }
            }
            MotionEvent.ACTION_CANCEL -> {
                pointerMap.clear()
                resetState()
                invalidate()
                return true
            }
        }
        return super.onTouchEvent(event)
    }

    private fun findControlAt(x: Float, y: Float): String? {
        // Check small buttons first (more specific)
        if (l3Rect.contains(x, y)) return "l3"
        if (r3Rect.contains(x, y)) return "r3"

        shoulderRects.forEach { (name, rect) ->
            if (rect.contains(x, y)) return name
        }
        abxyRects.forEach { (name, rect) ->
            if (rect.contains(x, y)) return name
        }
        dpadRects.forEach { (name, rect) ->
            if (rect.contains(x, y)) return name
        }

        // Sticks last (larger catchment)
        if (dist(x, y, lsCx, lsCy) < lsR * 1.25f) return "leftStick"
        if (dist(x, y, rsCx, rsCy) < rsR * 1.25f) return "rightStick"

        return null
    }

    private fun updateState(control: String, x: Float, y: Float, pressed: Boolean) {
        when (control) {
            "leftStick" -> {
                if (!pressed) {
                    lsKnobX = lsCx
                    lsKnobY = lsCy
                    state = state.copy(lx = 0f, ly = 0f)
                } else {
                    val dx = (x - lsCx) / lsR
                    val dy = (y - lsCy) / lsR
                    val len = kotlin.math.hypot(dx, dy)
                    val (cx, cy) = if (len > 1f) {
                        val s = 1f / len
                        Pair(dx * s, dy * s)
                    } else Pair(dx, dy)
                    lsKnobX = lsCx + cx * lsR
                    lsKnobY = lsCy + cy * lsR
                    state = state.copy(lx = cx, ly = -cy)
                }
            }
            "rightStick" -> {
                if (!pressed) {
                    rsKnobX = rsCx
                    rsKnobY = rsCy
                    state = state.copy(rx = 0f, ry = 0f)
                } else {
                    val dx = (x - rsCx) / rsR
                    val dy = (y - rsCy) / rsR
                    val len = kotlin.math.hypot(dx, dy)
                    val (cx, cy) = if (len > 1f) {
                        val s = 1f / len
                        Pair(dx * s, dy * s)
                    } else Pair(dx, dy)
                    rsKnobX = rsCx + cx * rsR
                    rsKnobY = rsCy + cy * rsR
                    state = state.copy(rx = cx, ry = -cy)
                }
            }
            "a" -> state = state.copy(a = pressed)
            "b" -> state = state.copy(b = pressed)
            "x" -> state = state.copy(x = pressed)
            "y" -> state = state.copy(y = pressed)
            "lb" -> state = state.copy(lb = pressed)
            "rb" -> state = state.copy(rb = pressed)
            "dUp" -> state = state.copy(dUp = pressed)
            "dDown" -> state = state.copy(dDown = pressed)
            "dLeft" -> state = state.copy(dLeft = pressed)
            "dRight" -> state = state.copy(dRight = pressed)
            "start" -> state = state.copy(start = pressed)
            "select" -> state = state.copy(select = pressed)
            "l3" -> state = state.copy(l3 = pressed)
            "r3" -> state = state.copy(r3 = pressed)
        }
        listener?.onStateChanged(state)
    }

    private fun resetState() {
        state = GamepadState()
        lsKnobX = lsCx
        lsKnobY = lsCy
        rsKnobX = rsCx
        rsKnobY = rsCy
        listener?.onStateChanged(state)
    }

    private fun dist(x1: Float, y1: Float, x2: Float, y2: Float): Float {
        return kotlin.math.hypot(x1 - x2, y1 - y2)
    }
}