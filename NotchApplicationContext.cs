namespace CodexUsageNotch;

internal sealed class NotchApplicationContext : ApplicationContext
{
    private readonly Control _dispatch = new();
    private readonly StripForm _strip = new();
    private readonly CardPopup _popup = new();
    private readonly TrayHost _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly UserPreferences _preferences = new();
    private readonly RefreshSchedule _schedule = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ToolStripMenuItem _visibility = new("Show top indicator");
    private readonly ToolStripMenuItem _ring = new("Tray usage ring");
    private readonly ToolStripMenuItem _startup = new("Start with Windows");
    private readonly ToolStripMenuItem _connect = new("Connect ChatGPT…");
    private readonly ToolStripMenuItem _refresh = new("Refresh now");
    private readonly ToolStripMenuItem _move = new("Move top indicator…");
    private readonly string? _smokeReport;
    private readonly Func<CodexUsageClient> _clientFactory;
    private readonly Func<Rectangle> _trayBounds;
    private CodexUsageClient? _client;
    private LoginDialog? _login;
    private PositionDialog? _positionDialog;
    private string? _loginId;
    private DateTimeOffset? _loginStarted;
    private int _loginAttempt;
    private UsageState _state = UsageState.Initial;
    private AppTheme _theme = AppTheme.Current;
    private DateTimeOffset _nextPaint;
    private DateTimeOffset _nextTopMost;
    private bool _busy;
    private bool _exit;
    private bool _disposed;
    private bool _autoLoginOffered;
    private int _authFailures;
    private string _retryCause = "The allowance request through Codex did not complete.";
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
        _strip.Position = _preferences.StripPosition;
        _ring.Checked = _preferences.RingEnabled;
        _startup.Checked = smokeReport is null && StartupRegistration.IsEnabled();
        _refresh.Click += (_, _) => StartRefresh();
        _move.Click += (_, _) => OpenPositionDialog();
        _connect.Click += (_, _) => StartLogin();
        _visibility.Click += (_, _) =>
        {
            _preferences.StripVisible = !_preferences.StripVisible;
            _visibility.Checked = _preferences.StripVisible;
            if (_preferences.StripVisible) { _strip.Reposition(); _strip.Show(); _strip.RestoreTopMost(); } else _strip.Hide();
            SavePreferences();
        };
        _ring.Click += (_, _) => { _preferences.RingEnabled = !_preferences.RingEnabled; _ring.Checked = _preferences.RingEnabled; SavePreferences(); Present(); };
        _startup.Click += (_, _) =>
        {
            if (StartupRegistration.TrySetEnabled(!_startup.Checked)) _startup.Checked = !_startup.Checked;
            else MessageBox.Show("Windows could not update your startup preference.", "Codex Usage Notch", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        var exit = new ToolStripMenuItem("Exit"); exit.Click += async (_, _) => await ExitAsync();
        var version = new ToolStripLabel($"Version {typeof(NotchApplicationContext).Assembly.GetName().Version?.ToString(3) ?? "unknown"}");
        _menu.Items.AddRange([_refresh, _move, _connect, new ToolStripSeparator(), _visibility, _ring, _startup, new ToolStripSeparator(), version, exit]);
        _menu.Closed += (_, _) => _tray.ReturnFocus();
        _tray.Selected += () =>
        {
            if (_popup.Visible && _popup.Interactive) _popup.Dismiss(); else OpenPopup(true);
        };
        _tray.ContextRequested += () =>
        {
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
            if (_popup.Interactive || _menu.Visible || _positionDialog is not null) return;
            PresentPopup(); _popup.Open(() => _strip.Bounds, true, false);
        };
        _strip.Selected += () =>
        {
            if (_menu.Visible || _positionDialog is not null) return;
            PresentPopup();
            _popup.Open(() => _strip.Bounds, true, true);
        };
        _strip.DragStarted += () => _popup.Dismiss();
        _strip.PositionChanged += SetStripPosition;
        _popup.RefreshRequested += StartRefresh;
        _popup.ConnectRequested += () => { _popup.Dismiss(); StartLogin(); };
        _popup.MoveRequested += () => { _popup.Dismiss(); _dispatch.BeginInvoke(new Action(OpenPositionDialog)); };
        _popup.ReturnFocusRequested += () => _tray.ReturnFocus();
        _theme.Apply(_menu);
        _strip.Present(_state, _theme);
        if (_preferences.StripVisible) _strip.Show();
        _tick.Tick += (_, _) => Tick(); _tick.Start();
        Present();
        _dispatch.BeginInvoke(new Action(() =>
        {
            if (_smokeReport is null) { StartRefresh(); _ = CheckForUpdatesAsync(); _ = CleanupUpdateHelpersAsync(); }
            else
            {
                _state = PreviewRenderer.Sample;
                Present(); OpenPopup(false);
            }
        }));
    }
    private async Task CleanupUpdateHelpersAsync()
    {
        try { await Task.Delay(10000, _shutdown.Token); ReleaseUpdater.CleanupHelpers(); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { Diagnostics.Write("update-cleanup", error); }
    }
    private async Task CheckForUpdatesAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (_preferences.LastUpdateCheckUtc is { } last && now >= last && now - last < TimeSpan.FromDays(1)) return;
        ReleaseUpdate? update;
        try
        {
            var installed = typeof(NotchApplicationContext).Assembly.GetName().Version ?? new Version(0, 0, 0);
            update = await ReleaseUpdater.CheckAsync(Application.ExecutablePath, installed, _shutdown.Token);
            _preferences.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            if (!_preferences.Save()) Diagnostics.Write("update-check-setting");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            Diagnostics.Write("update-check", error);
            return;
        }
        if (_exit || update is null) return;
        var answer = MessageBox.Show($"Codex Usage Notch v{update.Version.ToString(3)} is available. Download and install it now?",
            "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes || _exit) return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        using var dialog = new UpdateProgressDialog(update.Version, _theme);
        dialog.CancelRequested += cancellation.Cancel;
        dialog.Show();
        string? staged = null;
        var handedOff = false;
        try
        {
            staged = await ReleaseUpdater.DownloadAsync(update, Application.ExecutablePath,
                new Progress<int>(dialog.SetProgress), cancellation.Token);
            if (_exit || cancellation.IsCancellationRequested) return;
            ReleaseUpdater.LaunchHelper(Application.ExecutablePath, staged, update.Sha256);
            handedOff = true;
            await ExitAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            Diagnostics.Write("update-download", error);
            dialog.Hide();
            MessageBox.Show("The update could not be installed. The current app is unchanged. Please try again later or download it from GitHub Releases.",
                "Codex Usage Notch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            dialog.Finish();
            if (!handedOff && staged is not null && File.Exists(staged))
            {
                try { File.Delete(staged); } catch (IOException) { }
            }
        }
    }
    private void SavePreferences()
    {
        if (!_preferences.Save()) MessageBox.Show("Your display preference works for this session, but could not be saved.", "Codex Usage Notch", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private void SetStripPosition(double position)
    {
        _strip.Position = position;
        _preferences.StripPosition = _strip.Position;
        SavePreferences();
    }
    private void OpenPositionDialog()
    {
        if (_exit || _positionDialog is not null) return;
        _popup.Dismiss(); _menu.Close();
        var originalPosition = _strip.Position;
        var wasVisible = _strip.Visible;
        if (!wasVisible) _strip.Show();
        _strip.RestoreTopMost();
        var work = (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
        using var dialog = new PositionDialog(originalPosition, work.Width - _strip.Width, _theme);
        _positionDialog = dialog;
        dialog.PositionPreviewed += position => _strip.Position = position;
        try
        {
            if (dialog.ShowDialog() == DialogResult.OK) SetStripPosition(dialog.Position);
            else _strip.Position = originalPosition;
        }
        finally
        {
            _positionDialog = null;
            if (!wasVisible) _strip.Hide();
        }
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
            var snapshot = read.Update.Apply(_state.Snapshot, read.Full);
            if (!read.Full) _schedule.Changed(snapshot);
            _state = new(snapshot, ConnectionStatus.Ready, DateTimeOffset.UtcNow);
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
            _retryCause = "The local Codex app-server connection closed.";
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
            _authFailures = 0;
            _schedule.Succeeded(read.Update.Apply(_state.Snapshot, true));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_exit) return;
            Diagnostics.Write("usage-refresh", error);
            _retryCause = RecoveryInfo.CauseFor(error);
            _schedule.Failed();
            if (error is RpcFailure { NeedsLogin: true } && ++_authFailures >= 2)
            {
                _state = new(null, ConnectionStatus.NeedsLogin, null);
                offerLogin = !_autoLoginOffered; _autoLoginOffered = true;
            }
            else
            {
                if (error is not RpcFailure { NeedsLogin: true }) _authFailures = 0;
                _state = _state with { Status = error is System.ComponentModel.Win32Exception { NativeErrorCode: 2 } ? ConnectionStatus.MissingCodex : ConnectionStatus.Retrying };
            }
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
            _authFailures = 0;
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
            var stripAtTopCenter = _strip.Location == StripPlacement.Place(work, _strip.Width, 0.5);
            File.WriteAllText(_smokeReport, $"trayRegistered={_tray.Registered}\nstripAtTopCenter={stripAtTopCenter}\ncardWidth={_popup.Card.Width}\ncardHeight={_popup.Card.Height}\npreviewInteractive={_popup.Interactive}\n");
            _ = ExitAsync(); return;
        }
        if (_loginStarted is { } began && DateTimeOffset.UtcNow - began > TimeSpan.FromMinutes(5))
        { _login?.SetFailure(); _operation = CancelLoginAsync(); }
        if (DateTimeOffset.UtcNow >= _nextPaint) { Present(); _nextPaint = DateTimeOffset.UtcNow.AddSeconds(30); }
        if (DateTimeOffset.UtcNow >= _nextTopMost)
        { _strip.RestoreTopMost(); _nextTopMost = DateTimeOffset.UtcNow.AddSeconds(5); }
        if (_schedule.Due && _state.Status is not (ConnectionStatus.NeedsLogin or ConnectionStatus.SigningIn or ConnectionStatus.LoginFailed)) StartRefresh();
    }
    private void OpenPopup(bool interactive)
    { PresentPopup(); _popup.Open(_trayBounds, false, interactive); }
    private void PresentPopup() => _popup.Present(_state, _theme, _state.Status == ConnectionStatus.Retrying
        ? new RecoveryInfo(_retryCause, _busy, _schedule.Next == DateTimeOffset.MinValue ? null : _schedule.Next)
        : null);
    private void Present()
    {
        _strip.Present(_state, _theme);
        if (_popup.Visible) PresentPopup();
        _refresh.Enabled = !_busy && _loginId is null;
        _connect.Enabled = !_busy;
        var text = UsageTooltip.Format(_state, DateTimeOffset.UtcNow);
        var iconTheme = AppTheme.Taskbar;
        var key = $"{text}|{_preferences.RingEnabled}|{iconTheme}";
        if (_iconKey != key)
        { _tray.Update(TrayIconRenderer.Create(_state, iconTheme, _preferences.RingEnabled), text); _iconKey = key; }
    }
    private void UpdateEnvironment()
    {
        _theme = AppTheme.Current; _theme.Apply(_menu);
        _login?.UpdateTheme(_theme);
        _positionDialog?.UpdateTheme(_theme);
        _strip.Reposition(); _strip.RestoreTopMost(); Present();
    }
    private void OnResumed()
    {
        if (_exit || _state.Status is ConnectionStatus.NeedsLogin or ConnectionStatus.SigningIn or ConnectionStatus.LoginFailed) return;
        _strip.RestoreTopMost();
        _retryCause = "Windows resumed. The allowance reading needs to be checked again.";
        _schedule.Now(); _state = _state with { Status = ConnectionStatus.Retrying }; Present(); StartRefresh();
    }
    internal async Task ExitAsync()
    {
        if (_exit) return;
        _exit = true; _tick.Stop(); _shutdown.Cancel();
        _popup.Dismiss(); _strip.Hide(); _menu.Close();
        _positionDialog?.Close();
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
            _tick.Dispose(); _login?.Dispose(); _popup.Dispose(); _strip.Dispose(); _menu.Dispose(); _tray.Dispose(); _dispatch.Dispose();
            _shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
}
