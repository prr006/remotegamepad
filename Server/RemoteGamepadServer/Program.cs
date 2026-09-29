namespace RemoteGamepadServer;

class Program
{
    static async Task Main()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        using var controller = new VirtualController();
        try
        {
            controller.Initialize();
            await using var discovery = new UdpDiscoveryListener();
            await using var input = new UdpInputServer(controller);
            using var bluetooth = new BluetoothServer();
            bluetooth.InputReceived += (_, state) => controller.Update(state);
            bluetooth.Disconnected += (_, _) => controller.Reset();
            bluetooth.Error += (_, message) => Console.Error.WriteLine($"[BT] {message}");
            try { bluetooth.Start(); }
            catch (Exception ex) { Console.Error.WriteLine($"[BT] Bluetooth unavailable; UDP remains active: {ex.Message}"); }
            Console.WriteLine("RemoteGamepad Linux server (Ctrl+C to stop)");
            var tasks = new[] { discovery.RunAsync(cancellation.Token), input.RunAsync(cancellation.Token) };
            foreach (var task in tasks)
                _ = task.ContinueWith(_ => cancellation.Cancel(), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] Server startup/runtime failed: {ex.Message}");
            throw;
        }
        finally
        {
            cancellation.Cancel();
            controller.Reset();
        }
    }
}
