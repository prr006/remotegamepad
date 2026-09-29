namespace RemoteGamepadServer;

class Program
{
    static async Task Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        using var controller = new VirtualController();
        try
        {
            controller.Initialize();
            controller.TraceInput = args.Contains("--trace-input", StringComparer.Ordinal);
            if (args.Contains("--uinput-self-test", StringComparer.Ordinal))
            {
                controller.TraceInput = true;
                controller.Update(RunInputParserSelfTest());
                Thread.Sleep(750);
                controller.RunMappingSelfTest(Console.WriteLine);
                return;
            }

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

    private static InputParser.ControllerState RunInputParserSelfTest()
    {
        const string packet = "{\"lx\":0.5,\"ly\":-0.25,\"rx\":-1,\"ry\":1,\"lt\":0.25,\"rt\":0.75,\"a\":true,\"b\":false,\"x\":true,\"y\":false,\"lb\":true,\"rb\":false,\"dUp\":true,\"dDown\":false,\"dLeft\":true,\"dRight\":false,\"start\":true,\"select\":false,\"l3\":true,\"r3\":false}";
        var actual = InputParser.Parse(packet);
        var expected = new InputParser.ControllerState(
            0.5f, -0.25f, -1f, 1f, 0.25f, 0.75f,
            true, false, true, false, true, false,
            true, false, true, false, true, false, true, false);
        if (actual is null || actual.Value != expected)
            throw new InvalidOperationException($"InputParser self-test failed: {actual}");
        Console.WriteLine("[SELF-TEST] Raw Android JSON -> ControllerState: all 20 fields match");
        Console.WriteLine($"[SELF-TEST] Parsed state: {actual.Value}");
        return actual.Value;
    }
}
