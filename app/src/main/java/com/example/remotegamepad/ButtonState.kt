package com.example.remotegamepad

/**
 * Thread-safe authoritative snapshot of every digital control on the pad.
 *
 * [set] is called from the UI thread (once per touch-driven onPress /
 * onDirection callback, the same place that fires the existing DOWN/UP
 * edge events). [mask] is called from the periodic state-heartbeat thread
 * (see GamepadActivity.startStateTicker) to pack the current snapshot for
 * a STATE packet. Those are two different threads touching the same data,
 * so all access goes through [lock].
 *
 * This is intentionally a *separate* path from the DOWN/UP edge events:
 * those keep going out immediately and unbuffered for low latency. This
 * class only exists so that if one of those edge packets (almost always
 * the *_UP one) is lost on the wire, the next periodic snapshot corrects
 * the receiver automatically instead of leaving a button stuck held.
 *
 * Bit order must be kept in sync with Server/Program.cs's MaskButtons.
 */
class ButtonState {

    companion object {
        val ORDER = listOf(
            "A", "B", "X", "Y",
            "LB", "RB", "LS", "RS",
            "START", "SELECT",
            "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT",
            "LT", "RT"
        )
    }

    private val lock = Any()
    private val flags = BooleanArray(ORDER.size)
    private val indexOf: Map<String, Int> = ORDER.withIndex().associate { (i, name) -> name to i }

    /**
     * Records the current press/release state of one control. Unknown
     * names (e.g. "HOME", which has no corresponding Xbox360 button on the
     * PC side either) are ignored rather than throwing, so callers don't
     * need a special case for buttons that fall outside the tracked set.
     */
    fun set(name: String, pressed: Boolean) {
        val idx = indexOf[name] ?: return
        synchronized(lock) { flags[idx] = pressed }
    }

    /** Packs the current snapshot into a compact bitmask (bit i = ORDER[i]). */
    fun mask(): Int {
        synchronized(lock) {
            var m = 0
            for (i in flags.indices) {
                if (flags[i]) m = m or (1 shl i)
            }
            return m
        }
    }
}
