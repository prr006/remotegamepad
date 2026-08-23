namespace RemoteGamepadServer;

using InTheHand.Net.Bluetooth;
using System;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("============================================");
        Console.WriteLine("  RemoteGamepad Server — Milestone 2");
        Console.WriteLine("  Bluetooth Classic (RFCOMM/SPP)");
        Console.WriteLine("  Virtual Xbox 360 Controller (ViGEm)");
        Console.WriteLine("============================================");
        Console.WriteLine();

        // Verify Bluetooth is available
        var radio = BluetoothRadio.Default;
        if (radio == null)
        {
            Console.WriteLine("[FATAL] No Bluetooth radio found. Ensure Bluetooth is enabled.");
            Environment.Exit(1);
        }

        Console.WriteLine($"Bluetooth Radio: {radio.Name}");
        Console.WriteLine($"Radio Mode: {radio.Mode}");
        Console.WriteLine($"Local Address: {radio.LocalAddress}");
        Console.WriteLine();

        // Initialize virtual controller
        using var virtualController = new VirtualController();
        virtualController.Initialize();

        using var server = new BluetoothServer();

        server.Connected += (_, _) =>
        {
            Console.WriteLine("[EVENT] Connected");
        };

        server.Disconnected += (_, _) =>
        {
            Console.WriteLine("[EVENT] Disconnected — resetting virtual controller");
            virtualController.Reset();
        };

        server.MessageReceived += (_, msg) =>
        {
            // Non-INPUT messages logged by BluetoothServer
        };

        server.InputReceived += (_, state) =>
        {
            virtualController.Update(state);
        };

        server.Error += (_, err) => Console.WriteLine($"[EVENT] Error: {err}");

        server.Start();

        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  'q' + Enter  — quit server");
        Console.WriteLine("  'd' + Enter  — disconnect current client");
        Console.WriteLine();

        var cts = new CancellationTokenSource();
        _ = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line == null) continue;
                switch (line.Trim().ToLower())
                {
                    case "q":
                    case "quit":
                    case "exit":
                        cts.Cancel();
                        break;
                    case "d":
                    case "disconnect":
                        server.DisconnectClient();
                        break;
                    default:
                        if (!string.IsNullOrWhiteSpace(line) && server.IsConnected)
                        {
                            server.Send(line);
                            Console.WriteLine($"[SEND] {line}");
                        }
                        break;
                }
            }
        });

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        Console.WriteLine();
        Console.WriteLine("[SERVER] Shutting down gracefully...");
        virtualController.Reset();
        server.Stop();
        Console.WriteLine("[SERVER] Goodbye.");
    }
}
