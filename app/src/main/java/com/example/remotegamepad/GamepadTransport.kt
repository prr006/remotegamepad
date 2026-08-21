package com.example.remotegamepad

/**
 * Common contract implemented by every connection transport (Wi-Fi UDP,
 * Bluetooth, and - in the future - USB).
 *
 * The gamepad screen talks to whichever transport is currently active
 * purely through this interface. It never knows or cares whether input
 * is travelling over UDP, Bluetooth RFCOMM, or (later) USB - which is
 * what lets connection setup and the gamepad UI live as completely
 * separate concerns.
 */
interface GamepadTransport {

    /** Optional UI hook - invoked (off the UI thread) whenever connection state flips. */
    var onConnectionChanged: ((Boolean) -> Unit)?

    fun isReady(): Boolean

    /** Button / dpad / trigger edge events. Always high priority. */
    fun send(message: String)

    /** Continuous stick state. Latest value always wins, never queued. */
    fun setJoystick(stick: Char, x: Float, y: Float)

    /**
     * Authoritative snapshot of every digital button (see [ButtonState]),
     * sent periodically as a low-rate resync mechanism. This is what lets
     * the receiving side recover automatically if an individual DOWN/UP
     * edge event never arrived - it is NOT a replacement for [send]'s
     * immediate edge events, which remain the low-latency path.
     *
     * The transport owns and stamps its own monotonically increasing
     * sequence number so the receiver can safely discard a state packet
     * that arrives out of order behind a newer one.
     */
    fun sendState(mask: Int, leftX: Float, leftY: Float, rightX: Float, rightY: Float)

    fun close()
}
