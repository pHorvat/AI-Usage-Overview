namespace CodexUsageNotch;

internal sealed class NotchApplicationContext : ApplicationContext
{
    private readonly Control _dispatch = new();
    private readonly StripForm _strip = new();
    private readonly CardPopup _popup = new();
    private readonly TrayHost _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _trayHoverDelay = new() { Interval = 500 };
    private readonly UserPreferences _preferences = new();
    private readonly RefreshSchedule _schedule = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ToolStripMenuItem _visibility = new("Show top indicator");
    private readonly ToolStripMenuItem _ring = new("Tray usage ring");
    private readonly ToolStripMenuItem _startup = new("Start with Windows");
    private readonly ToolStripMenuItem _connect = new("Connect ChatGPT…");
    private readonly ToolStripMenuItem _refresh = new("Refresh now");
    private readonly string? _smokeReport;
    private readonly Func<CodexUsageClient> _clientFactory;
    private readonly Func<Rectangle> _trayBounds;
    private CodexUsageClient? _client;
    private LoginDialog? _login;
    private string? _loginId;
    private DateTimeOffset? _loginStarted;
    private int _loginAttempt;
    private UsageState _state = UsageState.Initial;
    private AppTheme _theme = AppTheme.Current;
    private DateTimeOffset _nextPaint;
    private bool _busy;
    private bool _exit;
    private bool _disposed;
    private bool _autoLoginOffered;
    private string? _iconKey;
    private Task _operation = Task.CompletedTask;
    private Task _loginCompletion = Task.CompletedTask;

    public NotchApplicationContext(string? smokeReport = null, Func<CodexUsageClient>? clientFactory = null, Func<Rectangle>? trayBounds = null)
    {
        _smokeReport = smokeReport;
        _clientFactory = clientFactory ?? (() => new CodexUsageClient());
        _trayBounds = trayBounds ?? _tray.IconBounds;
        _dispatch.CreateControl();
        if (smokeReport is null) _preferences.Load();
        _visibility.Checked = _preferences.StripVisible;
        _ring.Checked = _preferences.RingEnabled;
        _startup.Checked = smokeReport is null && StartupRegistration.IsEnabled();
        _refresh.Click += (_, _) => StartRefresh();
        _connect.Click += (_, _) => StartLogin();
        _visibility.Click += (_, _) =>
        {
            _preferences.StripVisible = !_preferences.StripVisible;
            _visibility.Checked = _preferences.StripVisible;
            if (_preferences.StripVisible) { _strip.Reposition(); _strip.Show(); } else _strip.Hide();
            SavePreferences();
        };
        _ring.Click += (_, _) => { _preferences.RingEnabled = !_preferences.RingEnabled; _ring.Checked = _preferences.RingEnabled; SavePreferences(); Present(); };
        _startup.Click += (_, _) =>
        {
            if (StartupRegistration.TrySetEnabled(!_startup.Checked)) _startup.Checked = !_startup.Checked;
            else MessageBox.Show("Windows could not update your startup preference.", "Codex Usage Notch", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        var exit = new ToolStripMenuItem("Exit"); exit.Click += async (_, _) => await ExitAsync();
        _menu.Items.AddRange([_refresh, _connect, new ToolStripSeparator(), _visibility, _ring, _startup, new ToolStripSeparator(), exit]);
        _menu.Closed += (_, _) => _tray.ReturnFocus();
        _trayHoverDelay.Tick += (_, _) =>
        {
            _trayHoverDelay.Stop();
            if (!_exit && !_menu.Visible && !_popup.Interactive && _trayBounds().Contains(Cursor.Position)) OpenPopup(false);
        };
        _tray.HoverOpened += () =>
        {
            if (!_exit && !_menu.Visible && !_popup.Visible && !_trayHoverDelay.Enabled) _trayHoverDelay.Start();
        };
        _tray.HoverClosed += () => _trayHoverDelay.Stop();
        _tray.Selected += () =>
        {
            _trayHoverDelay.Stop();
            if (_popup.Visible && _popup.Interactive) _popup.Dismiss(); else OpenPopup(true);
        };
        _tray.ContextRequested += () =>
        {
            _trayHoverDelay.Stop();
            _popup.Dismiss(); _tray.FocusHost();
            var anchor = _tray.IconBounds();
            var point = PopupPlacement.Place(anchor, _menu.GetPreferredSize(Size.Empty), Screen.FromRectangle(anchor).WorkingArea);
            _menu.Show(point);
        };
        _tray.EnvironmentChanged += UpdateEnvironment;
        _tray.Resumed += OnResumed;
        _tray.ExitRequested += () => _ = ExitAsync();
        _strip.HoverRequested += () =>
        {
            if (_popup.Interactive || _menu.Visible) return;
            _popup.Present(_state, _theme); _popup.Open(() => _strip.Bounds, true, false);
        };
        _popup.RefreshRequested += StartRefresh;
        _popup.ConnectRequested += () => { _popup.Dismiss(); StartLogin(); };
        _popup.ReturnFocusRequested += () => _tray.ReturnFocus();
        _theme.Apply(_menu);
        _strip.Present(_state, _theme);
        if (_preferences.StripVisible) _strip.Show();
        _tick.Tick += (_, _) => Tick(); _tick.Start();
        Present();
        _dispatch.BeginInvoke(new Action(() =>
        {
            if (_smokeReport is null) StartRefresh();
            else
            {
                _state = PreviewRenderer.Sample;
                Present(); OpenPopup(false);
            }
        }));
    }
    private void SavePreferences()
    {
        if (!_preferences.Save()) MessageBox.Show("Your display preference works for this session, but could not be saved.", "Codex Usage Notch", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private void Post(Action action)
    {
        if (_exit || _dispatch.IsDisposed) return;
        try { _dispatch.BeginInvoke(new Action(() => { if (!_exit) action(); })); }
        catch (InvalidOperationException) { }
    }
    private CodexUsageClient Client()
    {
        if (_client is not null) return _client;
        var client = _clientFactory();
        _client = client;
        client.UsageUpdated += read => Post(() =>
        {
            if (!ReferenceEquals(client, _client) || read.AccountGeneration != client.AccountGeneration || read.ConnectionGeneration != client.ConnectionGeneration || _loginId is not null) return;
            _state = new(read.Update.Apply(_state.Snapshot, read.Full), ConnectionStatus.Ready, DateTimeOffset.UtcNow);
            Present();
        });
        client.AccountChanged += generation => Post(() =>
        {
            if (!ReferenceEquals(client, _client) || generation != client.AccountGeneration) return;
            _state = new(null, _loginId is null ? ConnectionStatus.Connecting : ConnectionStatus.SigningIn, null);
            _schedule.Now(); Present();
        });
        client.Disconnected += generation => Post(() =>
        {
            if (!ReferenceEquals(client, _client) || generation != client.ConnectionGeneration) return;
            if (_loginId is not null || _state.Status is ConnectionStatus.NeedsLogin or ConnectionStatus.SigningIn or ConnectionStatus.LoginFailed) return;
            _state = _state with { Status = ConnectionStatus.Retrying };
            if (!_busy) _schedule.Failed();
            Present();
        });
        return client;
    }
    private void StartRefresh() { if (!_busy && !_exit && _loginId is null && _smokeReport is null) _operation = RefreshAsync(); }
    private async Task RefreshAsync()
    {
        _busy = true; Present();
        var offerLogin = false;
        var client = Client();
        try
        {
            var read = await client.ReadRateLimitsAsync(_shutdown.Token);
            if (_exit || !ReferenceEquals(client, _client) || read.AccountGeneration != client.AccountGeneration || read.ConnectionGeneration != client.ConnectionGeneration) return;
            if (!client.Connected) throw new IOException("Codex disconnected.");
            _schedule.Succeeded();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_exit) return;
            Diagnostics.Write("usage-refresh", error);
            _schedule.Failed();
            if (error is RpcFailure { NeedsLogin: true })
            {
                _state = new(null, ConnectionStatus.NeedsLogin, null);
                offerLogin = !_autoLoginOffered; _autoLoginOffered = true;
            }
            else _state = _state with { Status = error is System.ComponentModel.Win32Exception { NativeErrorCode: 2 } ? ConnectionStatus.MissingCodex : ConnectionStatus.Retrying };
        }
        finally { _busy = false; if (!_exit) Present(); }
        if (offerLogin) Post(StartLogin);
    }
    private void StartLogin()
    {
        if (_busy || _exit || _smokeReport is not null) return;
        if (_loginId is not null) { _login?.Activate(); return; }
        _operation = LoginAsync();
    }
    private async Task LoginAsync()
    {
        var attempt = ++_loginAttempt;
        _busy = true; _autoLoginOffered = true;
        _state = new(null, ConnectionStatus.SigningIn, null); Present();
        try
        {
            if (_login is null)
            {
                var dialog = new LoginDialog(); _login = dialog;
                dialog.RetryRequested += StartLogin;
                dialog.FormClosed += (_, _) =>
                {
                    if (!ReferenceEquals(_login, dialog)) return;
                    _login = null;
                    if (!_exit && (_loginId is not null || _busy)) _operation = CancelLoginAsync();
                };
            }
            _login.SetPending(); _login.Show();
            // A new subprocess isolates a cancelled/failed login and its delayed notifications.
            var old = _client; _client = null;
            if (old is not null) await old.DisposeAsync();
            if (_exit || attempt != _loginAttempt) return;
            var client = Client();
            var login = await client.StartLoginAsync(_shutdown.Token);
            if (_exit || attempt != _loginAttempt) return;
            _loginId = login.Id; _loginStarted = DateTimeOffset.UtcNow;
            if (!login.Completion.IsCompleted) _login.SetLogin(login.Url);
            _loginCompletion = ObserveLoginAsync(client, login);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_exit || attempt != _loginAttempt) return;
            Diagnostics.Write("login-start", error);
            _loginId = null; _loginStarted = null;
            _state = new(null, error is System.ComponentModel.Win32Exception { NativeErrorCode: 2 } ? ConnectionStatus.MissingCodex : ConnectionStatus.LoginFailed, null);
            _login?.SetFailure();
        }
        finally { if (attempt == _loginAttempt) _busy = false; if (!_exit) Present(); }
    }
    private async Task ObserveLoginAsync(CodexUsageClient client, LoginStart login)
    {
        var success = false;
        try { success = await login.Completion; }
        catch (OperationCanceledException) { }
        if (_exit || !ReferenceEquals(client, _client) || _loginId != login.Id) return;
        _loginId = null; _loginStarted = null;
        if (success)
        {
            _state = UsageState.Initial;
            var dialog = _login; _login = null; dialog?.Close(); dialog?.Dispose();
            _schedule.Now();
        }
        else
        {
            _state = UsageState.Initial with { Status = ConnectionStatus.LoginFailed };
            _login?.SetFailure(); _login?.Show();
        }
        Present();
    }
    private async Task CancelLoginAsync()
    {
        ++_loginAttempt;
        _busy = true; _loginId = null; _loginStarted = null;
        var client = _client; _client = null;
        try { if (client is not null) await client.DisposeAsync(); }
        finally { _busy = false; _state = new(null, ConnectionStatus.NeedsLogin, null); if (!_exit) Present(); }
    }
    private void Tick()
    {
        if (_exit) return;
        if (_smokeReport is not null)
        {
            var work = Screen.PrimaryScreen!.WorkingArea;
            var stripAtTopCenter = _strip.Top == work.Top && _strip.Left == work.Left + (work.Width - _strip.Width) / 2;
            File.WriteAllText(_smokeReport, $"trayRegistered={_tray.Registered}\nstripAtTopCenter={stripAtTopCenter}\ncardWidth={_popup.Card.Width}\ncardHeight={_popup.Card.Height}\npreviewInteractive={_popup.Interactive}\n");
            _ = ExitAsync(); return;
        }
        if (_loginStarted is { } began && DateTimeOffset.UtcNow - began > TimeSpan.FromMinutes(5))
        { _login?.SetFailure(); _operation = CancelLoginAsync(); }
        if (DateTimeOffset.UtcNow >= _nextPaint) { Present(); _nextPaint = DateTimeOffset.UtcNow.AddSeconds(30); }
        if (_schedule.Due && _state.Status is not (ConnectionStatus.NeedsLogin or ConnectionStatus.SigningIn or ConnectionStatus.LoginFailed)) StartRefresh();
    }
    private void OpenPopup(bool interactive)
    { _popup.Present(_state, _theme); _popup.Open(_trayBounds, false, interactive); }
    private void Present()
    {
        _strip.Present(_state, _theme);
        if (_popup.Visible) _popup.Present(_state, _theme);
        _refresh.Enabled = !_busy && _loginId is null;
        _connect.Enabled = !_busy;
        var primary = _state.Snapshot?.Primary;
        var stale = _state.IsStale(DateTimeOffset.UtcNow);
        var text = primary is null ? "Codex usage · " + _state.StatusText(DateTimeOffset.UtcNow)
            : $"Codex: {primary.Remaining}% left · {UsageText.Window(primary)}" + (stale ? " · last known" : "");
        var iconTheme = AppTheme.Taskbar;
        var key = $"{text}|{_preferences.RingEnabled}|{iconTheme}";
        if (_iconKey != key)
        { _tray.Update(TrayIconRenderer.Create(_state, iconTheme, _preferences.RingEnabled), text); _iconKey = key; }
    }
    private void UpdateEnvironment()
    {
        _theme = AppTheme.Current; _theme.Apply(_menu);
        _login?.UpdateTheme(_theme);
        _strip.Reposition(); Present();
    }
    private void OnResumed()
    {
        if (_exit || _state.Status is ConnectionStatus.NeedsLogin or ConnectionStatus.SigningIn or ConnectionStatus.LoginFailed) return;
        _schedule.Now(); _state = _state with { Status = ConnectionStatus.Retrying }; Present(); StartRefresh();
    }
    internal async Task ExitAsync()
    {
        if (_exit) return;
        _exit = true; _tick.Stop(); _trayHoverDelay.Stop(); _shutdown.Cancel();
        _popup.Dismiss(); _strip.Hide(); _menu.Close();
        _login?.Close();
        var client = _client; _client = null;
        try
        {
            if (client is not null) await client.DisposeAsync();
            await _operation;
            await _loginCompletion;
        }
        catch (Exception error) { Diagnostics.Write("shutdown", error); }
        finally { ExitThread(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _exit = true; _shutdown.Cancel();
            _client?.Abort();
            _tick.Dispose(); _trayHoverDelay.Dispose(); _login?.Dispose(); _popup.Dispose(); _strip.Dispose(); _menu.Dispose(); _tray.Dispose(); _dispatch.Dispose();
            _shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
}
