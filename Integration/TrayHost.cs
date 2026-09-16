using System.Runtime.InteropServices;

namespace CodexUsageNotch;

internal sealed class TrayHost : NativeWindow, IDisposable
{
    internal const int Callback = 0x8001;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly System.Windows.Forms.Timer _retry = new() { Interval = 5000 };
    private Icon? _icon;
    private string _text = "Codex usage";
    private bool _added;
    private bool _disposed;
    private Rectangle? _lastBounds;
    public event Action? HoverOpened;
    public event Action? HoverClosed;
    public event Action? Selected;
    public event Action? ContextRequested;
    public event Action? EnvironmentChanged;
    public event Action? Resumed;
    public event Action? ExitRequested;
    public bool Registered => _added;
    public TrayHost()
    {
        CreateHandle(new CreateParams { Caption = "CodexUsageNotch.TrayHost", Style = unchecked((int)0x80000000), ExStyle = 0x80 });
        _retry.Tick += (_, _) => { if (!_added) Register(); };
        _retry.Start();
    }
    public void Update(Icon icon, string text)
    {
        var previous = _icon;
        _icon = icon;
        _text = text.Length > 127 ? text[..127] : text;
        if (_added)
        {
            var data = Data();
            if (!Shell_NotifyIcon(1, ref data)) _added = false;
        }
        if (!_added) Register();
        previous?.Dispose();
    }
    private void Register()
    {
        if (_disposed || _icon is null) return;
        var data = Data();
        _added = Shell_NotifyIcon(0, ref data);
        if (_added)
        {
            data.Version = 4;
            if (!Shell_NotifyIcon(4, ref data)) Diagnostics.Write("tray-version");
        }
    }
    public Rectangle IconBounds()
    {
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = Handle, Id = 1 };
        if (Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 && rect.Right > rect.Left)
            _lastBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        // The overflow flyout may close when an interactive card takes focus.
        return _lastBounds ?? new Rectangle(Cursor.Position, new Size(1, 1));
    }
    public void FocusHost() => SetForegroundWindow(Handle);
    public void ReturnFocus() { var data = Data(); Shell_NotifyIcon(3, ref data); }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0010) { ExitRequested?.Invoke(); message.Result = IntPtr.Zero; return; }
        if (message.Msg == _taskbarCreated) { _added = false; _lastBounds = null; Register(); EnvironmentChanged?.Invoke(); }
        else if (message.Msg is 0x001A or 0x007E or 0x031A) { _lastBounds = null; EnvironmentChanged?.Invoke(); }
        else if (message.Msg == 0x0218 && message.WParam.ToInt64() is 0x07 or 0x12)
        { Resumed?.Invoke(); message.Result = (IntPtr)1; return; }
        else if (message.Msg == Callback)
        {
            switch ((int)(message.LParam.ToInt64() & 0xffff))
            {
                case 0x406: HoverOpened?.Invoke(); break;
                case 0x407: HoverClosed?.Invoke(); break;
                case 0x400: case 0x401: Selected?.Invoke(); break;
                case 0x007B: ContextRequested?.Invoke(); break;
            }
        }
        base.WndProc(ref message);
    }
    private IconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<IconData>(), Window = Handle, Id = 1,
        Flags = 1 | 2 | 4, Message = Callback, Icon = _icon?.Handle ?? IntPtr.Zero,
        Tip = _text, Info = "", Title = ""
    };
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retry.Dispose();
        var data = Data(); Shell_NotifyIcon(2, ref data);
        _icon?.Dispose(); _icon = null;
        DestroyHandle();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Message; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(uint message, ref IconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier id, out NativeRect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
}

internal static class PopupPlacement
{
    public static Point Place(Rectangle anchor, Size size, Rectangle work, int gap = 8)
    {
        var x = anchor.Left + anchor.Width / 2 - size.Width / 2;
        var y = anchor.Top - size.Height - gap;
        if (y < work.Top) y = anchor.Bottom + gap;
        return new Point(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width)),
            Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height)));
    }
}
