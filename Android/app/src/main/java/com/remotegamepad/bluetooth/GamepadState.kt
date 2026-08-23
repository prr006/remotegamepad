package com.remotegamepad.bluetooth

import org.json.JSONObject

/**
 * Represents the state of a gamepad controller.
 * All values are normalized:
 *   - Axes: -1.0 to +1.0 (0.0 = center)
 *   - Triggers: 0.0 to 1.0
 *   - Buttons: true = pressed
 */
data class GamepadState(
    val lx: Float = 0f,   // Left stick X
    val ly: Float = 0f,   // Left stick Y
    val rx: Float = 0f,   // Right stick X
    val ry: Float = 0f,   // Right stick Y
    val lt: Float = 0f,   // Left trigger
    val rt: Float = 0f,   // Right trigger
    val a: Boolean = false,
    val b: Boolean = false,
    val x: Boolean = false,
    val y: Boolean = false,
    val lb: Boolean = false,  // Left bumper
    val rb: Boolean = false,  // Right bumper
    val dUp: Boolean = false,
    val dDown: Boolean = false,
    val dLeft: Boolean = false,
    val dRight: Boolean = false,
    val start: Boolean = false,
    val select: Boolean = false,
    val l3: Boolean = false,  // Left stick click
    val r3: Boolean = false   // Right stick click
) {
    /**
     * Serialize to a compact JSON string prefixed with INPUT|.
     */
    fun toProtocolMessage(): String {
        val json = JSONObject().apply {
            put("lx", lx)
            put("ly", ly)
            put("rx", rx)
            put("ry", ry)
            put("lt", lt)
            put("rt", rt)
            put("a", a)
            put("b", b)
            put("x", x)
            put("y", y)
            put("lb", lb)
            put("rb", rb)
            put("dUp", dUp)
            put("dDown", dDown)
            put("dLeft", dLeft)
            put("dRight", dRight)
            put("start", start)
            put("select", select)
            put("l3", l3)
            put("r3", r3)
        }
        return "${Protocol.MSG_INPUT}|${json.toString()}"
    }

    companion object {
        fun fromProtocolMessage(message: String): GamepadState? {
            if (!message.startsWith("${Protocol.MSG_INPUT}|")) return null
            val jsonStr = message.substringAfter("${Protocol.MSG_INPUT}|")
            val json = JSONObject(jsonStr)
            return GamepadState(
                lx = json.optDouble("lx", 0.0).toFloat(),
                ly = json.optDouble("ly", 0.0).toFloat(),
                rx = json.optDouble("rx", 0.0).toFloat(),
                ry = json.optDouble("ry", 0.0).toFloat(),
                lt = json.optDouble("lt", 0.0).toFloat(),
                rt = json.optDouble("rt", 0.0).toFloat(),
                a = json.optBoolean("a", false),
                b = json.optBoolean("b", false),
                x = json.optBoolean("x", false),
                y = json.optBoolean("y", false),
                lb = json.optBoolean("lb", false),
                rb = json.optBoolean("rb", false),
                dUp = json.optBoolean("dUp", false),
                dDown = json.optBoolean("dDown", false),
                dLeft = json.optBoolean("dLeft", false),
                dRight = json.optBoolean("dRight", false),
                start = json.optBoolean("start", false),
                select = json.optBoolean("select", false),
                l3 = json.optBoolean("l3", false),
                r3 = json.optBoolean("r3", false)
            )
        }
    }
}
