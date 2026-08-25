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
 * Gamepad controller view — monochrome GeForce/SHIELD "Far Cry 5" style overlay.
 *
 * Layout (landscape only):
 *   Top-left:    LT  LB        (circular pair, outer→inner)
 *   Top-right:   RB  RT        (circular pair, inner→outer)
 *   Top-center:  VIEW  HOME  MENU   (pill, circle, pill)
 *   Left:        D-pad — 4 separate circular buttons (▲ ◀ ▶ ▼), above left stick
 *   Right:       ABXY — 4 separate monochrome circular buttons (diamond), above right stick
 *   Lower-left:  Large analog stick with concentric rings; L3 is a separate circle near the edge
 *   Lower-right: Mirrored right stick; R3 is a separate circle near the edge
 *
 * GEOMETRY MODEL — normalized design space, not per-device math:
 *
 * All control positions/sizes are authored once in a fixed reference
 * design canvas (DESIGN_W x DESIGN_H, ~16:9, matching the reference
 * screenshot this layout is based on). onSizeChanged() then computes a
 * single uniform scale factor — min(actualWidth / DESIGN_W,
 * actualHeight / DESIGN_H) — and applies that same factor to every
 * position, radius, gap, stroke width and text size. The scaled design
 * is centered in the real viewport, so a wider-than-16:9 phone gets
 * extra side margin and a narrower one gets uniformly scaled down —
 * either way circles stay circles and the composition never distorts
 * or reflows per aspect ratio. This view is intentionally landscape-
 * only; there is no portrait variant of this geometry.
 *
 * Protocol / state / listener contract is untouched: this view only
 * changes what is drawn and where touches are hit-tested. The HOME
 * button is drawn and responds to touch for visual feedback only —
 * GamepadState has no field for it, so it never emits a state change.
 * (Wiring HOME into the real protocol would require adding a `home`
 * field to GamepadState/Protocol, which is out of scope here.)
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

    /** Visual controller style — only affects labels, not geometry or protocol. */
    enum class ControllerStyle { XBOX, PS }

    /** Current controller label style. Setting this invalidates the view. */
    var controllerStyle = ControllerStyle.XBOX
        set(value) { field = value; invalidate() }

    companion object {
        // Reference design canvas — a fixed authoring space, NOT a
        // device resolution. Matches the reference image's ~16:9
        // composition. Every position/size below is expressed in this
        // space and uniformly scaled onto the real viewport at layout
        // time (see onSizeChanged).
        private const val DESIGN_W = 1536f
        private const val DESIGN_H = 864f
        private const val DESIGN_MIN = 864f // min(DESIGN_W, DESIGN_H)

        // Control centers — pushed toward the four corners/edges so the
        // composition spans the full canvas instead of sitting in a
        // smaller cluster. Every pairing below was checked for
        // circle-circle clearance against its neighbors at the sizes
        // defined in onSizeChanged (shR/dR/lsR/etc.), not just eyeballed.
        // LS/RS are the approved, fixed joystick anchors — everything
        // else (shoulders, D-pad, ABXY, L3/R3) is positioned around them.
        //
        // All four constants below are DISTANCES FROM THE NEAREST SCREEN
        // EDGE (left edge for LT/D-pad/L3, right edge for their RB/RT/
        // ABXY/R3 mirrors — see pxL()/pxR() in onSizeChanged). This
        // replaced plain design-space X coordinates: the old scheme
        // (offsetX + designX*scale) centers a fixed 1536x864 canvas and
        // letterboxes it on any phone wider than 16:9 — which is most
        // phones — so these outer controls sat inboard of the true screen
        // edges with unused space beyond them, even though they were
        // already almost touching the edge of the *design* canvas. The
        // sticks/VIEW/HOME/MENU still use the old centered scheme
        // (appropriate for center controls, and required to keep the
        // sticks' pixel position unchanged) — only these edge-hugging
        // clusters were switched to anchor off the real edge.
        //
        // Each constant is (control radius + a ~24-unit visual/touch
        // margin), NOT just the radius alone — the margin is what keeps
        // the control's own edge off the true screen edge. LT and D-pad
        // were bumped up for this (68→88, 180→200); L3 (70) already
        // landed on almost exactly this margin by coincidence, so it's
        // unchanged. All margins verified in onSizeChanged below.
        // LT/LB (and their RB/RT mirrors) sit lower and further apart
        // than earlier passes (Y 72->115, LB_X 245->300) to read as a
        // distinct shoulder row below VIEW/HOME/MENU, matching the
        // reference's layered top rows instead of one flat top edge.
        // D-pad/ABXY were left at their existing position -- they are
        // already at the geometric ceiling against the fixed stick (the
        // D-pad "up" button clears the stick with near-zero margin at
        // its current Y), so they can't move closer without shrinking
        // button radius, which is out of scope here. LB_X=300/Y=115 is
        // the binding pair -- LB vs. the D-pad "up" button clears by
        // ~23 units, tighter than other neighboring pairs but positive;
        // re-verify this pair first if these constants move again.
        private const val LT_X = 88f;    private const val LT_Y = 115f
        private const val LB_X = 300f;   private const val LB_Y = 115f

        private const val VIEW_X = 616f; private const val VIEW_Y = 65f
        private const val HOME_X = 768f; private const val HOME_Y = 65f
        private const val MENU_X = 920f; private const val MENU_Y = 65f

        private const val DPAD_CX = 200f;  private const val DPAD_CY = 380f

        // Fixed anchors — approved joystick position. Do not move.
        private const val LS_CX = 400f;  private const val LS_CY = 670f
        private const val RS_CX = 1136f; private const val RS_CY = 670f

        private const val L3_X = 70f;    private const val L3_Y = 790f
    }

    // ── Paints ──
    private val paintClear = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#000000")
        style = Paint.Style.FILL
    }

    // Shared monochrome round-button style, used for LT/LB/RB/RT, D-pad,
    // ABXY, L3/R3 and the HOME button — thin light-gray outline, dark
    // translucent interior, matching the reference overlay.
    private val paintRound = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#161616")
        style = Paint.Style.FILL
        alpha = 150
    }

    private val paintRoundPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#3A3A3A")
        style = Paint.Style.FILL
        alpha = 200
    }

    private val paintRoundStroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#9A9A9A")
        style = Paint.Style.STROKE
    }

    private val paintStickRingOuter = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#8A8A8A")
        style = Paint.Style.STROKE
    }

    private val paintStickRingInner = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#5A5A5A")
        style = Paint.Style.STROKE
    }

    private val paintStickBase = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#141414")
        style = Paint.Style.FILL
        alpha = 160
    }

    private val paintStickCap = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#B0B0B0")
        style = Paint.Style.FILL
    }

    private val paintStickCapPressed = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#E0E0E0")
        style = Paint.Style.FILL
    }

    private val paintTextLight = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#E8E8E8")
        textAlign = Paint.Align.CENTER
    }

    // Larger/bolder paint for PlayStation face-button symbols (△ □ ○ ✖)
    private val paintPsSymbol = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#E8E8E8")
        textAlign = Paint.Align.CENTER
    }

    private val paintTextMuted = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#AAAAAA")
        textAlign = Paint.Align.CENTER
    }

    // ── State ──
    private var state = GamepadState()
    private val pointerMap = mutableMapOf<Int, String>()

    // Purely local visual feedback for HOME — never touches GamepadState.
    private var homePressed = false

    // ── Geometry ──
    // Raw viewport size and the uniform design→viewport scale/offset
    // computed each time onSizeChanged runs. Every control rect below
    // is derived from these plus the DESIGN_* constants above.
    private var w = 0f
    private var h = 0f
    private var designScale = 0f

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

    // D-pad — 4 separate circles (up, down, left, right)
    private val dpadRects = mutableMapOf<String, RectF>()

    // ABXY — 4 separate circles
    private val abxyRects = mutableMapOf<String, RectF>()

    // Top-corner circular buttons: lt, lb, rb, rt
    private val roundRects = mutableMapOf<String, RectF>()

    // Top-center: select (VIEW) / start (MENU) pills, home circle
    private val pillRects = mutableMapOf<String, RectF>()
    private val homeRect = RectF()

    // Lower-corner L3 / R3
    private val l3Rect = RectF()
    private val r3Rect = RectF()

    override fun onSizeChanged(width: Int, height: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(width, height, oldw, oldh)
        w = width.toFloat()
        h = height.toFloat()

        // Uniform scale: fit the whole DESIGN_W x DESIGN_H composition
        // inside the real viewport without ever stretching it — the
        // tighter of the two axis ratios wins, and the other axis is
        // centered with leftover space as margin. This is what keeps
        // circles circular and the relative layout identical across
        // aspect ratios/DPIs/resolutions, instead of recomputing each
        // control independently from width/height percentages.
        val scale = kotlin.math.min(w / DESIGN_W, h / DESIGN_H)
        designScale = scale
        val offsetX = (w - DESIGN_W * scale) / 2f
        val offsetY = (h - DESIGN_H * scale) / 2f

        fun px(designX: Float) = offsetX + designX * scale
        fun py(designY: Float) = offsetY + designY * scale
        fun ps(designSize: Float) = designSize * scale

        // True-edge anchors for the outer control clusters (shoulders,
        // D-pad/ABXY, L3/R3): measured from the real view edge (x=0 or
        // x=w), NOT from the letterboxed design canvas. This is what lets
        // those controls actually reach the physical screen edge on
        // phones wider than the 16:9 design canvas, instead of stopping
        // at the canvas's letterboxed edge with unused space beyond it.
        fun pxL(edgeDist: Float) = edgeDist * scale
        fun pxR(edgeDist: Float) = w - edgeDist * scale

        // ── Text / stroke scaling (all driven by the same scale factor) ──
        paintTextLight.textSize = ps(DESIGN_MIN * 0.032f)
        paintPsSymbol.textSize = ps(DESIGN_MIN * 0.032f * 1.35f)
        paintTextMuted.textSize = ps(DESIGN_MIN * 0.028f)

        paintStickRingOuter.strokeWidth = ps(DESIGN_MIN * 0.006f)
        paintStickRingInner.strokeWidth = ps(DESIGN_MIN * 0.004f)
        paintRoundStroke.strokeWidth = ps(DESIGN_MIN * 0.004f)

        // ── Top corners: LT/LB pair (left), RB/RT pair (right) ──
        // RB/RT are LB/LT's mirror, anchored from the right edge via
        // pxR() — same distances, same Y, so the pair is always
        // perfectly symmetric regardless of aspect ratio.
        val shR = ps(DESIGN_MIN * 0.074f)

        roundRects.clear()
        roundRects["lt"] = makeRect(pxL(LT_X), py(LT_Y), shR)
        roundRects["lb"] = makeRect(pxL(LB_X), py(LB_Y), shR)
        roundRects["rb"] = makeRect(pxR(LB_X), py(LB_Y), shR)
        roundRects["rt"] = makeRect(pxR(LT_X), py(LT_Y), shR)

        // ── Top-center: VIEW pill, HOME circle, MENU pill ──
        val pillW = ps(DESIGN_W * 0.104f)
        val pillH = ps(DESIGN_MIN * 0.069f)
        val homeR = ps(DESIGN_MIN * 0.053f)

        val homeCx = px(HOME_X)
        val homeCy = py(HOME_Y)
        homeRect.set(homeCx - homeR, homeCy - homeR, homeCx + homeR, homeCy + homeR)

        val viewCx = px(VIEW_X)
        val viewCy = py(VIEW_Y)
        val menuCx = px(MENU_X)
        val menuCy = py(MENU_Y)

        pillRects.clear()
        pillRects["select"] = RectF(
            viewCx - pillW / 2f, viewCy - pillH / 2f,
            viewCx + pillW / 2f, viewCy + pillH / 2f
        )
        pillRects["start"] = RectF(
            menuCx - pillW / 2f, menuCy - pillH / 2f,
            menuCx + pillW / 2f, menuCy + pillH / 2f
        )

        // ── Sticks ──
        lsR = ps(DESIGN_MIN * 0.179f)
        lsKnobR = lsR * 0.40f
        lsCx = px(LS_CX)
        lsCy = py(LS_CY)
        lsKnobX = lsCx
        lsKnobY = lsCy

        rsR = ps(DESIGN_MIN * 0.179f)
        rsKnobR = rsR * 0.40f
        rsCx = px(RS_CX)
        rsCy = py(RS_CY)
        rsKnobX = rsCx
        rsKnobY = rsCy

        // ── D-pad: 4 separate circles in a diamond (▲ / ◀ ▶ / ▼) ──
        // Anchored to the true left edge via pxL() (see note above) —
        // this is what actually pulls it out to the physical screen
        // edge on wide phones instead of stopping at the letterboxed
        // canvas edge.
        val dpadCx = pxL(DPAD_CX)
        val dpadCy = py(DPAD_CY)
        val dR = ps(DESIGN_MIN * 0.074f)
        val dOffset = dR * 1.75f
        dpadRects.clear()
        dpadRects["dUp"] = makeRect(dpadCx, dpadCy - dOffset, dR)
        dpadRects["dLeft"] = makeRect(dpadCx - dOffset, dpadCy, dR)
        dpadRects["dRight"] = makeRect(dpadCx + dOffset, dpadCy, dR)
        dpadRects["dDown"] = makeRect(dpadCx, dpadCy + dOffset, dR)

        // ── ABXY: 4 separate circles in a diamond ── (right-edge mirror of D-pad)
        val abxyCx = pxR(DPAD_CX)
        val abxyCy = py(DPAD_CY)
        val br = ps(DESIGN_MIN * 0.074f)
        val abxyOffset = br * 1.75f
        abxyRects.clear()
        abxyRects["y"] = makeRect(abxyCx, abxyCy - abxyOffset, br)
        abxyRects["x"] = makeRect(abxyCx - abxyOffset, abxyCy, br)
        abxyRects["b"] = makeRect(abxyCx + abxyOffset, abxyCy, br)
        abxyRects["a"] = makeRect(abxyCx, abxyCy + abxyOffset, br)

        // ── L3 / R3 — separate circular buttons beside the sticks ──
        // R3 mirrors L3 from the right edge via pxR().
        val l3R = ps(DESIGN_MIN * 0.053f)
        val l3Cx = pxL(L3_X)
        val l3Cy = py(L3_Y)
        val r3Cx = pxR(L3_X)
        val r3Cy = py(L3_Y)
        l3Rect.set(l3Cx - l3R, l3Cy - l3R, l3Cx + l3R, l3Cy + l3R)
        r3Rect.set(r3Cx - l3R, r3Cy - l3R, r3Cx + l3R, r3Cy + l3R)
    }

    private fun makeRect(cx: Float, cy: Float, radius: Float): RectF {
        return RectF(cx - radius, cy - radius, cx + radius, cy + radius)
    }

    private fun drawRoundBtn(canvas: Canvas, rect: RectF, pressed: Boolean, label: String) {
        drawRoundBtn(canvas, rect, pressed, label, paintTextLight)
    }

    private fun drawRoundBtn(canvas: Canvas, rect: RectF, pressed: Boolean, label: String, paint: Paint) {
        val cx = rect.centerX()
        val cy = rect.centerY()
        val r = (rect.right - rect.left) / 2f
        canvas.drawCircle(cx, cy, r, if (pressed) paintRoundPressed else paintRound)
        canvas.drawCircle(cx, cy, r, paintRoundStroke)
        if (label.isNotEmpty()) {
            canvas.drawText(label, cx, cy + paint.textSize / 3, paint)
        }
    }

    override fun onDraw(canvas: Canvas) {
        canvas.drawPaint(paintClear)

        // ── D-pad ──
        drawRoundBtn(canvas, dpadRects["dUp"]!!, state.dUp, "\u25B2")
        drawRoundBtn(canvas, dpadRects["dLeft"]!!, state.dLeft, "\u25C0")
        drawRoundBtn(canvas, dpadRects["dRight"]!!, state.dRight, "\u25B6")
        drawRoundBtn(canvas, dpadRects["dDown"]!!, state.dDown, "\u25BC")

        // ── ABXY (label depends on controller style) ──
        when (controllerStyle) {
            ControllerStyle.XBOX -> {
                drawRoundBtn(canvas, abxyRects["y"]!!, state.y, "Y")
                drawRoundBtn(canvas, abxyRects["x"]!!, state.x, "X")
                drawRoundBtn(canvas, abxyRects["b"]!!, state.b, "B")
                drawRoundBtn(canvas, abxyRects["a"]!!, state.a, "A")
            }
            ControllerStyle.PS -> {
                drawRoundBtn(canvas, abxyRects["y"]!!, state.y, "△", paintPsSymbol)
                drawRoundBtn(canvas, abxyRects["x"]!!, state.x, "□", paintPsSymbol)
                drawRoundBtn(canvas, abxyRects["b"]!!, state.b, "○", paintPsSymbol)
                drawRoundBtn(canvas, abxyRects["a"]!!, state.a, "✖", paintPsSymbol)
            }
        }

        // ── Top-corner shoulder/trigger (label depends on controller style) ──
        val (ltLabel, lbLabel, rbLabel, rtLabel) = when (controllerStyle) {
            ControllerStyle.XBOX -> arrayOf("LT", "LB", "RB", "RT")
            ControllerStyle.PS   -> arrayOf("L2", "L1", "R1", "R2")
        }
        drawRoundBtn(canvas, roundRects["lt"]!!, state.lt > 0f, ltLabel)
        drawRoundBtn(canvas, roundRects["lb"]!!, state.lb, lbLabel)
        drawRoundBtn(canvas, roundRects["rb"]!!, state.rb, rbLabel)
        drawRoundBtn(canvas, roundRects["rt"]!!, state.rt > 0f, rtLabel)

        // ── L3 / R3 ──
        drawRoundBtn(canvas, l3Rect, state.l3, "L3")
        drawRoundBtn(canvas, r3Rect, state.r3, "R3")

        // ── Top-center: VIEW pill / HOME circle / MENU pill ──
        pillRects["select"]?.let { rect ->
            canvas.drawRoundRect(rect, rect.height() / 2f, rect.height() / 2f, if (state.select) paintRoundPressed else paintRound)
            canvas.drawRoundRect(rect, rect.height() / 2f, rect.height() / 2f, paintRoundStroke)
            val viewLabel = if (controllerStyle == ControllerStyle.PS) "CREATE" else "VIEW"
            canvas.drawText(viewLabel, rect.centerX(), rect.centerY() + paintTextMuted.textSize / 3, paintTextMuted)
        }
        pillRects["start"]?.let { rect ->
            canvas.drawRoundRect(rect, rect.height() / 2f, rect.height() / 2f, if (state.start) paintRoundPressed else paintRound)
            canvas.drawRoundRect(rect, rect.height() / 2f, rect.height() / 2f, paintRoundStroke)
            val menuLabel = if (controllerStyle == ControllerStyle.PS) "OPTIONS" else "MENU"
            canvas.drawText(menuLabel, rect.centerX(), rect.centerY() + paintTextMuted.textSize / 3, paintTextMuted)
        }
        drawRoundBtn(canvas, homeRect, homePressed, "")
        if (controllerStyle == ControllerStyle.PS) {
            // Draw a small "PS" label inside the home button for PlayStation mode
            canvas.drawText("PS", homeRect.centerX(), homeRect.centerY() + paintTextMuted.textSize / 3, paintTextMuted)
        } else {
            canvas.drawCircle(homeRect.centerX(), homeRect.centerY(), (homeRect.width() / 2f) * 0.32f, paintRoundStroke)
        }

        // ── Left stick ──
        canvas.drawCircle(lsCx, lsCy, lsR, paintStickBase)
        canvas.drawCircle(lsCx, lsCy, lsR, paintStickRingOuter)
        canvas.drawCircle(lsCx, lsCy, lsR * 0.62f, paintStickRingInner)
        canvas.drawCircle(lsKnobX, lsKnobY, lsKnobR, paintStickCap)

        // ── Right stick ──
        canvas.drawCircle(rsCx, rsCy, rsR, paintStickBase)
        canvas.drawCircle(rsCx, rsCy, rsR, paintStickRingOuter)
        canvas.drawCircle(rsCx, rsCy, rsR * 0.62f, paintStickRingInner)
        canvas.drawCircle(rsKnobX, rsKnobY, rsKnobR, paintStickCap)
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
        // Small/specific hit areas first, so they take priority over
        // the larger stick catchment areas below.
        if (l3Rect.contains(x, y)) return "l3"
        if (r3Rect.contains(x, y)) return "r3"
        if (homeRect.contains(x, y)) return "home"

        roundRects.forEach { (name, rect) ->
            if (rect.contains(x, y)) return name
        }
        pillRects.forEach { (name, rect) ->
            if (rect.contains(x, y)) return name
        }
        abxyRects.forEach { (name, rect) ->
            if (rect.contains(x, y)) return name
        }
        dpadRects.forEach { (name, rect) ->
            if (rect.contains(x, y)) return name
        }

        // Sticks last (larger catchment radius for reliable dragging)
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
            "lt" -> state = state.copy(lt = if (pressed) 1f else 0f)
            "rt" -> state = state.copy(rt = if (pressed) 1f else 0f)
            "dUp" -> state = state.copy(dUp = pressed)
            "dDown" -> state = state.copy(dDown = pressed)
            "dLeft" -> state = state.copy(dLeft = pressed)
            "dRight" -> state = state.copy(dRight = pressed)
            "start" -> state = state.copy(start = pressed)
            "select" -> state = state.copy(select = pressed)
            "l3" -> state = state.copy(l3 = pressed)
            "r3" -> state = state.copy(r3 = pressed)
            "home" -> {
                // No GamepadState field exists for Home — visual feedback
                // only, deliberately never touches protocol state.
                homePressed = pressed
            }
        }
        listener?.onStateChanged(state)
    }

    private fun resetState() {
        state = GamepadState()
        homePressed = false
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