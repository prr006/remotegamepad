namespace RemoteGamepadServer.Tests;

/// <summary>Minimal assertion helpers so the tests need no external test framework.</summary>
internal static class Check
{
    private static int _passed;
    private static int _failed;

    public static int Passed => _passed;
    public static int Failed => _failed;

    public static void Run(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Section(string title) => Console.WriteLine($"\n== {title} ==");

    public static void True(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException($"expected true: {because}");
    }

    public static void False(bool condition, string because) => True(!condition, because);

    public static void Equal<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected '{expected}' but got '{actual}' ({because})");
    }

    public static void Contains(IEnumerable<string> lines, string expected)
    {
        var all = lines.ToList();
        if (!all.Any(line => line.Contains(expected, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"log does not contain '{expected}'. Log was:{Environment.NewLine}    {string.Join(Environment.NewLine + "    ", all)}");
    }

    public static void DoesNotContain(IEnumerable<string> lines, string unexpected)
    {
        if (lines.Any(line => line.Contains(unexpected, StringComparison.Ordinal)))
            throw new InvalidOperationException($"log unexpectedly contains '{unexpected}'");
    }

    public static TException Throws<TException>(Action body, string because) where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new InvalidOperationException(
                $"expected {typeof(TException).Name} but got {other.GetType().Name}: {other.Message} ({because})");
        }
        throw new InvalidOperationException($"expected {typeof(TException).Name} but nothing was thrown ({because})");
    }

    /// <summary>Runs <paramref name="body"/> and fails the test if it throws.</summary>
    public static void DoesNotThrow(Action body, string because)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{because}: unexpected {ex.GetType().Name}: {ex.Message}", ex);
        }
    }
}

/// <summary>Thread-safe log sink that can be handed to the server as its logger.</summary>
internal sealed class LogSink
{
    private readonly List<string> _lines = new();

    public void Write(string line)
    {
        lock (_lines) _lines.Add(line);
    }

    public List<string> Lines
    {
        get { lock (_lines) return new List<string>(_lines); }
    }
}

/// <summary>Discoverability provider whose behaviour each test chooses.</summary>
internal sealed class FakeDiscoverability : IBluetoothDiscoverability
{
    private readonly Func<DiscoverabilityResult> _attempt;
    private readonly Func<string?> _describe;

    public FakeDiscoverability(Func<DiscoverabilityResult> attempt, Func<string?>? describe = null)
    {
        _attempt = attempt;
        _describe = describe ?? (() => "FakeAdapter (00:11:22:33:44:55)");
    }

    public int Attempts { get; private set; }

    /// <summary>Simulates exactly what Fedora 44 + BlueZ 5.87 reports.</summary>
    public static FakeDiscoverability BluezFailed() =>
        new(() => DiscoverabilityResult.Failed("org.bluez.Error.Failed: Failed"));

    /// <summary>A provider that violates the contract and throws instead of reporting.</summary>
    public static FakeDiscoverability Throwing() =>
        new(() => throw new InvalidOperationException("org.bluez.Error.Failed: Failed"));

    public static FakeDiscoverability Working() => new(() => DiscoverabilityResult.Success);

    public string? DescribeAdapter() => _describe();

    public DiscoverabilityResult TryMakeDiscoverable()
    {
        Attempts++;
        return _attempt();
    }
}
