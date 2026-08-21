package com.example.remotegamepad

import android.content.Context

/**
 * Which glyph set the four face buttons currently display.
 *
 * This is a pure display concern. It never touches the fixed protocol
 * names ("A"/"B"/"X"/"Y") that GamepadActivity sends over the wire, the
 * underlying button IDs, ButtonState, or the transport - those are set up
 * once in GamepadActivity.bindRound and never change. Toggling this value
 * only ever changes what RoundButtonView.label draws on screen.
 */
enum class FaceButtonLayout {
    XBOX, PLAYSTATION;

    companion object {
        fun other(current: FaceButtonLayout): FaceButtonLayout =
            if (current == XBOX) PLAYSTATION else XBOX
    }
}

/**
 * Display glyph for a given face button's fixed protocol name, under this
 * layout. Physical position -> protocol name is decided once in
 * GamepadActivity (btnA="A" is bottom, btnB="B" is right, btnX="X" is
 * left, btnY="Y" is top) and is not affected by this mapping - only the
 * glyph shown at that already-fixed position changes.
 */
fun FaceButtonLayout.glyphFor(protocolName: String): String = when (this) {
    FaceButtonLayout.XBOX -> protocolName
    FaceButtonLayout.PLAYSTATION -> when (protocolName) {
        "A" -> "\u2715" // bottom -> cross
        "B" -> "\u25CB" // right  -> circle
        "X" -> "\u25A1" // left   -> square
        "Y" -> "\u25B3" // top    -> triangle
        else -> protocolName
    }
}

/**
 * Tiny SharedPreferences wrapper that persists the user's chosen
 * face-button layout across app restarts. Mirrors the plain Kotlin-object
 * style already used by ConnectionSession elsewhere in this project -
 * there's no DI framework here, so a small Context-scoped object is the
 * idiomatic fit rather than introducing one.
 */
object FaceButtonLayoutPrefs {
    private const val PREFS_NAME = "remotegamepad_prefs"
    private const val KEY_LAYOUT = "face_button_layout"

    fun get(context: Context): FaceButtonLayout {
        val stored = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getString(KEY_LAYOUT, null)
        return stored?.let {
            runCatching { FaceButtonLayout.valueOf(it) }.getOrNull()
        } ?: FaceButtonLayout.XBOX
    }

    fun set(context: Context, layout: FaceButtonLayout) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_LAYOUT, layout.name)
            .apply()
    }
}
