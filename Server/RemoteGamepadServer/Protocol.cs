namespace RemoteGamepadServer;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Protocol definition for Milestone 1 + Controller.
///
/// Framing: [4-byte length, big-endian][UTF-8 payload]
///
/// Both Android and Windows must use exactly the same framing.
/// </summary>
public static class Protocol
{
    public const string MSG_HELLO      = "HELLO";
    public const string MSG_HELLO_ACK  = "HELLO_ACK";
    public const string MSG_PING       = "PING";
    public const string MSG_PONG       = "PONG";
    public const string MSG_DISCONNECT = "DISCONNECT";

    /// <summary>Controller input message prefix. Payload is JSON after the pipe.</summary>
    public const string MSG_INPUT      = "INPUT";

    /// <summary>Standard SPP UUID — must match Android side.</summary>
    public static readonly Guid SppUuid = Guid.Parse("00001101-0000-1000-8000-00805F9B34FB");

    public const string ServiceName = "RemoteGamepad";

    /// <summary>
    /// Write a framed message. Thread-safe when caller serialises access to <paramref name="stream"/>.
    /// </summary>
    public static void WriteMessage(Stream stream, string message)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        stream.Write(header, 0, 4);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    /// <summary>
    /// Read a framed message. Blocks until a complete message arrives.
    /// Returns null on clean EOF.
    /// </summary>
    public static string? ReadMessage(Stream stream, CancellationToken ct = default)
    {
        var header = ReadFully(stream, 4, ct);
        if (header == null) return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > 64 * 1024)
            throw new IOException($"Invalid message length: {length}");

        var payload = ReadFully(stream, length, ct);
        if (payload == null) throw new IOException("Unexpected EOF after header");

        return Encoding.UTF8.GetString(payload);
    }

    private static byte[]? ReadFully(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            ct.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
                return offset == 0 ? null : throw new IOException("Unexpected EOF");
            offset += read;
        }
        return buffer;
    }
}
