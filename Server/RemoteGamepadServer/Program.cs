namespace RemoteGamepadServer;

class Program
{
    static async Task Main()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown.Cancel(); };
        using var controller = new VirtualController();
        controller.Initialize();
        await using var discovery = new UdpDiscoveryListener();
        await using var input = new UdpInputServer(controller);
        using var cancellation = Shutdown.Source;
        Console.WriteLine("RemoteGamepad Linux server (Ctrl+C to stop)");
        var tasks = new[] { discovery.RunAsync(cancellation.Token), input.RunAsync(cancellation.Token) };
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        controller.Reset();
    }
    private static class Shutdown
    {
        public static readonly CancellationTokenSource Source = new();
        public static void Cancel() { try { Source.Cancel(); } catch (ObjectDisposedException) { } }
    }
}
