namespace RemoteGamepadServer;
using System.Net;
using System.Net.Sockets;
using System.Text;

public sealed class UdpInputServer : IAsyncDisposable
{
    public const int Port = 26761;
    private readonly VirtualController _controller;
    private UdpClient? _udp;
    public UdpInputServer(VirtualController controller) => _controller = controller;
    public async Task RunAsync(CancellationToken token)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        Console.WriteLine($"[UDP] Input listening on 0.0.0.0:{Port}");
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(token); } catch (OperationCanceledException) { break; }
            var message = Encoding.UTF8.GetString(packet.Buffer);
            var state = InputParser.Parse(message);
            if (state is { } value) _controller.Update(value);
        }
    }
    public ValueTask DisposeAsync() { _udp?.Dispose(); return ValueTask.CompletedTask; }
}
