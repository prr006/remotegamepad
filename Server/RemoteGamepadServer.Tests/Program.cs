namespace RemoteGamepadServer.Tests;

/// <summary>
/// Regression tests for the Linux Bluetooth startup path.
///
/// The bug they pin down: <c>BluetoothServer.Start()</c> used to assign
/// <c>BluetoothRadio.Default.Mode = RadioMode.Discoverable</c> unconditionally, so on
/// Fedora 44 / BlueZ 5.87 an <c>org.bluez.Error.Failed</c> aborted Bluetooth startup
/// before the native RFCOMM socket and the SDP record were ever created.
///
/// Run with:  dotnet run --project Server/RemoteGamepadServer.Tests -c Release
/// (also executed by ./scripts/test-linux.sh)
/// </summary>
internal static class TestProgram
{
    private static int Main()
    {
        Console.WriteLine("RemoteGamepad server tests");

        Check.Section("Discoverability policy (BlueZ failure must not be fatal on Linux)");

        Check.Run("BlueZ failure is logged, not thrown", () =>
        {
            var log = new LogSink();
            var provider = FakeDiscoverability.BluezFailed();
            var result = DiscoverabilityResult.Success;

            Check.DoesNotThrow(
                () => result = BluetoothDiscoverability.Apply(provider, required: false, log.Write),
                "a BlueZ error must never abort Bluetooth startup on Linux");

            Check.False(result.Succeeded, "the attempt did fail");
            Check.False(result.Skipped, "the attempt was made");
            Check.Equal(1, provider.Attempts, "discoverability is attempted exactly once");
            Check.Contains(log.Lines,
                "[BT] Discoverable mode could not be enabled through InTheHand/BlueZ: org.bluez.Error.Failed: Failed");
            Check.Contains(log.Lines, "[BT] Continuing with RFCOMM/SDP registration.");
        });

        Check.Run("a provider that throws is contained as well", () =>
        {
            var log = new LogSink();
            Check.DoesNotThrow(
                () => BluetoothDiscoverability.Apply(FakeDiscoverability.Throwing(), required: false, log.Write),
                "even a misbehaving provider must not abort startup");
            Check.Contains(log.Lines, BluetoothDiscoverability.FailurePrefix);
            Check.Contains(log.Lines, BluetoothDiscoverability.ContinueMessage);
        });

        Check.Run("success is reported without warnings", () =>
        {
            var log = new LogSink();
            var result = BluetoothDiscoverability.Apply(FakeDiscoverability.Working(), required: false, log.Write);
            Check.True(result.Succeeded, "the fake adapter accepts discoverable mode");
            Check.Contains(log.Lines, "[BT] Bluetooth adapter: FakeAdapter (00:11:22:33:44:55)");
            Check.DoesNotContain(log.Lines, BluetoothDiscoverability.FailurePrefix);
        });

        Check.Run("--no-discoverable skips the attempt quietly", () =>
        {
            var log = new LogSink();
            var result = BluetoothDiscoverability.Apply(new DisabledDiscoverability(), required: true, log.Write);
            Check.True(result.Skipped, "nothing was attempted");
            Check.Contains(log.Lines, "[BT] Discoverable mode not attempted");
            Check.Contains(log.Lines, BluetoothDiscoverability.ContinueMessage);
            Check.DoesNotContain(log.Lines, BluetoothDiscoverability.FailurePrefix);
        });

        Check.Run("required mode still fails (Windows behaviour unchanged)", () =>
        {
            var log = new LogSink();
            var error = Check.Throws<IOException>(
                () => BluetoothDiscoverability.Apply(FakeDiscoverability.BluezFailed(), required: true, log.Write),
                "platforms that require discoverability keep the old hard failure");
            Check.True(error.Message.Contains("org.bluez.Error.Failed", StringComparison.Ordinal),
                "the original error is preserved in the exception");
            Check.DoesNotContain(log.Lines, BluetoothDiscoverability.ContinueMessage);
        });

        Check.Run("the real InTheHand provider never throws", () =>
        {
            var provider = new InTheHandDiscoverability();
            Check.DoesNotThrow(() => provider.DescribeAdapter(), "describing the adapter is best-effort");
            var result = DiscoverabilityResult.Success;
            Check.DoesNotThrow(() => result = provider.TryMakeDiscoverable(), "the attempt is best-effort");
            Console.WriteLine(result.Succeeded
                ? "        (this machine accepted discoverable mode)"
                : $"        (this machine reported: {result.Message})");
        });

        Check.Section("BluetoothServer startup");

        Check.Run("Linux default policy is best-effort", () =>
        {
            using var server = new BluetoothServer(FakeDiscoverability.BluezFailed());
            Check.Equal(!OperatingSystem.IsLinux(), server.DiscoverabilityRequired,
                "discoverability is only mandatory off Linux");
        });

        Check.Run("Start() reaches RFCOMM/SDP after a BlueZ discoverability error", () =>
        {
            var log = new LogSink();
            using var server = new BluetoothServer(
                FakeDiscoverability.BluezFailed(), discoverabilityRequired: false, log: log.Write);

            Exception? failure = null;
            try
            {
                server.Start();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // Whatever the machine supports, startup must have continued past the
            // discoverability step into the native RFCOMM/SDP code.
            Check.Contains(log.Lines, BluetoothDiscoverability.ContinueMessage);

            if (failure is null)
            {
                // Real adapter + BlueZ compatibility mode: the whole path worked.
                Check.Contains(log.Lines, "[BT] Native BlueZ SPP SDP record registered on RFCOMM channel 1");
                Check.Contains(log.Lines, "[BT] RFCOMM listener active on channel 1");
                Check.False(server.IsConnected, "no phone is connected yet");
                server.Stop();
                Console.WriteLine("        (RFCOMM listener really started and was stopped again)");
            }
            else
            {
                // No Bluetooth stack here (container/CI): the failure must come from the
                // native stage, never from discoverability.
                var message = failure.Message;
                Check.True(
                    message.Contains("RFCOMM", StringComparison.Ordinal) ||
                    message.Contains("SDP", StringComparison.Ordinal),
                    $"startup must fail in the native stage, but failed with: {message}");
                Check.False(message.Contains("discoverable", StringComparison.OrdinalIgnoreCase),
                    "discoverability must not be the reason startup failed");
                Console.WriteLine($"        (no Bluetooth stack here; native stage reported: {message})");
            }
        });

        Check.Run("required discoverability aborts before the native stage", () =>
        {
            var log = new LogSink();
            using var server = new BluetoothServer(
                FakeDiscoverability.BluezFailed(), discoverabilityRequired: true, log: log.Write);

            var error = Check.Throws<IOException>(() => server.Start(), "the strict policy keeps failing fast");
            Check.True(error.Message.Contains("discoverable", StringComparison.OrdinalIgnoreCase),
                "the failure names discoverability");
            Check.DoesNotContain(log.Lines, "[BT] RFCOMM listener active");
        });

        Check.Section("Protocol and mapping are untouched");

        Check.Run("SPP UUID and ports are unchanged", () =>
        {
            Check.Equal("00001101-0000-1000-8000-00805f9b34fb", Protocol.SppUuid.ToString(), "SPP UUID");
            Check.Equal("RemoteGamepad", Protocol.ServiceName, "SDP service name");
            Check.Equal(26760, UdpInputServer.Port, "UDP input port");
            Check.Equal(26761, UdpDiscoveryListener.Port, "UDP discovery port");
        });

        Check.Run("Android JSON still parses into every controller field", () =>
        {
            const string packet = "{\"lx\":0.5,\"ly\":-0.25,\"rx\":-1,\"ry\":1,\"lt\":0.25,\"rt\":0.75," +
                                  "\"a\":true,\"b\":false,\"x\":true,\"y\":false,\"lb\":true,\"rb\":false," +
                                  "\"dUp\":true,\"dDown\":false,\"dLeft\":true,\"dRight\":false," +
                                  "\"start\":true,\"select\":false,\"l3\":true,\"r3\":false}";
            var parsed = InputParser.Parse(packet);
            Check.True(parsed is not null, "the packet parses");
            var expected = new InputParser.ControllerState(
                0.5f, -0.25f, -1f, 1f, 0.25f, 0.75f,
                true, false, true, false, true, false,
                true, false, true, false, true, false, true, false);
            Check.Equal(expected, parsed!.Value, "all 20 fields survive the round trip");
        });

        Console.WriteLine($"\nResult: {Check.Passed} passed, {Check.Failed} failed");
        return Check.Failed == 0 ? 0 : 1;
    }
}
