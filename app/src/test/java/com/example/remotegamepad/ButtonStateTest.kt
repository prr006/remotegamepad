package com.example.remotegamepad

import org.junit.Test
import org.junit.Assert.*

/**
 * Unit tests for ButtonState bitmask ordering.
 * Verifies that ORDER includes HOME and maintains correct bit positions
 * in sync with Server/Program.cs's MaskButtons array.
 */
class ButtonStateTest {

    @Test
    fun order_contains_home() {
        // HOME button must be present in ORDER for STATE mask synchronization
        assertTrue("ORDER should contain HOME", ButtonState.ORDER.contains("HOME"))
    }

    @Test
    fun order_has_correct_size() {
        // 17 buttons: A,B,X,Y, LB,RB,LS,RS, START,SELECT,HOME, DPAD_UP/DOWN/LEFT/RIGHT, LT,RT
        assertEquals(17, ButtonState.ORDER.size)
    }

    @Test
    fun order_positions_match_maskbuttons() {
        // Bit positions must match Server/Program.cs MaskButtons exactly:
        // 0:A, 1:B, 2:X, 3:Y, 4:LB, 5:RB, 6:LS, 7:RS, 8:START, 9:SELECT, 10:HOME(Guide),
        // 11:DPAD_UP, 12:DPAD_DOWN, 13:DPAD_LEFT, 14:DPAD_RIGHT, 15:LT, 16:RT
        val expectedOrder = listOf(
            "A", "B", "X", "Y",
            "LB", "RB", "LS", "RS",
            "START", "SELECT", "HOME",
            "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT",
            "LT", "RT"
        )
        assertEquals(expectedOrder, ButtonState.ORDER)
    }

    @Test
    fun set_and_mask_single_button() {
        val state = ButtonState()
        
        state.set("A", true)
        assertEquals(1, state.mask()) // bit 0
        
        state.set("A", false)
        assertEquals(0, state.mask())
    }

    @Test
    fun set_and_mask_home_button() {
        val state = ButtonState()
        
        // HOME is at index 10 (bit position 10)
        state.set("HOME", true)
        assertEquals(1 shl 10, state.mask())
        
        state.set("HOME", false)
        assertEquals(0, state.mask())
    }

    @Test
    fun set_and_mask_multiple_buttons() {
        val state = ButtonState()
        
        state.set("A", true)      // bit 0
        state.set("B", true)      // bit 1
        state.set("HOME", true)   // bit 10
        
        val expectedMask = (1 shl 0) or (1 shl 1) or (1 shl 10)
        assertEquals(expectedMask, state.mask())
    }

    @Test
    fun unknown_button_ignored() {
        val state = ButtonState()
        
        // Unknown button names should be silently ignored
        state.set("UNKNOWN_BUTTON", true)
        assertEquals(0, state.mask())
        
        // Valid button should still work
        state.set("A", true)
        assertEquals(1, state.mask())
    }

    @Test
    fun dpad_buttons_at_correct_positions() {
        val state = ButtonState()
        
        // DPAD_UP=11, DPAD_DOWN=12, DPAD_LEFT=13, DPAD_RIGHT=14
        state.set("DPAD_UP", true)
        assertEquals(1 shl 11, state.mask())
        
        state.set("DPAD_DOWN", true)
        assertEquals((1 shl 11) or (1 shl 12), state.mask())
        
        state.set("DPAD_LEFT", true)
        assertEquals((1 shl 11) or (1 shl 12) or (1 shl 13), state.mask())
        
        state.set("DPAD_RIGHT", true)
        assertEquals((1 shl 11) or (1 shl 12) or (1 shl 13) or (1 shl 14), state.mask())
    }

    @Test
    fun trigger_buttons_at_correct_positions() {
        val state = ButtonState()
        
        // LT=15, RT=16
        state.set("LT", true)
        assertEquals(1L shl 15, state.mask().toLong())
        
        state.set("RT", true)
        assertEquals((1L shl 15) or (1L shl 16), state.mask().toLong())
    }
}
