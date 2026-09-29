namespace RemoteGamepadServer;

using System.Runtime.InteropServices;

/// <summary>Linux uinput-backed virtual gamepad. Requires writable /dev/uinput.</summary>
public sealed class VirtualController : IDisposable
{
    private const int O_WRONLY = 1;
    private const int O_CLOEXEC = 0x80000;
    private const int EV_SYN = 0, EV_KEY = 1, EV_ABS = 3;
    private const int SYN_REPORT = 0;
    // Linux input_event.code is __u16; model event codes with that exact width.
    private const ushort ABS_X = 0, ABS_Y = 1, ABS_RX = 3, ABS_RY = 4, ABS_Z = 2, ABS_RZ = 5, ABS_HAT0X = 16, ABS_HAT0Y = 17;
    private const ushort BTN_SOUTH = 0x130, BTN_EAST = 0x131, BTN_NORTH = 0x133, BTN_WEST = 0x134;
    private const ushort BTN_TL = 0x136, BTN_TR = 0x137, BTN_SELECT = 0x13a, BTN_START = 0x13b, BTN_THUMBL = 0x13d, BTN_THUMBR = 0x13e;
    private const ushort BTN_DPAD_UP = 0x220, BTN_DPAD_DOWN = 0x221, BTN_DPAD_LEFT = 0x222, BTN_DPAD_RIGHT = 0x223;
    // Android labels follow Xbox physical positions: X is left/WEST; Y is top/NORTH.
    // Linux's historical BTN_X alias is BTN_NORTH and BTN_Y alias is BTN_WEST,
    // so map the Android face labels to the physical-position codes explicitly.
    // Triggers use common Xbox evdev ABS_Z (LT) and ABS_RZ (RT), each 0..255.
    // D-pad emits matching digital BTN_DPAD_* and analog ABS_HAT0X/Y values.
    private readonly object _sync = new();
    private int _fd = -1;
    private bool _created;
    private bool _hasLastState;
    private InputParser.ControllerState _lastState;
    private long _lastTraceTick;
    public bool IsAvailable => _created;
    public bool TraceInput { get; set; }

    public void Initialize()
    {
        if (_fd >= 0) throw new InvalidOperationException("The virtual controller is already initialized.");
        _fd = open("/dev/uinput", O_WRONLY | O_CLOEXEC);
        if (_fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new IOException($"Cannot open /dev/uinput (errno {errno}). Ensure the uinput module is loaded and the current user has write permission; see README-LINUX.md.");
        }
        try
        {
            SetBit(IOC(1, 'U', 100, 4), EV_SYN); SetBit(IOC(1, 'U', 100, 4), EV_KEY); SetBit(IOC(1, 'U', 100, 4), EV_ABS);
            foreach (var key in new[] { BTN_SOUTH, BTN_EAST, BTN_NORTH, BTN_WEST, BTN_TL, BTN_TR, BTN_SELECT, BTN_START, BTN_THUMBL, BTN_THUMBR, BTN_DPAD_UP, BTN_DPAD_DOWN, BTN_DPAD_LEFT, BTN_DPAD_RIGHT }) SetBit(IOC(1, 'U', 101, 4), key);
            foreach (var abs in new[] { ABS_X, ABS_Y, ABS_RX, ABS_RY, ABS_Z, ABS_RZ, ABS_HAT0X, ABS_HAT0Y }) SetBit(IOC(1, 'U', 103, 4), abs);
            var setup = new UInputSetup { Id = new InputId { Bus = 0x03, Vendor = 0x1209, Product = 0x0001, Version = 1 }, Name = "RemoteGamepad" };
            if (ioctl(_fd, IOC(1, 'U', 3, Marshal.SizeOf<UInputSetup>()), ref setup) < 0) Fail("UI_DEV_SETUP");
            foreach (var axis in new[] { ABS_X, ABS_Y, ABS_RX, ABS_RY }) SetAbs(axis, -32768, 32767);
            foreach (var axis in new[] { ABS_Z, ABS_RZ }) SetAbs(axis, 0, 255);
            foreach (var axis in new[] { ABS_HAT0X, ABS_HAT0Y }) SetAbs(axis, -1, 1);
            if (ioctl(_fd, IOC(0, 'U', 1, 0), 0) < 0) Fail("UI_DEV_CREATE");
            _created = true;
            Console.WriteLine("[UINPUT] Virtual gamepad created");
        }
        catch { close(_fd); _fd = -1; _created = false; throw; }
    }

    public void Update(InputParser.ControllerState s)
    {
        lock (_sync)
        {
            if (_fd < 0 || !_created) return;
            var dpadX = ResolveDpadAxis(s.DLeft, s.DRight);
            var dpadY = ResolveDpadAxis(s.DUp, s.DDown);
            foreach (var (key, value) in new[]
            {
                (BTN_SOUTH, s.A), (BTN_EAST, s.B), (BTN_WEST, s.X), (BTN_NORTH, s.Y),
                (BTN_TL, s.Lb), (BTN_TR, s.Rb), (BTN_START, s.Start), (BTN_SELECT, s.Select),
                (BTN_THUMBL, s.L3), (BTN_THUMBR, s.R3),
                (BTN_DPAD_UP, dpadY < 0), (BTN_DPAD_DOWN, dpadY > 0),
                (BTN_DPAD_LEFT, dpadX < 0), (BTN_DPAD_RIGHT, dpadX > 0)
            }) Key(key, value);
            Axis(ABS_X, Scale(s.Lx));
            Axis(ABS_Y, Scale(s.Ly));
            Axis(ABS_RX, Scale(s.Rx));
            Axis(ABS_RY, Scale(s.Ry));
            Axis(ABS_Z, (int)(Math.Clamp(s.Lt, 0f, 1f) * 255));
            Axis(ABS_RZ, (int)(Math.Clamp(s.Rt, 0f, 1f) * 255));
            Axis(ABS_HAT0X, dpadX);
            Axis(ABS_HAT0Y, dpadY);
            Sync();
            if (TraceInput && (!_hasLastState || s != _lastState))
            {
                var previous = _hasLastState ? _lastState : default;
                TraceButtonChange("A", s.A, previous.A, "BTN_SOUTH");
                TraceButtonChange("B", s.B, previous.B, "BTN_EAST");
                TraceButtonChange("X", s.X, previous.X, "BTN_WEST");
                TraceButtonChange("Y", s.Y, previous.Y, "BTN_NORTH");
                TraceButtonChange("LB", s.Lb, previous.Lb, "BTN_TL");
                TraceButtonChange("RB", s.Rb, previous.Rb, "BTN_TR");
                TraceButtonChange("Start", s.Start, previous.Start, "BTN_START");
                TraceButtonChange("Select", s.Select, previous.Select, "BTN_SELECT");
                TraceButtonChange("L3", s.L3, previous.L3, "BTN_THUMBL");
                TraceButtonChange("R3", s.R3, previous.R3, "BTN_THUMBR");
                var previousDpadX = ResolveDpadAxis(previous.DLeft, previous.DRight);
                var previousDpadY = ResolveDpadAxis(previous.DUp, previous.DDown);
                TraceButtonChange("DUp", dpadY < 0, previousDpadY < 0, "BTN_DPAD_UP + ABS_HAT0Y(-1)");
                TraceButtonChange("DDown", dpadY > 0, previousDpadY > 0, "BTN_DPAD_DOWN + ABS_HAT0Y(+1)");
                TraceButtonChange("DLeft", dpadX < 0, previousDpadX < 0, "BTN_DPAD_LEFT + ABS_HAT0X(-1)");
                TraceButtonChange("DRight", dpadX > 0, previousDpadX > 0, "BTN_DPAD_RIGHT + ABS_HAT0X(+1)");

                var now = Environment.TickCount64;
                if (HasAnalogChange(s, previous) && now - _lastTraceTick >= 250)
                {
                    Console.WriteLine($"[MAP] lx={s.Lx:F2}->ABS_X ly={s.Ly:F2}->ABS_Y rx={s.Rx:F2}->ABS_RX ry={s.Ry:F2}->ABS_RY lt={s.Lt:F2}->ABS_Z rt={s.Rt:F2}->ABS_RZ");
                    _lastTraceTick = now;
                }
            }
            _lastState = s;
            _hasLastState = true;
        }
    }

    private static int ResolveDpadAxis(bool negative, bool positive) =>
        negative == positive ? 0 : negative ? -1 : 1;

    private static bool HasAnalogChange(InputParser.ControllerState a, InputParser.ControllerState b) =>
        a.Lx != b.Lx || a.Ly != b.Ly || a.Rx != b.Rx || a.Ry != b.Ry || a.Lt != b.Lt || a.Rt != b.Rt;

    private static void TraceButtonChange(string label, bool current, bool previous, string mapping)
    {
        if (current != previous)
            Console.WriteLine($"[MAP] {label}={current} -> {mapping} {(current ? "pressed" : "released")}");
    }

    public void Reset() => Update(default);

    /// <summary>Emit isolated press/axis states for live inspection with evtest.</summary>
    public void RunMappingSelfTest(Action<string> report, int holdMilliseconds = 350, int releaseMilliseconds = 150)
    {
        var neutral = default(InputParser.ControllerState);
        var tests = new (string Label, InputParser.ControllerState State)[]
        {
            ("A -> BTN_SOUTH", neutral with { A = true }),
            ("B -> BTN_EAST", neutral with { B = true }),
            ("X (left physical button) -> BTN_WEST", neutral with { X = true }),
            ("Y (top physical button) -> BTN_NORTH", neutral with { Y = true }),
            ("LB -> BTN_TL", neutral with { Lb = true }),
            ("RB -> BTN_TR", neutral with { Rb = true }),
            ("Start -> BTN_START", neutral with { Start = true }),
            ("Select -> BTN_SELECT", neutral with { Select = true }),
            ("L3 -> BTN_THUMBL", neutral with { L3 = true }),
            ("R3 -> BTN_THUMBR", neutral with { R3 = true }),
            ("D-pad up -> BTN_DPAD_UP + ABS_HAT0Y(-1)", neutral with { DUp = true }),
            ("D-pad down -> BTN_DPAD_DOWN + ABS_HAT0Y(+1)", neutral with { DDown = true }),
            ("D-pad left -> BTN_DPAD_LEFT + ABS_HAT0X(-1)", neutral with { DLeft = true }),
            ("D-pad right -> BTN_DPAD_RIGHT + ABS_HAT0X(+1)", neutral with { DRight = true }),
            ("LT half/full -> ABS_Z(127/255)", neutral with { Lt = 0.5f }),
            ("LT full -> ABS_Z(255)", neutral with { Lt = 1f }),
            ("RT half/full -> ABS_RZ(127/255)", neutral with { Rt = 0.5f }),
            ("RT full -> ABS_RZ(255)", neutral with { Rt = 1f }),
            ("Left X left/right -> ABS_X(-32768/+32767)", neutral with { Lx = -1f }),
            ("Left X right -> ABS_X(+32767)", neutral with { Lx = 1f }),
            ("Left Y up/down -> ABS_Y(-32768/+32767)", neutral with { Ly = -1f }),
            ("Left Y down -> ABS_Y(+32767)", neutral with { Ly = 1f }),
            ("Right X left/right -> ABS_RX(-32768/+32767)", neutral with { Rx = -1f }),
            ("Right X right -> ABS_RX(+32767)", neutral with { Rx = 1f }),
            ("Right Y up/down -> ABS_RY(-32768/+32767)", neutral with { Ry = -1f }),
            ("Right Y down -> ABS_RY(+32767)", neutral with { Ry = 1f }),
        };

        foreach (var (label, state) in tests)
        {
            report($"[SELF-TEST] {label}; then release");
            Update(state);
            Thread.Sleep(holdMilliseconds);
            Update(neutral);
            Thread.Sleep(releaseMilliseconds);
        }
        Reset();
        report("[SELF-TEST] Completed; controller reset");
    }

    private static int Scale(float value)
    {
        if (!float.IsFinite(value)) return 0;
        value = Math.Clamp(value, -1f, 1f);
        return value <= -1f ? short.MinValue : (int)(value * short.MaxValue);
    }
    private void Key(ushort code, bool pressed) => Event(EV_KEY, code, pressed ? 1 : 0);
    private void Axis(ushort code, int value) => Event(EV_ABS, code, value);
    private void Sync() => Event(EV_SYN, SYN_REPORT, 0);
    private void Event(ushort type, ushort code, int value)
    {
        var inputEvent = new InputEvent { Type = type, Code = code, Value = value };
        var size = (nuint)Marshal.SizeOf<InputEvent>();
        var written = write(_fd, ref inputEvent, size);
        if (written < 0) Fail("writing input event");
        if ((nuint)written != size) throw new IOException($"Short uinput event write: {written} of {size} bytes.");
    }
    private void SetBit(nuint request, int bit)
    {
        if (ioctl(_fd, request, (nuint)bit) < 0) Fail("setting uinput capability");
    }
    private void SetAbs(ushort axis, int min, int max)
    {
        var setup = new UInputAbsSetup { Code = axis, AbsInfo = new AbsInfo { Minimum = min, Maximum = max } };
        if (ioctl(_fd, IOC(1, 'U', 4, Marshal.SizeOf<UInputAbsSetup>()), ref setup) < 0) Fail("UI_ABS_SETUP");
    }
    private static void Fail(string operation)=>throw new IOException($"uinput {operation} failed: {Marshal.GetLastPInvokeError()}");
    private static nuint IOC(int dir, char type, int nr, int size) =>
        (nuint)((dir << 30) | (size << 16) | (type << 8) | nr);
    public void Dispose()
    {
        lock (_sync)
        {
            if (_fd < 0) return;
            try { if (_created) Update(default); }
            finally
            {
                if (_created) ioctl(_fd, IOC(0, 'U', 2, 0), 0);
                close(_fd); // Closing also destroys an already-created uinput device.
                _fd = -1;
                _created = false;
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct InputId { public ushort Bus,Vendor,Product,Version; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Ansi)] private struct UInputSetup { public InputId Id; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string Name; public uint FfEffectsMax; }
    [StructLayout(LayoutKind.Sequential)] private struct AbsInfo { public int Value,Minimum,Maximum,Fuzz,Flat,Resolution; }
    [StructLayout(LayoutKind.Sequential)] private struct UInputAbsSetup { public ushort Code; public AbsInfo AbsInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct InputEvent { public long Seconds, Microseconds; public ushort Type,Code; public int Value; }
    [DllImport("libc",SetLastError=true,CharSet=CharSet.Ansi)] private static extern int open(string path,int flags);
    [DllImport("libc",SetLastError=true)] private static extern int close(int fd);
    [DllImport("libc",SetLastError=true)] private static extern long write(int fd,ref InputEvent data,nuint count);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl(int fd, nuint request, nuint value);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl(int fd, nuint request, ref UInputSetup setup);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl(int fd, nuint request, ref UInputAbsSetup setup);
}
