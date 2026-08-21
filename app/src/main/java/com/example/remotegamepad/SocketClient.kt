package com.example.remotegamepad

import android.content.Context
import android.net.wifi.WifiManager
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * Persistent, single-sender UDP client.
 *
 * v2 change: buttons and joystick state no longer share one FIFO queue.
 *
 *  - BUTTON events go into a small blocking queue and are always drained
 *    first by the sender loop. A button press can never end up sitting
 *    behind a backlog of joystick packets.
 *  - JOYSTICK state is NOT queued at all. Each stick has a single
 *    "latest value" slot that setJoystick() overwrites in place. The
 *    sender only ever transmits whatever is currently in that slot, so
 *    there is never more than one pending value per stick and it is
 *    always the newest one - old packets can't pile up or be sent stale.
 *
 * Design goals carried over from the previous version:
 *  - Exactly ONE background thread ever calls socket.send().
 *  - send()/setJoystick() are non-blocking from the caller's point of
 *    view, so they're safe to call directly from a touch listener / the
 *    joystick ticker.
 */
class SocketClient(context: Context) : GamepadTransport {

    private val appContext = context.applicationContext

    private var socket: DatagramSocket? = null
    private var serverAddress: InetAddress? = null
    private var serverPort = 5000

    @Volatile
    private var ready = false

    /** Optional UI hook - invoked (off the UI thread) whenever connection state flips. */
    override var onConnectionChanged: ((Boolean) -> Unit)? = null

    // High-priority path: button/dpad/trigger edge events, plus the
    // periodic STATE resync packets (see sendState) - both are low volume
    // compared to joystick traffic and both need to go out ahead of
    // joystick sends, so they share this queue. Bumped from 32 -> 64 to
    // give the ~20-30Hz STATE traffic headroom alongside button mashing
    // without offer() ever needing to silently drop something.
    private val buttonQueue = ArrayBlockingQueue<ByteArray>(64)

    // Monotonically increasing sequence number stamped on every STATE
    // packet so the PC can detect and discard a reordered/stale one (a
    // delayed old snapshot must never overwrite a newer one that already
    // arrived). Owned by the transport, not the caller - GamepadActivity
    // just hands us a mask.
    private val stateSeq = AtomicLong(0)

    // Monotonically increasing sequence numbers, one per stick, stamped on
    // every JOY_* packet so the PC can discard a reordered/delayed old
    // position that arrives after a newer one has already been applied.
    // This closes the hole where a JOY_L:0.0,0.25 torn-read packet could
    // arrive at the server AFTER JOY_L:0.0,0.0 and re-tilt the stick.
    private val joyLSeq = AtomicLong(0)
    private val joyRSeq = AtomicLong(0)

    // Low-priority, latest-value-only path: one slot per stick. Writing
    // a new value overwrites whatever was pending - never queues.
    @Volatile private var pendingJoyL: ByteArray? = null
    @Volatile private var pendingJoyR: ByteArray? = null

    private val senderStarted = AtomicBoolean(false)
    private var senderThread: Thread? = null

    private var wifiLock: WifiManager.WifiLock? = null

    fun connect(ip: String, port: Int) {
        Thread {
            try {
                serverAddress = InetAddress.getByName(ip)
                serverPort = port
                socket = DatagramSocket()
                ready = true
                Log.d("SOCKET", "UDP Ready -> $ip:$port")
                acquireWifiLock()
                startSenderThread()
                onConnectionChanged?.invoke(true)
            } catch (e: Exception) {
                e.printStackTrace()
            }
        }.start()
    }

    override fun isReady(): Boolean = ready

    /**
     * Android drops the WiFi radio into power-save mode whenever it thinks
     * nothing needs full throughput, which shows up as intermittent
     * 50-200ms send stalls - exactly the kind of "random laggy" feeling a
     * fixed-rate ticker can't fix on its own. Holding a high-perf lock for
     * the life of the connection keeps the radio awake and the send path
     * consistent. Requires android.permission.ACCESS_WIFI_STATE (and
     * CHANGE_WIFI_STATE on older API levels) in the manifest.
     */
    private fun acquireWifiLock() {
        try {
            val wm = appContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return
            wifiLock?.release()
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "RemoteGamepad:wifiLock").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private fun startSenderThread() {
        if (!senderStarted.compareAndSet(false, true)) return

        senderThread = Thread {
            while (true) {
                try {
                    // Wait briefly for a button event - this is the ONLY
                    // thing allowed to make us wait, and BlockingQueue
                    // wakes us the instant one arrives, so real button
                    // latency here is microseconds, not milliseconds.
                    val button = buttonQueue.poll(2, TimeUnit.MILLISECONDS)
                    if (button != null) {
                        transmit(button)
                        // Drain any other buttons that queued up in the
                        // meantime before touching joystick state at all.
                        var next = buttonQueue.poll()
                        while (next != null) {
                            transmit(next)
                            next = buttonQueue.poll()
                        }
                    }

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
                } catch (e: InterruptedException) {
                    break
                } catch (e: Exception) {
                    // Don't let one bad send kill the sender loop.
                    e.printStackTrace()
                }
            }
        }.apply {
            isDaemon = true
            name = "UDP-Sender"
                start()
        }
    }

    private fun transmit(data: ByteArray) {
        val addr = serverAddress ?: return
        val sock = socket ?: return
        sock.send(DatagramPacket(data, data.size, addr, serverPort))
    }

    /** Button / dpad / trigger edge events. Always high priority. */
    override fun send(message: String) {
        if (!ready) return
        buttonQueue.offer(message.toByteArray(Charsets.US_ASCII))
    }

    /** Continuous stick state. Latest value always wins, never queued.
     *  Packet format: JOY_L:seq:x,y  (seq is monotonically increasing per stick)
     *  so the server can reject a delayed/reordered old packet that arrives
     *  after a newer one has already been applied. */
    override fun setJoystick(stick: Char, x: Float, y: Float) {
        if (!ready) return
        val seq = if (stick == 'L') joyLSeq.getAndIncrement() else joyRSeq.getAndIncrement()
        val data = "JOY_$stick:$seq:$x,$y".toByteArray(Charsets.US_ASCII)
        if (stick == 'L') pendingJoyL = data else pendingJoyR = data
    }

    /**
     * Periodic authoritative digital-button snapshot. See
     * [GamepadTransport.sendState] - this is the recovery mechanism that
     * fixes a stuck button if a *_UP packet was lost, since the PC applies
     * this as an absolute state rather than a delta.
     */
    override fun sendState(mask: Int, leftX: Float, leftY: Float, rightX: Float, rightY: Float) {
        if (!ready) return
        val seq = stateSeq.getAndIncrement()
        // The authoritative heartbeat includes BOTH digital buttons and
        // absolute joystick positions. This is critical: a lost JOY_* zero
        // packet must not be able to leave a stick held at its last position.
        buttonQueue.offer(encodeState(seq, mask, leftX, leftY, rightX, rightY))
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
        // Best-effort final release/reset, sent synchronously on this
        // thread (NOT via buttonQueue - the sender thread we just
        // interrupted may not get a chance to drain it) while the socket
        // is still open. On a clean disconnect this means the PC doesn't
        // even need to wait out its failsafe timeout; if this send itself
        // fails (socket already gone, network down) the PC-side timeout
        // still catches it.
        if (ready) {
            try {
                transmit(encodeState(stateSeq.getAndIncrement(), 0, 0f, 0f, 0f, 0f))
            } catch (e: Exception) {
                // Best effort only - socket may already be unusable.
            }
        }
        socket?.close()
        ready = false
        try {
            wifiLock?.release()
        } catch (e: Exception) {
            // already released
        }
        wifiLock = null
        onConnectionChanged?.invoke(false)
    }
}
