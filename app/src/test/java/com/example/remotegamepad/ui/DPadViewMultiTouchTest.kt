package com.example.remotegamepad.ui

import android.view.MotionEvent
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * Multi-touch tests for DPadView pointer ownership logic.
 * These tests verify the six critical cases from the design analysis:
 * 1. Pointer 0 = UP, Pointer 1 = RIGHT → Pointer 0 lifts → RIGHT remains
 * 2. Pointer 0 = UP, Pointer 1 = UP → Pointer 0 lifts → UP remains (Pointer 1 owns it)
 * 3. Pointer 0 = UP+RIGHT → Pointer 0 moves to DOWN → UP/RIGHT released, DOWN pressed
 * 4. Pointer 0 = UP, Pointer 1 = RIGHT → Pointer 0 moves to RIGHT → RIGHT stays when P0 lifts
 * 5. ACTION_CANCEL → All ownership cleared
 * 6. ACTION_UP after multitouch → All remaining ownership cleared
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class DPadViewMultiTouchTest {

    @Test
    fun `case 1 - two pointers different directions, first lifts, second remains`() {
        val view = createTestDPadView()
        val events = mutableListOf<Pair<String, Boolean>>()
        view.onDirection = { name, pressed -> events.add(name to pressed) }

        // Pointer 0 presses UP area
        val down0 = createMotionEvent(0, MotionEvent.ACTION_DOWN, 0, 100f, 50f)
        view.onTouchEvent(down0)
        
        // Verify UP pressed
        assertTrue(events.contains("UP" to true))
        assertFalse(events.any { it.first == "DOWN" && it.second })
        
        // Pointer 1 presses RIGHT area
        val down1 = createMotionEvent(1, MotionEvent.ACTION_POINTER_DOWN, 1, 150f, 100f)
        view.onTouchEvent(down1)
        
        // Verify RIGHT pressed
        assertTrue(events.contains("RIGHT" to true))
        
        // Clear event log
        events.clear()
        
        // Pointer 0 lifts (ACTION_POINTER_UP)
        val up0 = createMotionEvent(1, MotionEvent.ACTION_POINTER_UP, 0, 100f, 50f)
        view.onTouchEvent(up0)
        
        // UP should be released, RIGHT should remain pressed (no RIGHT_UP event)
        assertTrue(events.contains("UP" to false))
        assertFalse(events.contains("RIGHT" to false))
    }

    @Test
    fun `case 2 - two pointers same direction, first lifts, direction remains`() {
        val view = createTestDPadView()
        val events = mutableListOf<Pair<String, Boolean>>()
        view.onDirection = { name, pressed -> events.add(name to pressed) }

        // Pointer 0 presses UP area
        val down0 = createMotionEvent(0, MotionEvent.ACTION_DOWN, 0, 100f, 50f)
        view.onTouchEvent(down0)
        assertTrue(events.contains("UP" to true))
        events.clear()
        
        // Pointer 1 also presses UP area (same location)
        val down1 = createMotionEvent(1, MotionEvent.ACTION_POINTER_DOWN, 1, 100f, 50f)
        view.onTouchEvent(down1)
        // No new UP event since it's already pressed
        assertFalse(events.contains("UP" to true))
        events.clear()
        
        // Pointer 0 lifts
        val up0 = createMotionEvent(1, MotionEvent.ACTION_POINTER_UP, 0, 100f, 50f)
        view.onTouchEvent(up0)
        
        // UP should NOT be released because Pointer 1 still owns it
        assertFalse(events.contains("UP" to false))
    }

    @Test
    fun `case 3 - single pointer moves from diagonal to opposite direction`() {
        val view = createTestDPadView()
        val events = mutableListOf<Pair<String, Boolean>>()
        view.onDirection = { name, pressed -> events.add(name to pressed) }

        // Pointer 0 presses UP+RIGHT diagonal area
        val down0 = createMotionEvent(0, MotionEvent.ACTION_DOWN, 0, 150f, 50f)
        view.onTouchEvent(down0)
        
        assertTrue(events.contains("UP" to true))
        assertTrue(events.contains("RIGHT" to true))
        events.clear()
        
        // Pointer 0 moves to DOWN area
        val move = createMotionEvent(0, MotionEvent.ACTION_MOVE, 0, 100f, 150f)
        view.onTouchEvent(move)
        
        // UP and RIGHT should be released, DOWN should be pressed
        assertTrue(events.contains("UP" to false))
        assertTrue(events.contains("RIGHT" to false))
        assertTrue(events.contains("DOWN" to true))
        assertFalse(events.contains("LEFT" to true))
    }

    @Test
    fun `case 4 - pointer moves to direction owned by another pointer`() {
        val view = createTestDPadView()
        val events = mutableListOf<Pair<String, Boolean>>()
        view.onDirection = { name, pressed -> events.add(name to pressed) }

        // Pointer 0 presses UP
        val down0 = createMotionEvent(0, MotionEvent.ACTION_DOWN, 0, 100f, 50f)
        view.onTouchEvent(down0)
        assertTrue(events.contains("UP" to true))
        events.clear()
        
        // Pointer 1 presses RIGHT
        val down1 = createMotionEvent(1, MotionEvent.ACTION_POINTER_DOWN, 1, 150f, 100f)
        view.onTouchEvent(down1)
        assertTrue(events.contains("RIGHT" to true))
        events.clear()
        
        // Pointer 0 moves to RIGHT area (now both pointers own RIGHT)
        val move0 = createMotionEvent(0, MotionEvent.ACTION_MOVE, 0, 150f, 100f)
        view.onTouchEvent(move0)
        // No change in RIGHT state (already pressed)
        assertTrue(events.isEmpty() || !events.any { it.first == "RIGHT" })
        events.clear()
        
        // Pointer 0 lifts - RIGHT should remain because Pointer 1 still owns it
        val up0 = createMotionEvent(1, MotionEvent.ACTION_POINTER_UP, 0, 150f, 100f)
        view.onTouchEvent(up0)
        
        assertFalse(events.contains("RIGHT" to false))
    }

    @Test
    fun `case 5 - ACTION_CANCEL clears all ownership`() {
        val view = createTestDPadView()
        val events = mutableListOf<Pair<String, Boolean>>()
        view.onDirection = { name, pressed -> events.add(name to pressed) }

        // Two pointers pressed
        val down0 = createMotionEvent(0, MotionEvent.ACTION_DOWN, 0, 100f, 50f)
        view.onTouchEvent(down0)
        val down1 = createMotionEvent(1, MotionEvent.ACTION_POINTER_DOWN, 1, 150f, 100f)
        view.onTouchEvent(down1)
        
        assertTrue(events.contains("UP" to true))
        assertTrue(events.contains("RIGHT" to true))
        events.clear()
        
        // ACTION_CANCEL
        val cancel = createMotionEvent(0, MotionEvent.ACTION_CANCEL, 0, 100f, 50f)
        view.onTouchEvent(cancel)
        
        // Both directions released
        assertTrue(events.contains("UP" to false))
        assertTrue(events.contains("RIGHT" to false))
    }

    @Test
    fun `case 6 - ACTION_UP after multitouch clears all remaining ownership`() {
        val view = createTestDPadView()
        val events = mutableListOf<Pair<String, Boolean>>()
        view.onDirection = { name, pressed -> events.add(name to pressed) }

        // Two pointers pressed
        val down0 = createMotionEvent(0, MotionEvent.ACTION_DOWN, 0, 100f, 50f)
        view.onTouchEvent(down0)
        val down1 = createMotionEvent(1, MotionEvent.ACTION_POINTER_DOWN, 1, 150f, 100f)
        view.onTouchEvent(down1)
        events.clear()
        
        // Pointer 0 lifts, Pointer 1 remains
        val up0 = createMotionEvent(1, MotionEvent.ACTION_POINTER_UP, 0, 100f, 50f)
        view.onTouchEvent(up0)
        assertTrue(events.contains("UP" to false))
        events.clear()
        
        // Final ACTION_UP (Pointer 1 lifts)
        val up1 = createMotionEvent(0, MotionEvent.ACTION_UP, 1, 150f, 100f)
        view.onTouchEvent(up1)
        
        // RIGHT should be released
        assertTrue(events.contains("RIGHT" to false))
    }

    @Test
    fun `single finger behavior unchanged - press and release`() {
        val view = createTestDPadView()
        val events = mutableListOf<Pair<String, Boolean>>()
        view.onDirection = { name, pressed -> events.add(name to pressed) }

        // Single finger press DOWN
        val down = createMotionEvent(0, MotionEvent.ACTION_DOWN, 0, 100f, 150f)
        view.onTouchEvent(down)
        
        assertTrue(events.contains("DOWN" to true))
        assertEquals(1, events.count { it.second }) // Only one direction pressed
        events.clear()
        
        // Single finger release
        val up = createMotionEvent(0, MotionEvent.ACTION_UP, 0, 100f, 150f)
        view.onTouchEvent(up)
        
        assertTrue(events.contains("DOWN" to false))
    }

    @Test
    fun `diagonal single press produces two directions`() {
        val view = createTestDPadView()
        val events = mutableListOf<Pair<String, Boolean>>()
        view.onDirection = { name, pressed -> events.add(name to pressed) }

        // Single finger at diagonal (UP+RIGHT)
        val down = createMotionEvent(0, MotionEvent.ACTION_DOWN, 0, 150f, 50f)
        view.onTouchEvent(down)
        
        assertTrue(events.contains("UP" to true))
        assertTrue(events.contains("RIGHT" to true))
        assertEquals(2, events.count { it.second })
    }

    private fun createTestDPadView(): DPadView {
        return DPadView(androidx.test.core.app.ApplicationProvider.getApplicationContext())
    }

    private fun createMotionEvent(
        actionIndex: Int,
        action: Int,
        pointerId: Int,
        x: Float,
        y: Float
    ): MotionEvent {
        val downTime = System.currentTimeMillis()
        val eventTime = downTime
        
        return MotionEvent.obtain(
            downTime,
            eventTime,
            action,
            1, // pointerCount will be adjusted by action type
            arrayOf(MotionEvent.PointerProperties().apply { id = pointerId }),
            arrayOf(MotionEvent.PointerCoords().apply { 
                this.x = x
                this.y = y
                pressure = 1.0f
                size = 1.0f
            }),
            0, // metaState
            0, // buttonState
            1.0f, // xPrecision
            1.0f, // yPrecision
            0, // deviceId
            0, // edgeFlags
            0, // source
            0  // flags
        ).apply {
            // For multi-pointer events, we need to set the action correctly
            // Robolectric handles this through the action parameter
        }
    }
}
