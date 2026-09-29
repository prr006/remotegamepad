namespace RemoteGamepadServer;
using System.Net;
using System.Net.Sockets;
using System.Text;

public sealed class UdpDiscoveryListener : IAsyncDisposable
{
    public const int Port = 26760;
    private UdpClient? _udp;
    public async Task RunAsync(CancellationToken token)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        Console.WriteLine($"[UDP] Discovery listening on 0.0.0.0:{Port}");
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try { request = await _udp.ReceiveAsync(token); } catch (OperationCanceledException) { break; }
            if (Encoding.UTF8.GetString(request.Buffer).Trim().Equals("DISCOVER", StringComparison.OrdinalIgnoreCase))
            {
                var response = Encoding.UTF8.GetBytes($"REMOTEGAMEPAD|{UdpInputServer.Port}");
                await _udp.SendAsync(response, request.RemoteEndPoint, token);
            }
        }
    }
    public ValueTask DisposeAsync() { _udp?.Dispose(); return ValueTask.CompletedTask; }
}
