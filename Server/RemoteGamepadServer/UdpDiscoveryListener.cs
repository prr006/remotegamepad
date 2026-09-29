namespace RemoteGamepadServer;

using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>Android LAN broadcast discovery listener, IPv4 UDP port 26761.</summary>
public sealed class UdpDiscoveryListener : IAsyncDisposable
{
    public const int Port = 26761;
    private UdpClient? _udp;

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
                _udp = udp;
                Console.WriteLine($"[UDP] Discovery listening on 0.0.0.0:{Port}");

                while (!token.IsCancellationRequested)
                {
                    var request = await udp.ReceiveAsync(token);
                    if (!request.Buffer.AsSpan().SequenceEqual("REMOTE_GAMEPAD_DISCOVER"u8)) continue;

                    var response = Encoding.UTF8.GetBytes(
                        $"REMOTE_GAMEPAD_SERVER|RemoteGamepad Linux|{UdpInputServer.Port}");
                    await udp.SendAsync(response, request.RemoteEndPoint, token);
                    Console.WriteLine($"[UDP] Discovery response sent to {request.RemoteEndPoint}");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"[UDP] Discovery listener error: {ex.Message}; retrying in 2 seconds");
                try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UDP] Discovery error: {ex.Message}; listener will restart");
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
