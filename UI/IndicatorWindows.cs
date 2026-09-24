using System.Runtime.InteropServices;

namespace CodexUsageNotch;

internal sealed class StripForm : Form
{
    private readonly System.Windows.Forms.Timer _hover = new() { Interval = 850 };
    private UsageState _state = UsageState.Initial;
    private AppTheme _theme = AppTheme.Current;
    public event Action? HoverRequested;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    { get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; return p; } }
    public StripForm()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
        AccessibleName = "Codex usage indicator";
        _hover.Tick += (_, _) => { _hover.Stop(); if (Bounds.Contains(Cursor.Position)) HoverRequested?.Invoke(); };
        MouseEnter += (_, _) => _hover.Start();
        MouseLeave += (_, _) => _hover.Stop();
        MouseDown += (_, _) => HoverRequested?.Invoke();
        DpiChanged += (_, _) => Reposition();
        Shown += (_, _) => { Reposition(); RestoreTopMost(); };
        Reposition();
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
        Location = new Point(screen.WorkingArea.Left + (screen.WorkingArea.Width - Width) / 2, screen.WorkingArea.Top);
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

internal sealed class CardPopup : Form
{
    internal readonly UsageCard Card = new();
    private readonly Button _refresh = new() { Text = "Refresh", AccessibleName = "Refresh usage", TabIndex = 0 };
    private readonly Button _connect = new() { Text = "Connect ChatGPT", TabIndex = 1 };
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 50 };
    private Func<Rectangle>? _anchor;
    private bool _below;
    private bool _interactive;
    private long? _leftAt;
    private UsageState _state = UsageState.Initial;
    private AppTheme _theme = AppTheme.Current;
    private Font? _buttonFont;
    private bool _layout;
    public event Action? RefreshRequested;
    public event Action? ConnectRequested;
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
        Controls.AddRange([Card, _refresh, _connect]);
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();
        _connect.Click += (_, _) => ConnectRequested?.Invoke();
        Card.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && !_interactive) Open(_anchor!, _below, true); };
        _watch.Tick += (_, _) => CheckPointer();
        Deactivate += (_, _) => { if (_interactive) Dismiss(); };
        DpiChanged += (_, _) => { if (!_layout) Present(_state, _theme); };
    }
    public void Open(Func<Rectangle> anchor, bool below, bool interactive)
    {
        _anchor = anchor; _below = below; _interactive = interactive; _leftAt = null;
        UpdateStyles();
        Location = anchor().Location; // Let Windows select the anchor monitor before measuring fonts.
        Present(_state, _theme);
        Show();
        Present(_state, _theme);
        if (interactive) { TrayHost.SetForegroundWindow(Handle); Activate(); _refresh.Focus(); }
        else _watch.Start();
    }
    public void Present(UsageState state, AppTheme theme)
    {
        if (_layout) return;
        _layout = true;
        try
        {
            var scroll = AutoScrollPosition;
            AutoScrollPosition = Point.Empty;
            _state = state; _theme = theme;
            var scale = DeviceDpi / 96f;
            var textScale = AppTheme.TextScale;
            Card.Present(state, theme, scale, textScale, DateTimeOffset.UtcNow);
            _refresh.Visible = _connect.Visible = _interactive;
            _refresh.Enabled = state.Status != ConnectionStatus.SigningIn;
            _connect.Enabled = state.Status != ConnectionStatus.SigningIn;
            _connect.Text = state.Status == ConnectionStatus.SigningIn ? "Signing in…" : "Connect ChatGPT";
            var fontSize = 12 * scale * textScale;
            if (_buttonFont is null || Math.Abs(_buttonFont.Size - fontSize) > .01f)
            {
                var oldFont = _buttonFont;
                _buttonFont = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                _refresh.Font = _connect.Font = _buttonFont;
                oldFont?.Dispose();
            }
            var padding = (int)(16 * scale);
            var buttonHeight = Math.Max((int)(34 * scale), _buttonFont.Height + padding);
            _refresh.SetBounds(padding, Card.Height + padding / 2, (Card.Width - padding * 3) / 2, buttonHeight);
            _connect.SetBounds(_refresh.Right + padding, _refresh.Top, _refresh.Width, buttonHeight);
            var desired = new Size(Card.Width, Card.Height + (_interactive ? buttonHeight + padding * 2 : 0));
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
