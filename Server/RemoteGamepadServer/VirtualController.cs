namespace RemoteGamepadServer;

using System.Runtime.InteropServices;

/// <summary>Linux uinput-backed virtual gamepad. Requires writable /dev/uinput.</summary>
public sealed class VirtualController : IDisposable
{
    private const int O_WRONLY = 1, O_NONBLOCK = 0x800;
    private const int EV_SYN = 0, EV_KEY = 1, EV_ABS = 3;
    private const int SYN_REPORT = 0;
    // Linux input_event.code is __u16; model event codes with that exact width.
    private const ushort ABS_X = 0, ABS_Y = 1, ABS_RX = 3, ABS_RY = 4, ABS_Z = 2, ABS_RZ = 5, ABS_HAT0X = 16, ABS_HAT0Y = 17;
    private const ushort BTN_SOUTH = 0x130, BTN_EAST = 0x131, BTN_NORTH = 0x133, BTN_WEST = 0x134;
    private const ushort BTN_TL = 0x136, BTN_TR = 0x137, BTN_SELECT = 0x13a, BTN_START = 0x13b, BTN_MODE = 0x13c, BTN_THUMBL = 0x13d, BTN_THUMBR = 0x13e;
    private int _fd = -1;
    public bool IsAvailable => _fd >= 0;

    public void Initialize()
    {
        _fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (_fd < 0) throw new IOException($"Cannot open /dev/uinput: {Marshal.GetLastPInvokeError()}. See README-LINUX.md.");
        try
        {
            SetBit(IOC(1, 'U', 100, 4), EV_SYN); SetBit(IOC(1, 'U', 100, 4), EV_KEY); SetBit(IOC(1, 'U', 100, 4), EV_ABS);
            foreach (var key in new[] { BTN_SOUTH, BTN_EAST, BTN_NORTH, BTN_WEST, BTN_TL, BTN_TR, BTN_SELECT, BTN_START, BTN_MODE, BTN_THUMBL, BTN_THUMBR }) SetBit(IOC(1, 'U', 101, 4), key);
            foreach (var abs in new[] { ABS_X, ABS_Y, ABS_RX, ABS_RY, ABS_Z, ABS_RZ, ABS_HAT0X, ABS_HAT0Y }) SetBit(IOC(1, 'U', 103, 4), abs);
            var setup = new UInputSetup { Id = new InputId { Bus = 0x03, Vendor = 0x1209, Product = 0x0001, Version = 1 }, Name = "RemoteGamepad" };
            if (ioctl(_fd, IOC(1, 'U', 3, Marshal.SizeOf<UInputSetup>()), ref setup) < 0) Fail("UI_DEV_SETUP");
            foreach (var axis in new[] { ABS_X, ABS_Y, ABS_RX, ABS_RY }) SetAbs(axis, -32768, 32767);
            foreach (var axis in new[] { ABS_Z, ABS_RZ }) SetAbs(axis, 0, 255);
            foreach (var axis in new[] { ABS_HAT0X, ABS_HAT0Y }) SetAbs(axis, -1, 1);
            if (ioctl(_fd, IOC(0, 'U', 1, 0), 0) < 0) Fail("UI_DEV_CREATE");
            Console.WriteLine("[UINPUT] Virtual gamepad created");
        }
        catch { close(_fd); _fd = -1; throw; }
    }

    public void Update(InputParser.ControllerState s)
    {
        if (_fd < 0) return;
        foreach (var (key, value) in new[] { (BTN_SOUTH,s.A),(BTN_EAST,s.B),(BTN_WEST,s.X),(BTN_NORTH,s.Y),(BTN_TL,s.Lb),(BTN_TR,s.Rb),(BTN_START,s.Start),(BTN_SELECT,s.Select),(BTN_MODE,false),(BTN_THUMBL,s.L3),(BTN_THUMBR,s.R3) }) Key(key,value);
        Axis(ABS_X, Scale(s.Lx)); Axis(ABS_Y, Scale(s.Ly)); Axis(ABS_RX, Scale(s.Rx)); Axis(ABS_RY, Scale(s.Ry)); Axis(ABS_Z,(int)(s.Lt*255)); Axis(ABS_RZ,(int)(s.Rt*255));
        Axis(ABS_HAT0X, (s.DRight?1:0)-(s.DLeft?1:0)); Axis(ABS_HAT0Y, (s.DDown?1:0)-(s.DUp?1:0)); Sync();
    }
    public void Reset() { if (_fd >= 0) Update(default); }
    private static int Scale(float f) => (int)(Math.Clamp(f,-1,1)*32767);
    private void Key(int c,bool v)=>Event(EV_KEY,c,v?1:0); private void Axis(int c,int v)=>Event(EV_ABS,c,v); private void Sync()=>Event(EV_SYN,SYN_REPORT,0);
    private void Event(ushort type,ushort code,int value) { var e = new InputEvent { Type=type, Code=code, Value=value }; if(write(_fd,ref e,(nuint)Marshal.SizeOf<InputEvent>())<0) Fail("writing input event"); }
    private void SetBit(uint request,int bit) { if(ioctl(_fd,request,bit)<0) Fail("setting uinput capability"); }
    private void SetAbs(int axis,int min,int max) { var a=new UInputAbsSetup { Code=(ushort)axis, AbsInfo=new AbsInfo { Minimum=min,Maximum=max } }; if(ioctl(_fd,IOC(1,'U',4,Marshal.SizeOf<UInputAbsSetup>()),ref a)<0) Fail("UI_ABS_SETUP"); }
    private static void Fail(string operation)=>throw new IOException($"uinput {operation} failed: {Marshal.GetLastPInvokeError()}");
    private static uint IOC(int dir,char type,int nr,int size)=>(uint)((dir<<30)|(size<<16)|(type<<8)|nr);
    public void Dispose() { if(_fd<0)return; Reset(); ioctl(_fd,IOC(0,'U',2,0),0); close(_fd); _fd=-1; }
    [StructLayout(LayoutKind.Sequential)] private struct InputId { public ushort Bus,Vendor,Product,Version; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Ansi)] private struct UInputSetup { public InputId Id; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string Name; public uint FfEffectsMax; }
    [StructLayout(LayoutKind.Sequential)] private struct AbsInfo { public int Value,Minimum,Maximum,Fuzz,Flat,Resolution; }
    [StructLayout(LayoutKind.Sequential)] private struct UInputAbsSetup { public ushort Code; public AbsInfo AbsInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct InputEvent { public long Seconds, Microseconds; public ushort Type,Code; public int Value; }
    [DllImport("libc",SetLastError=true,CharSet=CharSet.Ansi)] private static extern int open(string path,int flags);
    [DllImport("libc",SetLastError=true)] private static extern int close(int fd);
    [DllImport("libc",SetLastError=true)] private static extern long write(int fd,ref InputEvent data,nuint count);
    [DllImport("libc",SetLastError=true,EntryPoint="ioctl")] private static extern int ioctl(int fd,uint request,int value);
    [DllImport("libc",SetLastError=true,EntryPoint="ioctl")] private static extern int ioctl(int fd,uint request,ref UInputSetup setup);
    [DllImport("libc",SetLastError=true,EntryPoint="ioctl")] private static extern int ioctl(int fd,uint request,ref UInputAbsSetup setup);
}
