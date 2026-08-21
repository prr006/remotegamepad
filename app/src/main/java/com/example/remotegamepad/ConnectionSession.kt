package com.example.remotegamepad

enum class ConnectionMode { WIFI, BLUETOOTH }

/**
 * Holds the single active [GamepadTransport] across the ConnectionActivity
 * -> GamepadActivity handoff.
 *
 * Connection setup (QR/Wi-Fi, Bluetooth pairing) fully owns creating and
 * validating a transport; the gamepad screen never creates or configures
 * one itself - it only ever reads whatever connection screen already
 * established here. This is what keeps the two screens as independent UI
 * states instead of one screen reaching into the other's concerns.
 */
object ConnectionSession {

    var transport: GamepadTransport? = null
        private set
    var mode: ConnectionMode? = null
        private set

    fun set(transport: GamepadTransport, mode: ConnectionMode) {
        this.transport = transport
        this.mode = mode
    }

    /** Tears down the active transport (if any) and clears the session. */
    fun clear() {
        transport?.close()
        transport = null
        mode = null
    }
}
