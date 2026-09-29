using CodexUsageNotch;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class UiTests
{
    public static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        var count = 0;
        var savedPointer = Cursor.Position;
        try
        {
            using (var strip = new StripForm())
            {
                var primaryWork = Screen.PrimaryScreen!.WorkingArea;
                var foregroundBeforeStrip = GetForegroundWindow();
                strip.Show(); Pump(80);
                Check(strip.Location == StripPlacement.Place(primaryWork, strip.Width, 0.5),
                    $"strip starts at top center (actual {strip.Left},{strip.Top})");
                strip.Position = 0;
                Check(strip.Left == primaryWork.Left && strip.Top == primaryWork.Top, "strip moves to the left edge");
                strip.Position = 1;
                Check(strip.Right == primaryWork.Right && strip.Top == primaryWork.Top, "strip moves to the right edge");
                strip.Position = 0.37;
                Check(strip.Left == StripPlacement.Place(primaryWork, strip.Width, 0.37).X, "strip accepts a custom top position");
                strip.Position = 0.5;
                var dragged = 0;
                strip.PositionChanged += _ => dragged++;
                var dragStart = new Point(strip.Left + strip.Width / 2, strip.Top + Math.Min(2, strip.Height - 1));
                strip.BeginDrag(dragStart);
                strip.ContinueDrag(new Point(dragStart.X + 180, dragStart.Y));
                strip.EndDrag(true);
                Check(strip.Position > 0.5 && dragged == 1, $"dragging moves and commits the strip position ({strip.Position:F3}, events {dragged})");
                var selections = 0;
                strip.Selected += () => selections++;
                strip.BeginDrag(dragStart);
                strip.EndDrag(true);
                Check(selections == 1, "clicking the strip selects the usage card");
                strip.Position = 0.5;
                Check(GetForegroundWindow() == foregroundBeforeStrip, "strip startup does not take focus");
                using var rival = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                    Location = new Point(primaryWork.Left + 20, primaryWork.Top + 20), Size = new Size(30, 30) };
                rival.Show(); Pump(80);
                var foregroundBeforeRestore = GetForegroundWindow();
                strip.RestoreTopMost(); Pump(80);
                Check(IsAbove(strip.Handle, rival.Handle), "strip returns above other topmost windows");
                Check(GetForegroundWindow() == foregroundBeforeRestore, "restoring strip order does not take focus");
                rival.Close();
                strip.Hide(); strip.Show(); Pump(80);
                Check(strip.Location == StripPlacement.Place(primaryWork, strip.Width, 0.5),
                    "strip stays at top center after hide/show");
            }
            using (var dialog = new PositionDialog(0.5, 1000, AppTheme.Current))
            {
                var slider = Field<TrackBar>(dialog, "_slider");
                var previewed = 0d;
                dialog.PositionPreviewed += position => previewed = position;
                slider.Value = 730;
                Check(dialog.Position == 0.73 && previewed == 0.73, "position slider previews a precise top location");
                Check(dialog.AcceptButton is Button && dialog.CancelButton is Button, "position dialog has save and cancel actions");
            }
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Night })
            {
                using var menu = new ContextMenuStrip();
                var item = new ToolStripMenuItem("Hover contrast");
                menu.Items.Add(item); theme.Apply(menu);
                menu.Show(new Point(100, 100)); item.Select(); Pump(30);
                using var image = new Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(image, new Rectangle(Point.Empty, menu.Size));
                var sampleColor = image.GetPixel(menu.Width - 5, item.Bounds.Top + item.Height / 2);
                Check(sampleColor.ToArgb() == theme.Track.ToArgb(), "menu hover uses a readable theme color");
                menu.Close();
            }
            using (var stripCard = new CardPopup())
            {
                var stripWork = Screen.PrimaryScreen!.WorkingArea;
                var stripAnchor = new Rectangle(stripWork.Left + stripWork.Width / 2 - 100, stripWork.Top, 200, 6);
                stripCard.Present(new UsageState(null, ConnectionStatus.Retrying, null), AppTheme.Current,
                    new RecoveryInfo("Codex did not answer the allowance request in time.", false, DateTimeOffset.UtcNow.AddSeconds(15)));
                stripCard.Open(() => stripAnchor, true, true); Pump(80);
                Check(stripCard.Card.AccessibleDescription!.Contains("check the Codex connection") &&
                    stripCard.Card.AccessibleDescription.Contains("account/rateLimits/read"),
                    "interrupted card explains the next step and request");
                var actions = stripCard.Controls.OfType<Button>().ToArray();
                var refresh = actions.Single(button => button.Text == "Refresh");
                var move = actions.Single(button => button.Text == "Move top indicator");
                Check(stripCard.Interactive && move.Visible && move.Top >= refresh.Bottom,
                    "clicked strip card shows Move top indicator below Refresh");
                Check(!stripCard.AutoScroll && !stripCard.VerticalScroll.Visible && !stripCard.HorizontalScroll.Visible,
                    "clicked top indicator card has no scrollbars");
                var requested = 0;
                stripCard.MoveRequested += () => requested++;
                move.PerformClick();
                Check(requested == 1, "strip card Move button requests position controls");
                stripCard.Dismiss();
            }
            using var tray = new TrayHost();
            tray.Update(TrayIconRenderer.Create(PreviewRenderer.Sample, AppTheme.Current, true), "Codex UI test");
            Check(tray.Registered, "native tray registered");
            var selected = 0; var context = 0;
            tray.Selected += () => selected++; tray.ContextRequested += () => context++;
            foreach (var code in new[] { 0x400, 0x401, 0x7B }) SendMessage(tray.Handle, TrayHost.Callback, IntPtr.Zero, (IntPtr)((1 << 16) | code));
            Check(selected == 2 && context == 1, "version-4 mouse, keyboard and menu routing");
            var resumed = 0; var environment = 0;
            tray.Resumed += () => resumed++; tray.EnvironmentChanged += () => environment++;
            SendMessage(tray.Handle, 0x0218, (IntPtr)0x12, IntPtr.Zero);
            SendMessage(tray.Handle, 0x0218, (IntPtr)0x07, IntPtr.Zero);
            SendMessage(tray.Handle, 0x007E, IntPtr.Zero, IntPtr.Zero);
            Check(resumed == 2 && environment == 1, "resume and display changes route through the native tray window");
            var work = Screen.FromPoint(savedPointer).WorkingArea;
            var anchor = new Rectangle(work.Right - 180, work.Bottom - 30, 24, 24);
            using var popup = new CardPopup();
            popup.Present(PreviewRenderer.Sample, AppTheme.Current);
            Cursor.Position = new Point(anchor.Left + 5, anchor.Top + 5);
            var foreground = GetForegroundWindow();
            popup.Open(() => anchor, false, false);
            Pump(80);
            Check(popup.Visible && !popup.Interactive && GetForegroundWindow() == foreground, "hover preview does not take focus");
            Check(work.Contains(popup.Bounds), "popup remains in work area");
            Cursor.Position = new Point(popup.Left + 20, popup.Top + 20); Pump(500);
            Check(popup.Visible, "pointer can cross from icon into card");
            Cursor.Position = new Point(work.Left + 10, work.Top + 40); Pump(200);
            Check(popup.Visible, "leave grace period");
            Until(() => !popup.Visible); Check(!popup.Visible, "preview dismisses after leaving");
            popup.Open(() => anchor, false, true); Pump(80);
            Check(popup.Visible && popup.Interactive && popup.ContainsFocus, "click opens keyboard-accessible card");
            var buttons = popup.Controls.OfType<Button>().ToArray();
            Check(buttons.Length == 3 && buttons.All(x => x.Visible && x.TabStop), "actions are keyboard accessible");
            var refreshed = 0; popup.RefreshRequested += () => refreshed++;
            buttons.Single(x => x.Text == "Refresh").PerformClick(); Check(refreshed == 1, "refresh action routes once");
            var moved = 0; popup.MoveRequested += () => moved++;
            var moveButton = buttons.Single(x => x.Text == "Move top indicator");
            Check(moveButton.Top >= buttons.Single(x => x.Text == "Refresh").Bottom, "move action sits below refresh");
            moveButton.PerformClick(); Check(moved == 1, "move action routes once");
            // Post to the focused control's message queue so WinForms runs key preprocessing.
            // SendKeys can target the invoking terminal when the desktop focus changes mid-test.
            PostMessage(popup.ActiveControl!.Handle, 0x100, (IntPtr)Keys.Escape, IntPtr.Zero);
            PostMessage(popup.ActiveControl!.Handle, 0x101, (IntPtr)Keys.Escape, IntPtr.Zero);
            Pump(80); Check(!popup.Visible, "Escape dismisses interactive card");
            popup.Open(() => anchor, false, true); Pump(80);
            using var outside = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(work.Left + 30, work.Top + 40), Size = new Size(100, 80) };
            outside.Show(); outside.Activate(); Pump(80);
            Check(!popup.Visible, "activation outside dismisses card");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Night, AppTheme.Contrast })
            {
                popup.Present(PreviewRenderer.Sample, theme);
                Check(popup.Card.BackColor == theme.Surface, "live theme updates card");
            }
            popup.MaximumSize = new Size(420, 400);
            foreach (var textScale in new[] { 1f, 2f, 2.25f })
            {
                popup.Present(new UsageState(null, ConnectionStatus.Retrying, null), AppTheme.Current,
                    new RecoveryInfo("Codex did not answer the allowance request in time.", false, DateTimeOffset.UtcNow.AddSeconds(15)), textScale);
                popup.Open(() => anchor, false, true); Pump(80);
                popup.Present(new UsageState(null, ConnectionStatus.Retrying, null), AppTheme.Current,
                    new RecoveryInfo("Codex did not answer the allowance request in time.", false, DateTimeOffset.UtcNow.AddSeconds(15)), textScale);
                Check(!popup.AutoScroll && !popup.VerticalScroll.Visible && !popup.HorizontalScroll.Visible,
                    $"popup has no scrollbars at {textScale:P0} text scaling");
                Check(popup.Width <= 420 && popup.Height <= 400 && popup.ClientRectangle.Contains(popup.Card.Bounds) &&
                    popup.Card.TextBounds.All(rect => popup.Card.ClientRectangle.Contains(rect)) &&
                    buttons.All(button => popup.ClientRectangle.Contains(button.Bounds)),
                    $"card and actions fit the constrained popup at {textScale:P0} text scaling");
                popup.Dismiss();
            }
            popup.Dismiss();
            using (var updateDialog = new UpdateProgressDialog(new Version(1, 2, 6), AppTheme.Current))
            {
                var cancellations = 0;
                updateDialog.CancelRequested += () => cancellations++;
                updateDialog.Show(); Pump(30);
                updateDialog.SetProgress(55);
                Check(Field<ProgressBar>(updateDialog, "_progress").Value == 55, "update dialog reports download progress");
                Field<Button>(updateDialog, "_cancel").PerformClick();
                Check(cancellations == 1, "update dialog allows cancelling the download");
                updateDialog.Finish();
                Check(!updateDialog.Visible, "update dialog closes after download completes");
            }
            using (var completedDialog = new UpdateProgressDialog(new Version(1, 2, 6), AppTheme.Current))
            {
                completedDialog.Show(); Pump(30);
                completedDialog.Finish();
                Check(!completedDialog.Visible, "completed update closes its progress dialog");
            }
            using var login = new LoginDialog();
            login.SetFailure(); login.Show(); Pump(80);
            var loginButtons = login.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().ToArray();
            Check(loginButtons.Single(x => x.Text == "Try again").Visible && !loginButtons.Single(x => x.Text == "Open sign-in").Enabled,
                "failed sign-in offers a retry and disables the expired link");
            login.UpdateTheme(AppTheme.Night);
            Check(login.BackColor == AppTheme.Night.Surface, "sign-in dialog follows theme changes");
            login.Close();
            TestApplication(Check);
            Console.WriteLine($"PASS: {count} UI checks"); return 0;
            void Check(bool condition, string message)
            { if (!condition) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { Cursor.Position = savedPointer; }
    }
    private static void Pump(int milliseconds)
    { var timer = Stopwatch.StartNew(); while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); } }
    private static void Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Application state did not settle.");
            Pump(10);
        }
    }
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(target)!;
    private static bool IsAbove(IntPtr higher, IntPtr lower)
    {
        for (var window = GetWindow(higher, 2); window != IntPtr.Zero; window = GetWindow(window, 2))
            if (window == lower) return true;
        return false;
    }
    private static void TestApplication(Action<bool, string> check)
    {
        const string sample = "{\"rateLimits\":{\"primary\":{\"usedPercent\":62}}}";
        var initial = new Tests.FakeTransport(sample)
        {
            AfterLogin = "{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"test-login\",\"success\":true}}"
        };
        var failed = new Tests.FakeTransport(sample) { LoginResult = "null" };
        var transports = new Queue<Tests.FakeTransport>([initial, failed]);
        // Keep synthetic hover callbacks independent of Shell overflow-icon placement.
        var work = Screen.PrimaryScreen!.WorkingArea;
        var icon = new Rectangle(work.Left + 100, work.Bottom - 100, 24, 24);
        using var context = new NotchApplicationContext(clientFactory: () => new CodexUsageClient(() => transports.Dequeue()), trayBounds: () => icon);
        UsageState State() => Field<UsageState>(context, "_state");
        var connect = Field<ToolStripMenuItem>(context, "_connect");
        try
        {
            var menu = Field<ContextMenuStrip>(context, "_menu");
            check(menu.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == "Move top indicator…"),
                "tray menu offers position controls next to refresh");
            check(menu.Items.OfType<ToolStripMenuItem>().All(item => item.Text != "Top indicator position"),
                "tray menu omits the redundant position submenu");
            check(menu.Items.OfType<ToolStripLabel>().Any(item => item.Text == $"Version {typeof(NotchApplicationContext).Assembly.GetName().Version?.ToString(3)}"),
                "tray menu displays the installed version");
            Until(() => State().Status == ConnectionStatus.Ready);
            var tray = Field<TrayHost>(context, "_tray");
            var popup = Field<CardPopup>(context, "_popup");
            Cursor.Position = new Point(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2);
            SendMessage(tray.Handle, TrayHost.Callback, IntPtr.Zero, (IntPtr)((1 << 16) | 0x406));
            Pump(650);
            check(!popup.Visible, "hovering over the tray leaves the native tooltip in control");
            SendMessage(tray.Handle, TrayHost.Callback, IntPtr.Zero, (IntPtr)((1 << 16) | 0x400));
            check(popup.Visible && popup.Interactive, "clicking the tray opens the interactive card");
            popup.Dismiss();
            var next = Field<RefreshSchedule>(context, "_schedule").Next;
            initial.Push("{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{\"primary\":{\"usedPercent\":80}}}}");
            Until(() => State().Snapshot?.Primary?.Remaining == 20);
            check(Field<RefreshSchedule>(context, "_schedule").Next == next, "usage notification preserves the periodic full-read deadline");
            initial.ReadErrorsRemaining = 1;
            Field<ToolStripMenuItem>(context, "_refresh").PerformClick();
            Until(() => State().Status == ConnectionStatus.Retrying);
            check(Field<LoginDialog?>(context, "_login") is null && !initial.Disposed,
                "one authentication error retries without restarting Codex or opening sign-in");
            Field<ToolStripMenuItem>(context, "_refresh").PerformClick();
            Until(() => State().Status == ConnectionStatus.Ready);
            connect.PerformClick();
            Until(() => initial.Methods.Count(method => method == "account/rateLimits/read") >= 4 && State().Status == ConnectionStatus.Ready);
            check(Field<LoginDialog?>(context, "_login") is null, "immediate sign-in success closes the dialog and refreshes usage");
            check(!initial.Disposed, "sign-in reuses the existing Codex connection");
            initial.AfterLogin = null;
            initial.HangLogin = true;
            connect.PerformClick();
            Until(() => initial.Methods.Count(method => method == "account/login/start") >= 2);
            Field<LoginDialog>(context, "_login").Close();
            Until(() => initial.Disposed && State().Status == ConnectionStatus.NeedsLogin);
            check(Field<LoginDialog?>(context, "_login") is null, "closing during a sign-in request cancels it without reopening the dialog");
            connect.PerformClick();
            Until(() => State().Status == ConnectionStatus.LoginFailed);
            check(Field<LoginDialog>(context, "_login").Visible, "a failed sign-in start leaves a visible retry dialog");
            SendMessage(Field<TrayHost>(context, "_tray").Handle, 0x0218, (IntPtr)0x12, IntPtr.Zero);
            check(State().Status == ConnectionStatus.LoginFailed, "resume preserves a failed sign-in until the user retries");
        }
        finally
        {
            var exit = context.ExitAsync(); Until(() => exit.IsCompleted); exit.GetAwaiter().GetResult();
            var timer = Stopwatch.StartNew(); context.Dispose();
            check(timer.Elapsed < TimeSpan.FromSeconds(2), "application disposal does not block the desktop thread");
        }
        check(failed.Disposed, "application shutdown disposes its connection");
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);
}
