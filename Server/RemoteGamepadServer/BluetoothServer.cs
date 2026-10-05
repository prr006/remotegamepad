namespace RemoteGamepadServer;

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
    private readonly IBluetoothDiscoverability _discoverability;
    private readonly Action<string> _log;
    private int _listenerFd = -1, _clientFd = -1;
    private RfcommStream? _stream;
    private CancellationTokenSource? _serverCts;
    private Task? _acceptTask;
    private IntPtr _sdpSession;
    private IntPtr _sdpRecord;

    /// <param name="discoverability">
    /// Provider for the optional "make the adapter discoverable" step; defaults to
    /// <see cref="InTheHandDiscoverability"/>. Tests inject a provider that simulates
    /// <c>org.bluez.Error.Failed</c>.
    /// </param>
    /// <param name="discoverabilityRequired">
    /// <c>null</c> (default) keeps the platform policy: best-effort on Linux, where BlueZ often
    /// rejects the request while RFCOMM/SDP work fine, and required everywhere else so Windows
    /// behaviour is unchanged.
    /// </param>
    /// <param name="log">Log sink; defaults to <see cref="Console.WriteLine(string)"/>.</param>
    public BluetoothServer(
        IBluetoothDiscoverability? discoverability = null,
        bool? discoverabilityRequired = null,
        Action<string>? log = null)
    {
        _discoverability = discoverability ?? new InTheHandDiscoverability();
        DiscoverabilityRequired = discoverabilityRequired ?? !OperatingSystem.IsLinux();
        _log = log ?? Console.WriteLine;
    }

    /// <summary>When false (Linux default) a failed discoverability attempt is logged and ignored.</summary>
    public bool DiscoverabilityRequired { get; }

    public bool IsConnected => Volatile.Read(ref _clientFd) >= 0;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? Error;
    public event EventHandler<InputParser.ControllerState>? InputReceived;

    public void Start()
    {
        if (_listenerFd >= 0) throw new InvalidOperationException("Bluetooth server is already started.");

        // Discoverability is advisory: on Linux a BlueZ failure (org.bluez.Error.Failed) is
        // logged and startup continues straight into RFCOMM bind/listen and SDP registration.
        BluetoothDiscoverability.Apply(_discoverability, DiscoverabilityRequired, _log);

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

            _log($"[BT] RFCOMM listener active on channel {RfcommChannel}; SPP UUID {Protocol.SppUuid}");
            _log("[BT] Waiting for Android RFCOMM connection...");
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
        IntPtr l2capUuid = IntPtr.Zero, rfcommUuid = IntPtr.Zero, channelData = IntPtr.Zero;
        IntPtr l2capProtocol = IntPtr.Zero, rfcommProtocol = IntPtr.Zero;
        IntPtr accessSequence = IntPtr.Zero, accessProtocols = IntPtr.Zero;
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
            if (SetServiceClasses(record, serviceList) < 0 || SetBrowseGroups(record, browseList) < 0)
                ThrowSdp("building SPP service UUID attributes");
            sdp_set_service_id(record, Marshal.PtrToStructure<SdpUuid>(serviceUuid));

            // Mirror BlueZ sdptool's native access-protocol list shape: outer
            // access list -> sequence of protocol descriptors -> each descriptor.
            l2capUuid = CreateUuid(0x0100);
            l2capProtocol = Append(IntPtr.Zero, l2capUuid);
            accessSequence = Append(IntPtr.Zero, l2capProtocol);
            rfcommUuid = CreateUuid(0x0003);
            rfcommProtocol = Append(IntPtr.Zero, rfcommUuid);
            channelData = sdp_data_alloc(0x08, new byte[] { RfcommChannel });
            if (channelData == IntPtr.Zero) throw new OutOfMemoryException("BlueZ could not allocate the RFCOMM channel descriptor.");
            rfcommProtocol = Append(rfcommProtocol, channelData);
            accessSequence = Append(accessSequence, rfcommProtocol);
            accessProtocols = Append(IntPtr.Zero, accessSequence);
            if (sdp_set_access_protos(record, accessProtocols) < 0)
                ThrowSdp("building L2CAP/RFCOMM protocol attributes");
            sdp_set_info_attr(record, "RemoteGamepad", "", "RemoteGamepad Bluetooth gamepad server");
            if (sdp_record_register(session, record, 0) < 0) ThrowSdp("registering the SPP service record");

            _sdpSession = session;
            _sdpRecord = record;
            session = record = IntPtr.Zero;
            _log("[BT] Native BlueZ SPP SDP record registered on RFCOMM channel 1");
        }
        finally
        {
            FreeList(serviceList); FreeList(browseList);
            FreeList(l2capProtocol); FreeList(rfcommProtocol);
            FreeList(accessSequence); FreeList(accessProtocols);
            if (channelData != IntPtr.Zero) sdp_data_free(channelData);
            if (serviceUuid != IntPtr.Zero) Marshal.FreeHGlobal(serviceUuid);
            if (browseUuid != IntPtr.Zero) Marshal.FreeHGlobal(browseUuid);
            if (l2capUuid != IntPtr.Zero) Marshal.FreeHGlobal(l2capUuid);
            if (rfcommUuid != IntPtr.Zero) Marshal.FreeHGlobal(rfcommUuid);
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

    // BlueZ declares service-class and browse-group setters as header-inline
    // wrappers, so call their exported implementation with the official IDs.
    private static int SetServiceClasses(IntPtr record, IntPtr sequence) => sdp_set_uuidseq_attr(record, 0x0001, sequence);
    private static int SetBrowseGroups(IntPtr record, IntPtr sequence) => sdp_set_uuidseq_attr(record, 0x0005, sequence);

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
            _log("[BT] Android RFCOMM client connected");
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
                _log("[BT] Client disconnected; waiting for a new connection");
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
                _log($"[BT] Received {message}");
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
        _log("[BT] RFCOMM listener stopped");
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
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern void sdp_set_service_id(IntPtr record, SdpUuid uuid);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern IntPtr sdp_uuid16_create(IntPtr uuid, ushort value);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern IntPtr sdp_list_append(IntPtr list, IntPtr data);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern void sdp_list_free(IntPtr list, IntPtr freeFunction);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern IntPtr sdp_data_alloc(byte dtd, byte[] value);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern void sdp_data_free(IntPtr data);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern int sdp_set_uuidseq_attr(IntPtr record, ushort attribute, IntPtr sequence);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern int sdp_set_access_protos(IntPtr record, IntPtr protocols);
    [DllImport("libbluetooth.so.3", SetLastError = true)] private static extern void sdp_set_info_attr(IntPtr record, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string provider, [MarshalAs(UnmanagedType.LPUTF8Str)] string description);

    [StructLayout(LayoutKind.Explicit, Size = 20)] private struct SdpUuid
    {
        [FieldOffset(0)] public byte Type;
        [FieldOffset(4)] public ushort Uuid16;
        [FieldOffset(4)] public uint Uuid32;
        [FieldOffset(4)] public ulong Uuid128Low;
        [FieldOffset(12)] public ulong Uuid128High;
    }

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
