package com.remotegamepad.bluetooth

import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
import kotlinx.coroutines.*
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Isolated Bluetooth Classic transport layer.
 *
 * Responsibilities:
 * - Connect to a remote Bluetooth device (RFCOMM / SPP)
 * - Send/receive framed messages using [Protocol]
 * - Notify callers of state changes via callbacks
 * - Clean up resources on disconnect
 * - Support reconnection by creating a fresh instance
 */
class BluetoothService(private val context: Context) {

    companion object {
        private const val TAG = "BluetoothService"
    }

    sealed class State {
        object Disconnected : State()
        object Connecting : State()
        object Connected : State()
        data class Error(val reason: String) : State()
    }

    interface Callback {
        fun onStateChanged(state: State)
        fun onMessageReceived(message: String)
        fun onError(message: String)
    }

    private val adapter: BluetoothAdapter? = BluetoothAdapter.getDefaultAdapter()
    private var socket: BluetoothSocket? = null
    private var outStream: OutputStream? = null
    private var inStream: InputStream? = null

    private val _state = AtomicBoolean(false) // true = connected
    @Volatile
    private var currentState: State = State.Disconnected

    private var callback: Callback? = null
    private var ioJob: Job? = null
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val mainHandler = Handler(Looper.getMainLooper())

    fun setCallback(cb: Callback) {
        callback = cb
    }

    fun getState(): State = currentState

    fun isConnected(): Boolean = _state.get()

    /**
     * Attempt to connect to [device].
     * Must have BLUETOOTH_CONNECT permission.
     */
    fun connect(device: BluetoothDevice) {
        if (adapter == null) {
            postState(State.Error("Bluetooth not available"))
            return
        }
        if (_state.get() || currentState == State.Connecting) {
            Log.w(TAG, "Already connected or connecting")
            return
        }

        ioJob?.cancel()
        ioJob = serviceScope.launch {
            postState(State.Connecting)
            var tmpSocket: BluetoothSocket? = null
            try {
                tmpSocket = device.createRfcommSocketToServiceRecord(Protocol.SPP_UUID)
                // Cancel discovery to speed up connection
                adapter.cancelDiscovery()
                tmpSocket.connect()

                socket = tmpSocket
                outStream = tmpSocket.outputStream
                inStream = tmpSocket.inputStream
                _state.set(true)
                postState(State.Connected)

                // Start read loop
                readLoop()
            } catch (e: Exception) {
                Log.e(TAG, "Connection failed", e)
                safeClose(tmpSocket)
                postState(State.Error("Connection failed: ${e.message}"))
            }
        }
    }

    /**
     * Send a framed message. Thread-safe.
     */
    fun send(message: String): Boolean {
        return try {
            val out = outStream ?: return false
            if (!_state.get()) return false
            Protocol.writeMessage(out, message)
            true
        } catch (e: IOException) {
            Log.e(TAG, "Send failed", e)
            disconnect()
            false
        }
    }

    /**
     * Gracefully disconnect and clean up.
     */
    fun disconnect() {
        _state.set(false)
        ioJob?.cancel()
        safeClose(socket)
        socket = null
        outStream = null
        inStream = null
        postState(State.Disconnected)
    }

    fun release() {
        disconnect()
        serviceScope.cancel()
    }

    private suspend fun readLoop() {
        val input = inStream ?: return
        try {
            while (_state.get() && ioJob?.isActive == true) {
                val msg = Protocol.readMessage(input)
                if (msg == null) {
                    Log.i(TAG, "Stream closed by remote")
                    break
                }
                mainHandler.post {
                    callback?.onMessageReceived(msg)
                }
                // Auto-respond to protocol messages
                when (msg) {
                    Protocol.MSG_HELLO_ACK -> { /* UI will show it */ }
                    Protocol.MSG_PONG -> { /* UI will show it */ }
                    else -> { /* pass to UI */ }
                }
            }
        } catch (e: IOException) {
            if (_state.get()) {
                Log.e(TAG, "Read error", e)
                mainHandler.post {
                    callback?.onError("Read error: ${e.message}")
                }
            }
        } finally {
            if (_state.get()) {
                // Remote closed or error
                _state.set(false)
                safeClose(socket)
                socket = null
                outStream = null
                inStream = null
                postState(State.Disconnected)
            }
        }
    }

    private fun postState(state: State) {
        currentState = state
        mainHandler.post {
            callback?.onStateChanged(state)
        }
    }

    private fun safeClose(s: BluetoothSocket?) {
        try { s?.close() } catch (_: Exception) { }
    }
}
