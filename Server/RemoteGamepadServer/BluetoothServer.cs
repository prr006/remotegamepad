namespace RemoteGamepadServer;

using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Isolated Bluetooth Classic transport layer for the Windows server.
///
/// Responsibilities:
/// - Advertise an RFCOMM service (SPP)
/// - Accept incoming Bluetooth connections from Android
/// - Send/receive framed messages using <see cref="Protocol"/>
/// - Notify callers via events
/// - Clean up resources on disconnect
/// </summary>
public class BluetoothServer : IDisposable
{
    private BluetoothListener? _listener;
    private BluetoothClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    public bool IsConnected => _client?.Connected ?? false;

    public event EventHandler<string>? MessageReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? Error;

    /// <summary>
    /// Fired when an INPUT message is received and successfully parsed.
    /// </summary>
    public event EventHandler<InputParser.ControllerState>? InputReceived;

    /// <summary>
    /// Start listening for incoming Bluetooth connections.
    /// </summary>
    public void Start()
    {
        if (_listener != null)
            throw new InvalidOperationException("Server already started");

        _listener = new BluetoothListener(Protocol.SppUuid)
        {
            ServiceName = Protocol.ServiceName
        };
        _listener.Start();

        Console.WriteLine($"[SERVER] Listening on {BluetoothRadio.Default.LocalAddress}");
        Console.WriteLine($"[SERVER] Service UUID: {Protocol.SppUuid}");
        Console.WriteLine($"[SERVER] Service Name: {Protocol.ServiceName}");
        Console.WriteLine("[SERVER] Waiting for Android connection...");

        _cts = new CancellationTokenSource();

        // Accept loop on a background thread
        Task.Run(() => AcceptLoop(_cts.Token));
    }

    private void AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = _listener!.AcceptBluetoothClient();
                if (ct.IsCancellationRequested)
                {
                    client.Close();
                    return;
                }

                HandleClient(client);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Error?.Invoke(this, $"Accept error: {ex.Message}");
                Console.WriteLine($"[ERROR] Accept error: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private void HandleClient(BluetoothClient client)
    {
        // Close previous client if any (single-client policy)
        if (_client != null)
        {
            Console.WriteLine("[SERVER] New connection — closing previous client");
            CleanupClient();
        }

        _client = client;
        _stream = client.GetStream();

        Console.WriteLine($"[SERVER] Client connected: {client.RemoteMachineName}");
        Connected?.Invoke(this, EventArgs.Empty);

        _cts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_cts.Token));
    }

    private void ReadLoop(CancellationToken ct)
    {
        var stream = _stream;
        if (stream == null) return;

        try
        {
            while (!ct.IsCancellationRequested && _client?.Connected == true)
            {
                var msg = Protocol.ReadMessage(stream, ct);
                if (msg == null)
                {
                    Console.WriteLine("[SERVER] Client closed stream cleanly");
                    break;
                }

                // Only log non-INPUT messages to avoid console spam
                if (!msg.StartsWith(Protocol.MSG_INPUT, StringComparison.Ordinal))
                {
                    Console.WriteLine($"[RECV] {msg}");
                }
                else
                {
                    // Throttled logging for input
                    if (Environment.TickCount % 5000 < 50)
                    {
                        Console.WriteLine("[RECV] INPUT (throttled)");
                    }
                }

                MessageReceived?.Invoke(this, msg);

                // Auto-respond to protocol messages + parse INPUT
                switch (msg)
                {
                    case Protocol.MSG_HELLO:
                        Send(Protocol.MSG_HELLO_ACK);
                        Console.WriteLine("[SEND] HELLO_ACK");
                        break;
                    case Protocol.MSG_PING:
                        Send(Protocol.MSG_PONG);
                        Console.WriteLine("[SEND] PONG");
                        break;
                    case Protocol.MSG_DISCONNECT:
                        Console.WriteLine("[SERVER] Received DISCONNECT — closing connection");
                        break;
                    default:
                        // Handle INPUT messages
                        if (msg.StartsWith(Protocol.MSG_INPUT, StringComparison.Ordinal))
                        {
                            var state = InputParser.Parse(msg);
                            if (state.HasValue)
                            {
                                InputReceived?.Invoke(this, state.Value);
                            }
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[SERVER] Read loop cancelled");
        }
        catch (IOException ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[ERROR] Read error: {ex.Message}");
                Error?.Invoke(this, $"Read error: {ex.Message}");
            }
        }
        finally
        {
            if (_client != null && _client.Connected)
            {
                CleanupClient();
                Disconnected?.Invoke(this, EventArgs.Empty);
                Console.WriteLine("[SERVER] Client disconnected. Waiting for new connection...");
            }
        }
    }

    /// <summary>
    /// Send a framed message to the connected client.
    /// </summary>
    public bool Send(string message)
    {
        try
        {
            var stream = _stream;
            if (stream == null || !IsConnected) return false;
            Protocol.WriteMessage(stream, message);
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Send error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gracefully disconnect the current client.
    /// </summary>
    public void DisconnectClient()
    {
        _cts?.Cancel();
        _readTask?.Wait(TimeSpan.FromSeconds(2));
        CleanupClient();
    }

    /// <summary>
    /// Stop the server entirely.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _readTask?.Wait(TimeSpan.FromSeconds(2));
        CleanupClient();
        _listener?.Stop();
        _listener = null;
        Console.WriteLine("[SERVER] Stopped");
    }

    private void CleanupClient()
    {
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
