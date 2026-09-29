namespace RemoteGamepadServer;

class Program
{
    static async Task Main()
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        using var controller = new VirtualController();
        try
        {
            controller.Initialize();
            var inputGate = new object();
            string? lastInputTransport = null;
            void ApplyState(InputParser.ControllerState state, string transport)
            {
                lock (inputGate)
                {
                    controller.Update(state);
                    lastInputTransport = transport;
                }
            }

            await using var discovery = new UdpDiscoveryListener();
            await using var input = new UdpInputServer(state => ApplyState(state, "Wi-Fi"));
            var tasks = new[] { discovery.RunAsync(cancellation.Token), input.RunAsync(cancellation.Token) };
            using var bluetooth = new BluetoothServer();
            bluetooth.InputReceived += (_, state) => ApplyState(state, "Bluetooth");
            bluetooth.Disconnected += (_, _) =>
            {
                lock (inputGate)
                {
                    if (lastInputTransport == "Bluetooth")
                    {
                        controller.Reset();
                        lastInputTransport = null;
                    }
                }
            };
            bluetooth.Error += (_, message) => Console.Error.WriteLine($"[BT] {message}");
            try { bluetooth.Start(); }
            catch (Exception ex) { Console.Error.WriteLine($"[BT] Bluetooth unavailable; Wi-Fi remains active: {ex.Message}"); }
            Console.WriteLine("RemoteGamepad Linux server (Ctrl+C to stop)");
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
            Console.CancelKeyPress -= cancelHandler;
            controller.Reset();
        }
    }
}
