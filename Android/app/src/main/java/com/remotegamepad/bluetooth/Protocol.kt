package com.remotegamepad.bluetooth

import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Protocol definition for Milestone 1 + Controller.
 *
 * Framing: [4-byte length, big-endian][UTF-8 payload]
 *
 * This prevents message merging on the Bluetooth stream.
 */
object Protocol {

    const val MSG_HELLO       = "HELLO"
    const val MSG_HELLO_ACK   = "HELLO_ACK"
    const val MSG_PING        = "PING"
    const val MSG_PONG        = "PONG"
    const val MSG_DISCONNECT  = "DISCONNECT"

    /** Controller input message prefix. Payload is JSON after the pipe. */
    const val MSG_INPUT       = "INPUT"

    /** SPP UUID used by both sides. */
    val SPP_UUID: java.util.UUID =
        java.util.UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")

    /** Service name advertised / looked up on Windows. */
    const val SERVICE_NAME = "RemoteGamepad"

    /**
     * Write a framed message to the output stream.
     * Thread-safe as long as the caller serialises access to [out].
     */
    @Throws(java.io.IOException::class)
    fun writeMessage(out: OutputStream, message: String) {
        val payload = message.toByteArray(Charsets.UTF_8)
        val header = ByteBuffer.allocate(4)
            .order(ByteOrder.BIG_ENDIAN)
            .putInt(payload.size)
            .array()
        out.write(header)
        out.write(payload)
        out.flush()
    }

    /**
     * Read a framed message from the input stream.
     * Blocks until a complete message is available.
     *
     * @return the message string, or null if the stream closed cleanly.
     * @throws java.io.IOException on I/O errors.
     */
    @Throws(java.io.IOException::class)
    fun readMessage(`in`: InputStream): String? {
        // Read 4-byte length header
        val header = readFully(`in`, 4) ?: return null
        val length = ByteBuffer.wrap(header).order(ByteOrder.BIG_ENDIAN).int
        if (length < 0 || length > 64 * 1024) {
            throw java.io.IOException("Invalid message length: $length")
        }
        // Read payload
        val payload = readFully(`in`, length) ?: return null
        return String(payload, Charsets.UTF_8)
    }

    /** Reads exactly [count] bytes, or returns null on clean EOF. */
    private fun readFully(`in`: InputStream, count: Int): ByteArray? {
        val buf = ByteArray(count)
        var offset = 0
        while (offset < count) {
            val read = `in`.read(buf, offset, count - offset)
            if (read == -1) {
                return if (offset == 0) null else throw java.io.IOException("Unexpected EOF")
            }
            offset += read
        }
        return buf
    }
}
