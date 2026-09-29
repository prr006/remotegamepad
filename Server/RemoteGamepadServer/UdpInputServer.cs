namespace RemoteGamepadServer;

using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>Receives Android Wi-Fi controller JSON datagrams over IPv4.</summary>
public sealed class UdpInputServer : IAsyncDisposable
{
    public const int Port = 26760;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Action<InputParser.ControllerState> _applyState;
    private UdpClient? _udp;
    private long _lastActivityLog;

    public UdpInputServer(Action<InputParser.ControllerState> applyState) => _applyState = applyState;

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
                _udp = udp;
                Console.WriteLine($"[UDP] Input listening on 0.0.0.0:{Port}");

                while (!token.IsCancellationRequested)
                {
                    var packet = await udp.ReceiveAsync(token);
                    if (packet.Buffer.Length > 4096)
                    {
                        Console.Error.WriteLine($"[UDP] Ignoring oversized input datagram ({packet.Buffer.Length} bytes)");
                        continue;
                    }

                    string message;
                    try { message = StrictUtf8.GetString(packet.Buffer); }
                    catch (DecoderFallbackException)
                    {
                        Console.Error.WriteLine($"[UDP] Ignoring invalid UTF-8 datagram from {packet.RemoteEndPoint}");
                        continue;
                    }

                    var state = InputParser.Parse(message);
                    if (state is not { } parsed) continue;
                    _applyState(parsed);

                    var now = Environment.TickCount64;
                    if (now - _lastActivityLog >= 5000)
                    {
                        _lastActivityLog = now;
                        Console.WriteLine($"[UDP] Input received from {packet.RemoteEndPoint}");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"[UDP] Input listener error: {ex.Message}; retrying in 2 seconds");
                try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UDP] Input processing error: {ex.Message}; listener will restart");
                try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
            finally { _udp = null; }
        }
    }

    public ValueTask DisposeAsync()
    {
        _udp?.Dispose();
        return ValueTask.CompletedTask;
    }
}
