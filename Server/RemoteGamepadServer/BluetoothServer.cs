namespace RemoteGamepadServer;

using InTheHand.Net.Bluetooth;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

/// <summary>BlueZ RFCOMM/SPP server with native RFCOMM sockets and BlueZ SDP tooling.</summary>
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
    private string? _sdpRecordHandle;

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
        // Request the record handle up front. This avoids `sdptool browse local`,
        // which is a separate SDP query and can fail under different inherited
        // credentials even when registration itself is permitted.
        var handle = $"0x{RandomNumberGenerator.GetInt32(0x00010000, 0x7fffffff):X8}";
        var output = RunBluezTool("sdptool", "add", $"--handle={handle}", "--channel=1", "SP");
        _sdpRecordHandle = handle;
        Console.WriteLine($"[BT] SPP SDP record registered (handle {handle}): {output.Trim()}");
    }

    private static string RunBluezTool(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {tool}; install the Ubuntu bluez package.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(8000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"{tool} {string.Join(' ', args)} timed out.");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new IOException(
                $"{tool} {string.Join(' ', args)} failed ({process.ExitCode}): {stderr.Trim()} {stdout.Trim()} " +
                $"Server process credentials: {DescribeProcessCredentials()}. Child processes inherit the server's supplementary groups; " +
                "if bluetooth group membership was changed, fully restart the server process/session and compare its Groups line in /proc/<pid>/status with `id -G`."
            );
        return stdout;
    }

    private static string DescribeProcessCredentials()
    {
        try
        {
            var wanted = File.ReadLines("/proc/self/status")
                .Where(line => line.StartsWith("Uid:", StringComparison.Ordinal) ||
                               line.StartsWith("Gid:", StringComparison.Ordinal) ||
                               line.StartsWith("Groups:", StringComparison.Ordinal));
            return $"user={Environment.UserName}; {string.Join("; ", wanted)}";
        }
        catch (Exception ex) { return $"user={Environment.UserName}; /proc credentials unavailable: {ex.Message}"; }
    }

    private void UnregisterSdpRecord()
    {
        var handle = _sdpRecordHandle;
        _sdpRecordHandle = null;
        if (handle is null) return;
        try { RunBluezTool("sdptool", "del", handle); }
        catch (Exception ex) { Console.Error.WriteLine($"[BT] Could not remove SDP record {handle}: {ex.Message}"); }
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
