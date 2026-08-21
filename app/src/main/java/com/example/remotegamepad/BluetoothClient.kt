package com.example.remotegamepad

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import android.util.Log
import java.io.OutputStream
import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * Bluetooth (classic RFCOMM / SPP) transport.
 *
 * This is a separate connection layer from [SocketClient]: it implements
 * the exact same [GamepadTransport] contract and reuses the exact same
 * plain-text protocol ("<NAME>_DOWN" / "<NAME>_UP" / "JOY_L:x,y") - only
 * the wire it travels over is different (a Bluetooth byte stream instead
 * of UDP datagrams). Adding this never touches SocketClient or the Wi-Fi
 * UDP packet protocol.
 */
class BluetoothClient : GamepadTransport {

    companion object {
        // Standard Serial Port Profile UUID.
        private val SPP_UUID: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    }

    private var socket: BluetoothSocket? = null
    private var output: OutputStream? = null

    @Volatile private var ready = false
    override var onConnectionChanged: ((Boolean) -> Unit)? = null

    /**
     * Diagnostic only: the exact exception text from the most recent failed
     * [connect] attempt, so the UI can display it instead of a generic
     * "couldn't connect" message. Does not affect connection behavior.
     */
    @Volatile var lastError: String? = null
        private set

    // Same high-priority / latest-value-only split as SocketClient, so
    // button and stick latency characteristics match across transports.
    // Capacity matches SocketClient's bump (32 -> 64) to leave headroom
    // for STATE traffic sharing the queue with button edges.
    private val buttonQueue = ArrayBlockingQueue<ByteArray>(64)

    // EXPERIMENT 1 (Bluetooth-only): STATE heartbeat packets get their own
    // queue, separate from buttonQueue, so the sender loop can drain them
    // strictly after button events instead of FIFO-mixed with them. On
    // UDP this split doesn't exist because draining a mixed queue is cheap
    // enough not to matter; on RFCOMM each queued transmit is expensive
    // enough that STATE backlog measurably delays higher-priority traffic
    // behind it, so here it gets its own lowest-priority lane.
    private val stateQueue = ArrayBlockingQueue<ByteArray>(64)

    @Volatile private var pendingJoyL: ByteArray? = null
    @Volatile private var pendingJoyR: ByteArray? = null

    // Same STATE sequence-numbering scheme as SocketClient. RFCOMM is a
    // reliable, ordered stream so this transport doesn't actually need the
    // resync for correctness, but implementing it keeps both transports on
    // one shared wire protocol (see class doc) instead of diverging.
    private val stateSeq = AtomicLong(0)

    // Per-stick sequence numbers for JOY_ packets - same rationale as
    // SocketClient: lets the PC discard a delayed/reordered old position
    // that arrives after a newer one has already been applied.
    private val joyLSeq = AtomicLong(0)
    private val joyRSeq = AtomicLong(0)

    private val senderStarted = AtomicBoolean(false)
    private var senderThread: Thread? = null

    /**
     * Connects to [device] on a background thread. [onResult] is invoked
     * once (off the UI thread) with whether the RFCOMM connection was
     * established.
     */
    @SuppressLint("MissingPermission")
    fun connect(device: BluetoothDevice, onResult: (Boolean) -> Unit) {
        Thread {
            try {
                BluetoothAdapter.getDefaultAdapter()?.cancelDiscovery()
                val sock = device.createRfcommSocketToServiceRecord(SPP_UUID)
                sock.connect()
                socket = sock
                output = sock.outputStream
                ready = true
                startSenderThread()
                Log.d("BLUETOOTH", "Connected -> ${device.name ?: device.address}")
                onConnectionChanged?.invoke(true)
                onResult(true)
            } catch (e: Exception) {
                Log.e("BLUETOOTH", "Bluetooth connection failed", e)
                e.printStackTrace()
                // TEMPORARY DIAGNOSTIC: capture the exact exception class/message
                // plus the first few stack frames so the UI can show precisely
                // which API call threw, without needing adb/Logcat. Read-only -
                // does not alter the connect attempt above in any way.
                val trace = e.stackTrace
                    .take(5)
                    .joinToString("\n") { "    at $it" }
                lastError = buildString {
                    append(e.javaClass.name)
                    append(": ")
                    append(e.message)
                    if (trace.isNotEmpty()) {
                        append("\n")
                        append(trace)
                    }
                }
                ready = false
                onResult(false)
            }
        }.start()
    }

    override fun isReady(): Boolean = ready

    private fun startSenderThread() {
        if (!senderStarted.compareAndSet(false, true)) return

        senderThread = Thread {
            // EXPERIMENT 1 (Bluetooth-only, controlled test - see class doc):
            // priority is JOY_L, JOY_R, button events, STATE heartbeat, in
            // that order every iteration. Joystick slots are checked FIRST,
            // before anything is drained from either queue, so a fresh stick
            // value is never stuck behind button/STATE traffic. They remain
            // single latest-value-only slots - never queued - so an unsent
            // older value is simply overwritten and disappears, exactly as
            // before. buttonQueue.poll(2ms) is kept purely as the loop's
            // idle-wait so this thread doesn't busy-spin when nothing is
            // pending; it no longer implies buttons are checked before
            // joystick.
            while (true) {
                try {
                    // Priority 1 & 2: latest pending joystick values, always first.
                    val jl = pendingJoyL
                    if (jl != null) {
                        pendingJoyL = null
                        transmit(jl)
                    }
                    val jr = pendingJoyR
                    if (jr != null) {
                        pendingJoyR = null
                        transmit(jr)
                    }

                    // Priority 3: button events.
                    val button = buttonQueue.poll(2, TimeUnit.MILLISECONDS)
                    if (button != null) {
                        transmit(button)
                        var next = buttonQueue.poll()
                        while (next != null) {
                            transmit(next)
                            next = buttonQueue.poll()
                        }
                    }

                    // Priority 4: STATE heartbeat, drained last.
                    var state = stateQueue.poll()
                    while (state != null) {
                        transmit(state)
                        state = stateQueue.poll()
                    }
                } catch (e: InterruptedException) {
                    break
                } catch (e: Exception) {
                    e.printStackTrace()
                }
            }
        }.apply {
            isDaemon = true
            name = "BT-Sender"
            start()
        }
    }

    private fun transmit(data: ByteArray) {
        val out = output ?: return
        try {
            // Unlike UDP, an RFCOMM socket is a continuous byte stream
            // with no built-in message boundaries, so frame each message
            // with a newline for the receiving side to split on.
            //
            // EXPERIMENT 2: payload and delimiter are combined into a
            // single byte array and written in ONE write() call instead of
            // two, so this transmit is at most one underlying stream write
            // instead of two before the single flush(). Framing (trailing
            // '\n') and wire format are unchanged - the receiving side
            // sees byte-for-byte the same stream.
            val framed = ByteArray(data.size + 1)
            System.arraycopy(data, 0, framed, 0, data.size)
            framed[data.size] = '\n'.code.toByte()
            out.write(framed)
            out.flush()
        } catch (e: Exception) {
            e.printStackTrace()
            ready = false
            onConnectionChanged?.invoke(false)
        }
    }

    /** Button / dpad / trigger edge events. Always high priority. */
    override fun send(message: String) {
        if (!ready) return
        buttonQueue.offer(message.toByteArray(Charsets.US_ASCII))
    }

    /** Continuous stick state. Latest value always wins, never queued.
     *  Packet format: JOY_L:seq:x,y — same as SocketClient. */
    override fun setJoystick(stick: Char, x: Float, y: Float) {
        if (!ready) return
        val seq = if (stick == 'L') joyLSeq.getAndIncrement() else joyRSeq.getAndIncrement()
        val data = "JOY_$stick:$seq:$x,$y".toByteArray(Charsets.US_ASCII)
        if (stick == 'L') pendingJoyL = data else pendingJoyR = data
    }

    /** Periodic authoritative digital-button snapshot - see SocketClient.sendState.
     *  EXPERIMENT 1: goes into stateQueue (lowest priority), not buttonQueue. */
    override fun sendState(mask: Int, leftX: Float, leftY: Float, rightX: Float, rightY: Float) {
        if (!ready) return
        val seq = stateSeq.getAndIncrement()
        stateQueue.offer(encodeState(seq, mask, leftX, leftY, rightX, rightY))
    }

    private fun encodeState(
        seq: Long,
        mask: Int,
        leftX: Float,
        leftY: Float,
        rightX: Float,
        rightY: Float
    ): ByteArray =
        "STATE:$seq:${mask and 0xFFFF}:$leftX,$leftY:$rightX,$rightY"
            .toByteArray(Charsets.US_ASCII)

    override fun close() {
        senderThread?.interrupt()
        senderStarted.set(false)
        // Best-effort final release, sent synchronously before the stream
        // closes - see SocketClient.close() for the reasoning.
        if (ready) {
            try {
                transmit(encodeState(stateSeq.getAndIncrement(), 0, 0f, 0f, 0f, 0f))
            } catch (e: Exception) {
                // Best effort only - stream may already be unusable.
            }
        }
        ready = false
        try { output?.close() } catch (e: Exception) { /* already closed */ }
        try { socket?.close() } catch (e: Exception) { /* already closed */ }
        output = null
        socket = null
        onConnectionChanged?.invoke(false)
    }
}