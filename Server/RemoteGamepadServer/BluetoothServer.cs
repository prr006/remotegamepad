namespace RemoteGamepadServer;

using InTheHand.Net.Bluetooth;
using System.Net.Sockets;
using System.Runtime.InteropServices;

/// <summary>BlueZ RFCOMM/SPP server using native RFCOMM sockets and libbluetooth SDP APIs.</summary>
public sealed class BluetoothServer : IDisposable
{
    private const int AfBluetooth = 31, SockStream = 1, BtpProtocolRfcomm = 3;
    private const int SockCloexec = 0x80000, PollIn = 1;
    private const int ShutReadWrite = 2, MsgNoSignal = 0x4000;
    private const byte RfcommChannel = 1;
    private readonly object _sendLock = new();
    private int _listenerFd = -1, _clientFd = -1;
    private RfcommStream? _stream;
    private CancellationTokenSource? _serverCts;
    private Task? _acceptTask;
    private IntPtr _sdpSession;
    private IntPtr _sdpRecord;

    public bool IsConnected => Volatile.Read(ref _clientFd) >= 0;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? Error;
    public event EventHandler<InputParser.ControllerState>? InputReceived;

    public void Start()
    {
        if (_listenerFd >= 0) throw new InvalidOperationException("Bluetooth server is already started.");
        var radio = BluetoothRadio.Default;
        radio.Mode = RadioMode.Discoverable;
        Console.WriteLine($"[BT] Bluetooth adapter: {radio.Name} ({radio.LocalAddress})");

        var fd = bluetoothSocket(AfBluetooth, SockStream | SockCloexec, BtpProtocolRfcomm);
        if (fd < 0) ThrowNative("creating RFCOMM socket");
        try
        {
            // sockaddr_rc: native-endian family, BDADDR_ANY (six zeros), RFCOMM channel, pad.
            var address = new byte[] { AfBluetooth, 0, 0, 0, 0, 0, 0, 0, RfcommChannel, 0 };
            if (bind(fd, address, (uint)address.Length) < 0) ThrowNative("binding RFCOMM channel 1");
            if (listen(fd, 1) < 0) ThrowNative("listening on RFCOMM channel 1");
            _listenerFd = fd;
            RegisterSdpRecord();

            Console.WriteLine($"[BT] RFCOMM listener active on channel {RfcommChannel}; SPP UUID {Protocol.SppUuid}");
            Console.WriteLine("[BT] Waiting for Android RFCOMM connection...");
            _serverCts = new CancellationTokenSource();
            var token = _serverCts.Token;
            _acceptTask = Task.Run(() => AcceptLoop(fd, token));
        }
        catch
        {
            _serverCts?.Cancel();
            _serverCts?.Dispose();
            _serverCts = null;
            _listenerFd = -1;
            close(fd);
            UnregisterSdpRecord();
            throw;
        }
    }

    private void RegisterSdpRecord()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Native Bluetooth SDP registration is supported only on Linux.");
        if (!NativeLibrary.TryLoad("libbluetooth.so.3", out var library))
            throw new IOException("Bluetooth SDP registration requires Ubuntu's libbluetooth.so.3 (install the bluez package).");
        NativeLibrary.Free(library);

        IntPtr record = IntPtr.Zero, session = IntPtr.Zero;
        IntPtr serviceUuid = IntPtr.Zero, browseUuid = IntPtr.Zero;
        IntPtr serviceList = IntPtr.Zero, browseList = IntPtr.Zero;
        IntPtr protocolAttribute = IntPtr.Zero;
        try
        {
            // BDADDR_ANY source + BDADDR_LOCAL destination selects the local SDP daemon.
            var source = new byte[6];
            var local = new byte[] { 0, 0, 0, 0xff, 0xff, 0xff };
            session = sdp_connect(source, local, 0);
            if (session == IntPtr.Zero) ThrowSdp("connecting to the local BlueZ SDP server");
            record = sdp_record_alloc();
            if (record == IntPtr.Zero) throw new OutOfMemoryException("BlueZ could not allocate an SDP service record.");

            serviceUuid = CreateUuid(0x1101);
            browseUuid = CreateUuid(0x1002);
            serviceList = Append(IntPtr.Zero, serviceUuid);
            browseList = Append(IntPtr.Zero, browseUuid);
            if (sdp_set_uuidseq_attr(record, 0x0001, serviceList) < 0 ||
                sdp_set_uuidseq_attr(record, 0x0005, browseList) < 0)
                ThrowSdp("building SPP service UUID attributes");
            var serviceIdData = sdp_data_alloc(0x19, new ushort[] { 0x1101 });
            if (serviceIdData == IntPtr.Zero || sdp_attr_add(record, 0x0003, serviceIdData) < 0)
            {
                if (serviceIdData != IntPtr.Zero) sdp_data_free(serviceIdData);
                ThrowSdp("setting the SPP service ID");
            }

            var l2capUuidData = sdp_data_alloc(0x19, new ushort[] { 0x0100 });
            var l2capSequence = MakeSequence(l2capUuidData);
            var rfcommUuidData = sdp_data_alloc(0x19, new ushort[] { 0x0003 });
            var rfcommChannelData = sdp_data_alloc(0x08, new byte[] { RfcommChannel });
            var rfcommSequence = MakeSequence(rfcommUuidData, rfcommChannelData);
            var protocolFields = MakeSequence(l2capSequence, rfcommSequence);
            protocolAttribute = sdp_data_alloc(0x35, protocolFields);
            if (protocolAttribute == IntPtr.Zero)
            {
                sdp_data_free(protocolFields);
                throw new OutOfMemoryException("BlueZ could not allocate the SDP protocol attribute.");
            }
            if (sdp_attr_add(record, 0x0004, protocolAttribute) < 0)
                ThrowSdp("building L2CAP/RFCOMM protocol attributes");
            protocolAttribute = IntPtr.Zero; // The record now owns the complete nested data tree.
            sdp_set_info_attr(record, "RemoteGamepad", "", "RemoteGamepad Bluetooth gamepad server");
            if (sdp_record_register(session, record, 0) < 0) ThrowSdp("registering the SPP service record");

            _sdpSession = session;
            _sdpRecord = record;
            session = record = IntPtr.Zero;
            Console.WriteLine("[BT] Native BlueZ SPP SDP record registered on RFCOMM channel 1");
        }
        finally
        {
            FreeList(serviceList); FreeList(browseList);
            if (serviceUuid != IntPtr.Zero) Marshal.FreeHGlobal(serviceUuid);
            if (browseUuid != IntPtr.Zero) Marshal.FreeHGlobal(browseUuid);
            if (protocolAttribute != IntPtr.Zero) sdp_data_free(protocolAttribute);
            if (record != IntPtr.Zero) sdp_record_free(record);
            if (session != IntPtr.Zero) sdp_close(session);
        }
    }

    private static IntPtr CreateUuid(ushort value)
    {
        // uuid_t is 20 bytes on the supported BlueZ ABI: a byte followed by a
        // naturally aligned union containing the 128-bit UUID representation.
        var uuid = Marshal.AllocHGlobal(20);
        if (sdp_uuid16_create(uuid, value) == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(uuid);
            throw new IOException("BlueZ failed to construct an SDP UUID.");
        }
        return uuid;
    }

    private static IntPtr Append(IntPtr list, IntPtr data)
    {
        if (data == IntPtr.Zero) throw new OutOfMemoryException("BlueZ could not allocate SDP list data.");
        var result = sdp_list_append(list, data);
        if (result == IntPtr.Zero) throw new OutOfMemoryException("BlueZ could not allocate an SDP list node.");
        return result;
    }

    private static void FreeList(IntPtr list)
    {
        if (list != IntPtr.Zero) sdp_list_free(list, IntPtr.Zero);
    }

    private static IntPtr MakeSequence(params IntPtr[] fields)
    {
        if (fields.Length == 0 || fields.Any(field => field == IntPtr.Zero))
        {
            foreach (var field in fields) if (field != IntPtr.Zero) sdp_data_free(field);
            throw new OutOfMemoryException("BlueZ could not allocate SDP protocol descriptor data.");
        }
        var sequence = fields[0];
        for (var i = 1; i < fields.Length; i++) sequence = sdp_seq_append(sequence, fields[i]);
        var wrapped = sdp_data_alloc(0x35, sequence);
        if (wrapped == IntPtr.Zero)
        {
            sdp_data_free(sequence);
            throw new OutOfMemoryException("BlueZ could not allocate an SDP sequence.");
        }
        return wrapped;
    }

    private static void ThrowSdp(string operation)
    {
        var errno = Marshal.GetLastPInvokeError();
        throw new IOException($"Bluetooth SDP error while {operation} (native errno {errno}: {new System.ComponentModel.Win32Exception(errno).Message}).");
    }

    private void UnregisterSdpRecord()
    {
        var record = Interlocked.Exchange(ref _sdpRecord, IntPtr.Zero);
        var session = Interlocked.Exchange(ref _sdpSession, IntPtr.Zero);
        if (record != IntPtr.Zero && session != IntPtr.Zero)
        {
            if (sdp_record_unregister(session, record) < 0)
                Console.Error.WriteLine($"[BT] Could not unregister native SDP record: {new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            sdp_record_free(record);
        }
        if (session != IntPtr.Zero) sdp_close(session);
    }

    private void AcceptLoop(int listenerFd, CancellationToken token)
    {
        var pollFd = new PollFd { Fd = listenerFd, Events = PollIn };
        while (!token.IsCancellationRequested)
        {
            try
            {
                var ready = poll(ref pollFd, 1, 300);
                if (ready == 0) continue;
                if (ready < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == 4) continue; // EINTR
                    throw new SocketException(errno);
                }
                var clientFd = accept4(listenerFd, IntPtr.Zero, IntPtr.Zero, SockCloexec);
                if (clientFd < 0)
                {
                    if (token.IsCancellationRequested) break;
                    throw new SocketException(Marshal.GetLastPInvokeError());
                }
                if (token.IsCancellationRequested) { close(clientFd); break; }
                HandleClient(clientFd, token);
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[BT] Accept/session error: {ex.Message}; retrying");
                    Error?.Invoke(this, $"Accept/session error: {ex.Message}");
                    try { Task.Delay(250, token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { }
                }
            }
        }
    }

    private void HandleClient(int clientFd, CancellationToken token)
    {
        _clientFd = clientFd;
        var stream = new RfcommStream(clientFd);
        _stream = stream;
        try
        {
            Console.WriteLine("[BT] Android RFCOMM client connected");
            Connected?.Invoke(this, EventArgs.Empty);
            ReadLoop(stream, token);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _clientFd, -1, clientFd) == clientFd)
            {
                _stream = null;
                stream.Dispose();
                Disconnected?.Invoke(this, EventArgs.Empty);
                Console.WriteLine("[BT] Client disconnected; waiting for a new connection");
            }
            else stream.Dispose();
        }
    }

    private void ReadLoop(RfcommStream stream, CancellationToken token)
    {
        while (!token.IsCancellationRequested && IsConnected)
        {
            var message = Protocol.ReadMessage(stream, token);
            if (message is null) return;
            if (!message.StartsWith(Protocol.MSG_INPUT, StringComparison.Ordinal))
                Console.WriteLine($"[BT] Received {message}");
            MessageReceived?.Invoke(this, message);
            switch (message)
            {
                case Protocol.MSG_HELLO: Send(Protocol.MSG_HELLO_ACK); break;
                case Protocol.MSG_PING: Send(Protocol.MSG_PONG); break;
                case Protocol.MSG_DISCONNECT: return;
                default:
                    if (!message.StartsWith(Protocol.MSG_INPUT + "|", StringComparison.Ordinal)) continue;
                    var state = InputParser.Parse(message);
                    if (state is { } parsed) InputReceived?.Invoke(this, parsed);
                    break;
            }
        }
    }

    public bool Send(string message)
    {
        lock (_sendLock)
        {
            try
            {
                var stream = _stream;
                if (stream is null || !IsConnected) return false;
                Protocol.WriteMessage(stream, message);
                return true;
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Send error: {ex.Message}");
                return false;
            }
        }
    }

    public void DisconnectClient()
    {
        var fd = Interlocked.Exchange(ref _clientFd, -1);
        if (fd < 0) return;
        var stream = Interlocked.Exchange(ref _stream, null);
        shutdown(fd, ShutReadWrite);
        stream?.Dispose();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _serverCts?.Cancel();
        DisconnectClient();
        try { _acceptTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException ex) { Console.Error.WriteLine($"[BT] Listener shutdown error: {ex.GetBaseException().Message}"); }
        _acceptTask = null;
        var listenerFd = Interlocked.Exchange(ref _listenerFd, -1);
        if (listenerFd >= 0) close(listenerFd);
        _serverCts?.Dispose();
        _serverCts = null;
        UnregisterSdpRecord();
        Console.WriteLine("[BT] RFCOMM listener stopped");
    }

    private static void ThrowNative(string operation)
    {
        var socketException = new SocketException(Marshal.GetLastPInvokeError());
        throw new IOException($"Bluetooth {operation} failed: {socketException.Message}", socketException);
    }

    [DllImport("libbluetooth.so.3", SetLastError = true, EntryPoint = "sdp_connect")] private static extern IntPtr sdp_connect(byte[] source, byte[] destination, uint flags);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern int sdp_close(IntPtr session);
    [DllImport("libbluetooth.so.3", SetLastError = true, EntryPoint = "sdp_record_alloc")] private static extern IntPtr sdp_record_alloc();
    [DllImport("libbluetooth.so.3", SetLastError = true, EntryPoint = "sdp_record_free")] private static extern void sdp_record_free(IntPtr record);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern int sdp_record_register(IntPtr session, IntPtr record, byte flags);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern int sdp_record_unregister(IntPtr session, IntPtr record);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern IntPtr sdp_uuid16_create(IntPtr uuid, ushort value);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern IntPtr sdp_list_append(IntPtr list, IntPtr data);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern void sdp_list_free(IntPtr list, IntPtr freeFunction);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern IntPtr sdp_data_alloc(byte dtd, byte[] value);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern void sdp_data_free(IntPtr data);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern IntPtr sdp_seq_append(IntPtr sequence, IntPtr data);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern int sdp_attr_add(IntPtr record, ushort attribute, IntPtr data);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern int sdp_set_uuidseq_attr(IntPtr record, ushort attribute, IntPtr sequence);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern void sdp_set_info_attr(IntPtr record, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string provider, [MarshalAs(UnmanagedType.LPUTF8Str)] string description);

    [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events; public short Revents; }
    [DllImport("libc", SetLastError = true, EntryPoint = "socket")] private static extern int bluetoothSocket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] private static extern int bind(int fd, byte[] address, uint addressLength);
    [DllImport("libc", SetLastError = true)] private static extern int listen(int fd, int backlog);
    [DllImport("libc", SetLastError = true)] private static extern int accept4(int fd, IntPtr address, IntPtr addressLength, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollFd fds, nuint count, int timeoutMilliseconds);
    [DllImport("libc", SetLastError = true)] private static extern int shutdown(int fd, int how);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern long recv(int fd, IntPtr buffer, nuint length, int flags);
    [DllImport("libc", SetLastError = true)] private static extern long send(int fd, IntPtr buffer, nuint length, int flags);

    private sealed class RfcommStream(int fd) : Stream
    {
        private int _fd = fd;
        public override bool CanRead => _fd >= 0;
        public override bool CanSeek => false;
        public override bool CanWrite => _fd >= 0;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if ((uint)offset > buffer.Length || (uint)count > buffer.Length - offset) throw new ArgumentOutOfRangeException();
            var handle = Volatile.Read(ref _fd);
            if (handle < 0) throw new ObjectDisposedException(nameof(RfcommStream));
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                while (true)
                {
                    var received = recv(handle, IntPtr.Add(pin.AddrOfPinnedObject(), offset), (nuint)count, 0);
                    if (received >= 0) return checked((int)received);
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == 4) continue; // EINTR
                    throw new SocketException(errno);
                }
            }
            finally { pin.Free(); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if ((uint)offset > buffer.Length || (uint)count > buffer.Length - offset) throw new ArgumentOutOfRangeException();
            var fd = Volatile.Read(ref _fd);
            if (fd < 0) throw new ObjectDisposedException(nameof(RfcommStream));
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var written = 0;
                while (written < count)
                {
                    var pointer = IntPtr.Add(pin.AddrOfPinnedObject(), offset + written);
                    var result = send(fd, pointer, (nuint)(count - written), MsgNoSignal);
                    if (result < 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        if (errno == 4) continue; // EINTR
                        throw new SocketException(errno);
                    }
                    if (result == 0) throw new IOException("RFCOMM socket closed during write.");
                    written += checked((int)result);
                }
            }
            finally { pin.Free(); }
        }

        protected override void Dispose(bool disposing)
        {
            var fd = Interlocked.Exchange(ref _fd, -1);
            if (fd >= 0) { shutdown(fd, ShutReadWrite); close(fd); }
            base.Dispose(disposing);
        }
    }

    public void Dispose() => Stop();
}
