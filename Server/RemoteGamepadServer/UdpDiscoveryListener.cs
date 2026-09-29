namespace RemoteGamepadServer;
using System.Net;
using System.Net.Sockets;
using System.Text;

public sealed class UdpDiscoveryListener : IAsyncDisposable
{
    public const int Port = 26761;
    private UdpClient? _udp;
    public async Task RunAsync(CancellationToken token)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        Console.WriteLine($"[UDP] Discovery listening on 0.0.0.0:{Port}");
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try { request = await _udp.ReceiveAsync(token); } catch (OperationCanceledException) { break; }
            if (Encoding.UTF8.GetString(request.Buffer).Trim() == "REMOTE_GAMEPAD_DISCOVER")
            {
                var response = Encoding.UTF8.GetBytes($"REMOTE_GAMEPAD_SERVER|RemoteGamepad Linux|{UdpInputServer.Port}");
                await _udp.SendAsync(response, request.RemoteEndPoint, token);
                Console.WriteLine($"[UDP] Discovery response sent to {request.RemoteEndPoint}");
            }
        }
    }
    public ValueTask DisposeAsync() { _udp?.Dispose(); return ValueTask.CompletedTask; }
}
