using System.Runtime.InteropServices;

namespace CodexUsageNotch;

internal sealed class StripForm : Form
{
    private readonly System.Windows.Forms.Timer _hover = new() { Interval = 850 };
    private double _position = 0.5;
    private Point? _dragStart;
    private int _dragLeft;
    private bool _dragging;
    private UsageState _state = UsageState.Initial;
    private AppTheme _theme = AppTheme.Current;
    public event Action? HoverRequested;
    public event Action? Selected;
    public event Action? DragStarted;
    public event Action<double>? PositionChanged;
    public double Position
    {
        get => _position;
        set { _position = Math.Clamp(value, 0, 1); Reposition(); }
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    { get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; return p; } }
    public StripForm()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true; Cursor = Cursors.SizeWE;
        AccessibleName = "Codex usage indicator";
        _hover.Tick += (_, _) => { _hover.Stop(); if (_dragStart is null && Bounds.Contains(Cursor.Position)) HoverRequested?.Invoke(); };
        MouseEnter += (_, _) => { if (_dragStart is null) _hover.Start(); };
        MouseLeave += (_, _) => _hover.Stop();
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            BeginDrag(Cursor.Position);
        };
        MouseMove += (_, _) => ContinueDrag(Cursor.Position);
        MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) EndDrag(true);
        };
        MouseCaptureChanged += (_, _) => EndDrag(false);
        DpiChanged += (_, _) => Reposition();
        Shown += (_, _) => { Reposition(); RestoreTopMost(); };
        Reposition();
    }
    internal void BeginDrag(Point pointer)
    {
        _hover.Stop(); _dragStart = pointer; _dragLeft = Left; _dragging = false;
    }
    internal void ContinueDrag(Point pointer)
    {
        if (_dragStart is not { } start) return;
        var distance = pointer.X - start.X;
        if (!_dragging && Math.Abs(distance) < Math.Max(4, SystemInformation.DragSize.Width / 2)) return;
        if (!_dragging) { _dragging = true; DragStarted?.Invoke(); }
        var work = (Screen.PrimaryScreen ?? Screen.FromPoint(pointer)).WorkingArea;
        Position = StripPlacement.PositionFor(work, Width, _dragLeft + distance);
    }
    internal void EndDrag(bool openOnClick)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        if (_dragging) { _dragging = false; PositionChanged?.Invoke(_position); }
        else if (openOnClick) Selected?.Invoke();
    }
    public void RestoreTopMost()
    {
        if (Visible && !SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0213))
            Diagnostics.Write("strip-topmost", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }
    public void Present(UsageState state, AppTheme theme)
    {
        _state = state; _theme = theme;
        AccessibleDescription = state.StatusText(DateTimeOffset.UtcNow);
        Invalidate();
    }
    public void Reposition()
    {
        var screen = Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
        var scale = DeviceDpi / 96f;
        Size = new Size((int)(210 * scale), Math.Max(6, (int)(6 * scale)));
        Location = StripPlacement.Place(screen.WorkingArea, Width, _position);
        using var shape = Shape.Round(ClientRectangle, Height / 2);
        var previous = Region; Region = new Region(shape); previous?.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_theme.Track);
        var primary = _state.Snapshot?.Primary;
        var stale = _state.IsStale(DateTimeOffset.UtcNow);
        using var brush = new SolidBrush(primary is null || stale ? _theme.Muted : _theme.Accent(primary.Remaining));
        var width = primary is null ? Width : Width * primary.Remaining / 100;
        e.Graphics.FillRectangle(brush, 0, 0, width, Height);
    }
    protected override void Dispose(bool disposing) { if (disposing) _hover.Dispose(); base.Dispose(disposing); }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

internal static class StripPlacement
{
    public static Point Place(Rectangle work, int width, double position)
    {
        var travel = Math.Max(0, work.Width - width);
        return new Point(work.Left + (int)Math.Round(travel * Math.Clamp(position, 0, 1)), work.Top);
    }
    public static double PositionFor(Rectangle work, int width, int left) =>
        work.Width <= width ? 0 : Math.Clamp((double)(left - work.Left) / (work.Width - width), 0, 1);
}

internal sealed class PositionDialog : Form
{
    private readonly Label _description = new() { AutoSize = true };
    private readonly TrackBar _slider;
    public event Action<double>? PositionPreviewed;
    public double Position => (double)_slider.Value / _slider.Maximum;
    public PositionDialog(double position, int travel, AppTheme theme)
    {
        Text = "Move top indicator";
        AccessibleName = "Move top indicator";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false; TopMost = true;
        MaximizeBox = false; MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(430, 150);
        _slider = new TrackBar
        {
            Dock = DockStyle.Fill, Minimum = 0, Maximum = Math.Max(1, travel),
            TickFrequency = Math.Max(1, travel / 10), SmallChange = 1,
            LargeChange = Math.Max(1, travel / 20), TabIndex = 0,
            AccessibleName = "Top indicator horizontal position"
        };
        _slider.Value = (int)Math.Round(Math.Clamp(position, 0, 1) * _slider.Maximum);
        var save = new Button { Text = "Save position", AutoSize = true, DialogResult = DialogResult.OK, TabIndex = 1 };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, TabIndex = 2 };
        AcceptButton = save; CancelButton = cancel;
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.AddRange([save, cancel]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_description, 0, 0);
        layout.Controls.Add(_slider, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        _slider.ValueChanged += (_, _) => { UpdateDescription(); PositionPreviewed?.Invoke(Position); };
        Shown += (_, _) => _slider.Focus();
        UpdateDescription(); UpdateTheme(theme);
    }
    public void UpdateTheme(AppTheme theme) => theme.Apply(this);
    private void UpdateDescription() => _description.Text = $"Position along the top: {Position:P0} (Left to Right)";
}

internal sealed class CardPopup : Form
{
    internal readonly UsageCard Card = new();
    private readonly Button _refresh = new() { Text = "Refresh", AccessibleName = "Refresh usage", TabIndex = 0 };
    private readonly Button _connect = new() { Text = "Connect ChatGPT", TabIndex = 1 };
    private readonly Button _move = new() { Text = "Move top indicator", AccessibleName = "Move top indicator", TabIndex = 2 };
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 50 };
    private Func<Rectangle>? _anchor;
    private bool _below;
    private bool _interactive;
    private long? _leftAt;
    private UsageState _state = UsageState.Initial;
    private AppTheme _theme = AppTheme.Current;
    private RecoveryInfo? _recovery;
    private Font? _buttonFont;
    private bool _layout;
    public event Action? RefreshRequested;
    public event Action? ConnectRequested;
    public event Action? MoveRequested;
    public event Action? ReturnFocusRequested;
    public bool Interactive => _interactive;
    protected override bool ShowWithoutActivation => !_interactive;
    protected override CreateParams CreateParams
    { get { var p = base.CreateParams; p.ExStyle |= 0x80; if (!_interactive) p.ExStyle |= 0x08000000; return p; } }
    public CardPopup()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual; KeyPreview = true; AutoScroll = true;
        AccessibleName = "Codex usage details";
        Controls.AddRange([Card, _refresh, _connect, _move]);
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();
        _connect.Click += (_, _) => ConnectRequested?.Invoke();
        _move.Click += (_, _) => MoveRequested?.Invoke();
        Card.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && !_interactive) Open(_anchor!, _below, true); };
        _watch.Tick += (_, _) => CheckPointer();
        Deactivate += (_, _) => { if (_interactive) Dismiss(); };
        DpiChanged += (_, _) => { if (!_layout) Present(_state, _theme, _recovery); };
    }
    public void Open(Func<Rectangle> anchor, bool below, bool interactive)
    {
        _anchor = anchor; _below = below; _interactive = interactive; _leftAt = null;
        UpdateStyles();
        Location = anchor().Location; // Let Windows select the anchor monitor before measuring fonts.
        Present(_state, _theme, _recovery);
        Show();
        Present(_state, _theme, _recovery);
        if (interactive) { TrayHost.SetForegroundWindow(Handle); Activate(); _refresh.Focus(); }
        else _watch.Start();
    }
    public void Present(UsageState state, AppTheme theme, RecoveryInfo? recovery = null)
    {
        if (_layout) return;
        _layout = true;
        try
        {
            var scroll = AutoScrollPosition;
            AutoScrollPosition = Point.Empty;
            _state = state; _theme = theme; _recovery = recovery;
            var scale = DeviceDpi / 96f;
            var textScale = AppTheme.TextScale;
            Card.Present(state, theme, scale, textScale, DateTimeOffset.UtcNow, recovery);
            _refresh.Visible = _connect.Visible = _move.Visible = _interactive;
            _refresh.Enabled = state.Status != ConnectionStatus.SigningIn;
            _connect.Enabled = state.Status != ConnectionStatus.SigningIn;
            _connect.Text = state.Status == ConnectionStatus.SigningIn ? "Signing in…" : "Connect ChatGPT";
            var fontSize = 12 * scale * textScale;
            if (_buttonFont is null || Math.Abs(_buttonFont.Size - fontSize) > .01f)
            {
                var oldFont = _buttonFont;
                _buttonFont = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                _refresh.Font = _connect.Font = _move.Font = _buttonFont;
                oldFont?.Dispose();
            }
            var padding = (int)(16 * scale);
            var buttonHeight = Math.Max((int)(34 * scale), _buttonFont.Height + padding);
            _refresh.SetBounds(padding, Card.Height + padding / 2, (Card.Width - padding * 3) / 2, buttonHeight);
            _connect.SetBounds(_refresh.Right + padding, _refresh.Top, _refresh.Width, buttonHeight);
            _move.SetBounds(padding, _refresh.Bottom + padding / 2, Card.Width - padding * 2, buttonHeight);
            var desired = new Size(Card.Width, Card.Height + (_interactive ? buttonHeight * 2 + padding * 2 : 0));
            var anchor = _anchor?.Invoke() ?? Bounds;
            var work = Screen.FromRectangle(anchor).WorkingArea;
            ClientSize = new Size(Math.Min(desired.Width, work.Width), Math.Min(desired.Height, work.Height));
            AutoScrollMinSize = desired;
            AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
            theme.Apply(this);
            using var path = Shape.Round(ClientRectangle, (int)(12 * scale));
            var previous = Region; Region = new Region(path); previous?.Dispose();
            if (_anchor is not null)
            {
                Location = _below ? new Point(Math.Clamp(anchor.Left + anchor.Width / 2 - Width / 2, work.Left, Math.Max(work.Left, work.Right - Width)), work.Top)
                    : PopupPlacement.Place(anchor, Size, work, (int)(8 * scale));
            }
        }
        finally { _layout = false; }
    }
    private void CheckPointer()
    {
        if (!Visible || _interactive) { _watch.Stop(); return; }
        if (Bounds.Contains(Cursor.Position) || (_anchor?.Invoke().Contains(Cursor.Position) ?? false)) { _leftAt = null; return; }
        _leftAt ??= Environment.TickCount64;
        if (Environment.TickCount64 - _leftAt >= 400) Dismiss();
    }
    public void Dismiss()
    {
        _watch.Stop();
        if (!Visible) return;
        Hide(); _interactive = false;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    { if (keyData == Keys.Escape) { Dismiss(); ReturnFocusRequested?.Invoke(); return true; } return base.ProcessCmdKey(ref msg, keyData); }
    protected override void Dispose(bool disposing)
    { if (disposing) { _watch.Dispose(); _buttonFont?.Dispose(); } base.Dispose(disposing); }
}

internal sealed class LoginDialog : Form
{
    private readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(420, 0), Text = "Complete sign-in in your browser. Your usage will appear automatically." };
    private readonly Button _open = new() { AutoSize = true, Text = "Open sign-in", TabIndex = 0 };
    private readonly Button _copy = new() { AutoSize = true, Text = "Copy sign-in link", TabIndex = 1 };
    private readonly Button _retry = new() { AutoSize = true, Text = "Try again", TabIndex = 2, Visible = false };
    private readonly Button _close = new() { AutoSize = true, Text = "Close", TabIndex = 3 };
    private Font? _font;
    private Uri? _url;
    public event Action? RetryRequested;
    public LoginDialog()
    {
        Text = "Connect ChatGPT"; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var layout = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24) };
        var title = new Label { Text = "Your ChatGPT allowance, at a glance", AutoSize = true, Margin = new Padding(0, 0, 0, 16) };
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 18, 0, 0) };
        actions.Controls.AddRange([_open, _copy, _retry, _close]);
        layout.Controls.AddRange([title, _detail, actions]); Controls.Add(layout);
        _open.Click += (_, _) => OpenBrowser();
        _copy.Click += (_, _) =>
        {
            if (_url is null) return;
            try { Clipboard.SetText(_url.AbsoluteUri); _detail.Text = "Link copied. Paste it into your browser to finish signing in."; }
            catch (ExternalException e) { Diagnostics.Write("clipboard", e); _detail.Text = "Clipboard is busy. Try copying again, or open the sign-in page."; }
        };
        _retry.Click += (_, _) => RetryRequested?.Invoke();
        _close.Click += (_, _) => Close();
        CancelButton = _close;
        UpdateTheme(AppTheme.Current);
    }
    public void UpdateTheme(AppTheme theme)
    {
        var size = 10 * AppTheme.TextScale;
        if (_font is null || Math.Abs(_font.Size - size) > .01f)
        {
            var old = _font;
            Font = _font = new Font("Segoe UI", size);
            old?.Dispose();
        }
        theme.Apply(this);
    }
    public void SetLogin(Uri url)
    { _url = url; _retry.Visible = false; _open.Enabled = _copy.Enabled = true; _detail.Text = "Complete sign-in in your browser. Your usage will appear automatically."; OpenBrowser(); }
    public void SetPending()
    { _url = null; _retry.Visible = false; _open.Enabled = _copy.Enabled = false; _detail.Text = "Requesting a sign-in link…"; }
    public void SetFailure()
    { _url = null; _open.Enabled = _copy.Enabled = false; _retry.Visible = true; _detail.Text = "Sign-in did not complete. Try again to open a new sign-in page."; }
    private void OpenBrowser()
    {
        if (_url is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Diagnostics.Write("browser-open", e); _detail.Text = "Windows could not open your browser. Copy the sign-in link and paste it into a browser."; }
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) _font?.Dispose(); }
}
