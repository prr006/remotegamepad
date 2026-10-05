namespace RemoteGamepadServer;

using InTheHand.Net.Bluetooth;

/// <summary>Outcome of one best-effort attempt to make the local adapter discoverable.</summary>
/// <param name="Succeeded">True when the adapter accepted discoverable mode.</param>
/// <param name="Skipped">True when the attempt was deliberately not made.</param>
/// <param name="Message">Error text or skip reason; <c>null</c> on success.</param>
public readonly record struct DiscoverabilityResult(bool Succeeded, bool Skipped, string? Message)
{
    public static DiscoverabilityResult Success { get; } = new(true, false, null);

    public static DiscoverabilityResult Failed(string? error) =>
        new(false, false, Normalize(error, "unknown error"));

    public static DiscoverabilityResult NotAttempted(string? reason) =>
        new(false, true, Normalize(reason, "skipped"));

    private static string Normalize(string? text, string fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
}

/// <summary>
/// "Make the local adapter discoverable", expressed as a replaceable operation.
/// <para>
/// Discoverability is <b>optional</b> for RemoteGamepad: the Android app connects to the SPP
/// service of a paired device, so RFCOMM + SDP are what actually matter. BlueZ regularly
/// refuses the request with <c>org.bluez.Error.Failed</c> (adapter not powered, no active
/// session for polkit, kernel/controller quirks), which used to abort Bluetooth startup
/// entirely. Implementations therefore report failures instead of throwing, and tests can
/// substitute a provider that simulates a BlueZ error.
/// </para>
/// </summary>
public interface IBluetoothDiscoverability
{
    /// <summary>Adapter description for logging, or <c>null</c> when none can be queried.</summary>
    string? DescribeAdapter();

    /// <summary>Attempts to switch the adapter to discoverable mode. Must not throw.</summary>
    DiscoverabilityResult TryMakeDiscoverable();
}

/// <summary>Default provider, backed by InTheHand.Net.Bluetooth (BlueZ/D-Bus on Linux).</summary>
public sealed class InTheHandDiscoverability : IBluetoothDiscoverability
{
    public string? DescribeAdapter()
    {
        try
        {
            var radio = BluetoothRadio.Default;
            return radio is null ? null : $"{radio.Name} ({radio.LocalAddress})";
        }
        catch (Exception ex)
        {
            return $"unavailable ({Describe(ex)})";
        }
    }

    public DiscoverabilityResult TryMakeDiscoverable()
    {
        try
        {
            var radio = BluetoothRadio.Default;
            if (radio is null)
                return DiscoverabilityResult.Failed("the Bluetooth stack reported no local radio");
            radio.Mode = RadioMode.Discoverable;
            return DiscoverabilityResult.Success;
        }
        catch (Exception ex)
        {
            return DiscoverabilityResult.Failed(Describe(ex));
        }
    }

    /// <summary>Unwraps wrapper exceptions so D-Bus/BlueZ errors stay readable in the log.</summary>
    internal static string Describe(Exception exception)
    {
        var baseException = exception.GetBaseException();
        var message = baseException.Message?.Trim();
        return string.IsNullOrEmpty(message) ? baseException.GetType().Name : message;
    }
}

/// <summary>Provider that performs no adapter changes (<c>--no-discoverable</c>, tests).</summary>
public sealed class DisabledDiscoverability : IBluetoothDiscoverability
{
    public string? DescribeAdapter() => null;

    public DiscoverabilityResult TryMakeDiscoverable() =>
        DiscoverabilityResult.NotAttempted("disabled with --no-discoverable");
}

/// <summary>Policy that decides what a failed discoverability attempt means for startup.</summary>
public static class BluetoothDiscoverability
{
    /// <summary>Logged before the error text when discoverability could not be enabled.</summary>
    public const string FailurePrefix = "[BT] Discoverable mode could not be enabled through InTheHand/BlueZ: ";

    /// <summary>Logged right after <see cref="FailurePrefix"/> to show that startup continues.</summary>
    public const string ContinueMessage = "[BT] Continuing with RFCOMM/SDP registration.";

    /// <summary>Hint shown once when the adapter stays hidden; the scripts can fix it persistently.</summary>
    public const string HintMessage =
        "[BT] If the phone cannot find this machine, pair once with 'bluetoothctl discoverable on' " +
        "or run 'sudo ./scripts/setup-linux.sh --discoverable'.";

    /// <summary>
    /// Runs the discoverability step. Never throws when <paramref name="required"/> is
    /// <c>false</c> (Linux), so the caller always reaches RFCOMM bind/listen and SDP
    /// registration. When <paramref name="required"/> is <c>true</c> (Windows, unchanged
    /// behaviour) a failure is raised as an <see cref="IOException"/>.
    /// </summary>
    public static DiscoverabilityResult Apply(IBluetoothDiscoverability provider, bool required, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(log);

        var adapter = TryDescribe(provider);
        if (!string.IsNullOrEmpty(adapter)) log($"[BT] Bluetooth adapter: {adapter}");

        DiscoverabilityResult result;
        try
        {
            result = provider.TryMakeDiscoverable();
        }
        catch (Exception ex)
        {
            // A provider must not throw, but never let a buggy one abort Bluetooth startup.
            result = DiscoverabilityResult.Failed(InTheHandDiscoverability.Describe(ex));
        }

        if (result.Succeeded)
        {
            log("[BT] Adapter set to discoverable mode.");
            return result;
        }

        if (result.Skipped)
        {
            log($"[BT] Discoverable mode not attempted: {result.Message}.");
            log(ContinueMessage);
            return result;
        }

        if (required)
            throw new IOException($"Bluetooth adapter could not be made discoverable: {result.Message}");

        log(FailurePrefix + result.Message);
        log(ContinueMessage);
        log(HintMessage);
        return result;
    }

    private static string? TryDescribe(IBluetoothDiscoverability provider)
    {
        try
        {
            return provider.DescribeAdapter();
        }
        catch (Exception ex)
        {
            return $"unavailable ({InTheHandDiscoverability.Describe(ex)})";
        }
    }
}
