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
                Check(strip.Top == primaryWork.Top && strip.Left == primaryWork.Left + (primaryWork.Width - strip.Width) / 2,
                    $"strip starts at top center (actual {strip.Left},{strip.Top})");
                Check(GetForegroundWindow() == foregroundBeforeStrip, "strip startup does not take focus");
                strip.Hide(); strip.Show(); Pump(80);
                Check(strip.Top == primaryWork.Top && strip.Left == primaryWork.Left + (primaryWork.Width - strip.Width) / 2,
                    "strip stays at top center after hide/show");
            }
            using var tray = new TrayHost();
            tray.Update(TrayIconRenderer.Create(PreviewRenderer.Sample, AppTheme.Current, true), "Codex UI test");
            Check(tray.Registered, "native tray registered");
            var hovered = 0; var selected = 0; var context = 0; var closed = 0;
            tray.HoverOpened += () => hovered++; tray.Selected += () => selected++; tray.ContextRequested += () => context++; tray.HoverClosed += () => closed++;
            foreach (var code in new[] { 0x406, 0x407, 0x400, 0x401, 0x7B }) SendMessage(tray.Handle, TrayHost.Callback, IntPtr.Zero, (IntPtr)((1 << 16) | code));
            Check(hovered == 1 && closed == 1 && selected == 2 && context == 1, "version-4 mouse, keyboard and menu routing");
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
            Check(buttons.Length == 2 && buttons.All(x => x.Visible && x.TabStop), "actions are keyboard accessible");
            var refreshed = 0; popup.RefreshRequested += () => refreshed++;
            buttons.Single(x => x.Text == "Refresh").PerformClick(); Check(refreshed == 1, "refresh action routes once");
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
            popup.MaximumSize = new Size(180, 120);
            popup.Open(() => anchor, false, true); Pump(80);
            Check(popup.AutoScroll && popup.VerticalScroll.Visible && popup.HorizontalScroll.Visible, "oversized card offers scrolling");
            popup.ScrollControlIntoView(buttons[1]); Pump(80);
            Check(popup.ClientRectangle.IntersectsWith(buttons[1].Bounds), "actions remain reachable in a constrained viewport");
            popup.Dismiss();
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
    private static void TestApplication(Action<bool, string> check)
    {
        const string sample = "{\"rateLimits\":{\"primary\":{\"usedPercent\":62}}}";
        var initial = new Tests.FakeTransport(sample);
        var completed = new Tests.FakeTransport(sample)
        {
            AfterLogin = "{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"test-login\",\"success\":true}}"
        };
        var pending = new Tests.FakeTransport(sample) { HangLogin = true };
        var failed = new Tests.FakeTransport(sample) { LoginResult = "null" };
        var transports = new Queue<Tests.FakeTransport>([initial, completed, pending, failed]);
        // Keep synthetic hover callbacks independent of Shell overflow-icon placement.
        var work = Screen.PrimaryScreen!.WorkingArea;
        var icon = new Rectangle(work.Left + 100, work.Bottom - 100, 24, 24);
        using var context = new NotchApplicationContext(clientFactory: () => new CodexUsageClient(() => transports.Dequeue()), trayBounds: () => icon);
        UsageState State() => Field<UsageState>(context, "_state");
        var connect = Field<ToolStripMenuItem>(context, "_connect");
        try
        {
            Until(() => State().Status == ConnectionStatus.Ready);
            var tray = Field<TrayHost>(context, "_tray");
            var popup = Field<CardPopup>(context, "_popup");
            Cursor.Position = new Point(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2);
            SendMessage(tray.Handle, TrayHost.Callback, IntPtr.Zero, (IntPtr)((1 << 16) | 0x406));
            Pump(200);
            check(!popup.Visible, "passing over the tray does not immediately open the preview");
            SendMessage(tray.Handle, TrayHost.Callback, IntPtr.Zero, (IntPtr)((1 << 16) | 0x407));
            Pump(400);
            check(!popup.Visible, "leaving the tray cancels the pending preview");
            SendMessage(tray.Handle, TrayHost.Callback, IntPtr.Zero, (IntPtr)((1 << 16) | 0x406));
            Until(() => popup.Visible);
            check(popup.Visible && !popup.Interactive, "remaining over the tray opens the delayed preview");
            popup.Dismiss();
            SendMessage(tray.Handle, TrayHost.Callback, IntPtr.Zero, (IntPtr)((1 << 16) | 0x406));
            SendMessage(tray.Handle, TrayHost.Callback, IntPtr.Zero, (IntPtr)((1 << 16) | 0x400));
            check(popup.Visible && popup.Interactive, "clicking during the hover delay opens the interactive card immediately");
            popup.Dismiss();
            Pump(600);
            check(!popup.Visible, "clicking cancels the pending hover preview");
            var next = Field<RefreshSchedule>(context, "_schedule").Next;
            initial.Push("{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{\"primary\":{\"usedPercent\":80}}}}");
            Until(() => State().Snapshot?.Primary?.Remaining == 20);
            check(Field<RefreshSchedule>(context, "_schedule").Next == next, "usage notification preserves the periodic full-read deadline");
            connect.PerformClick();
            Until(() => completed.Methods.Contains("account/rateLimits/read") && State().Status == ConnectionStatus.Ready);
            check(Field<LoginDialog?>(context, "_login") is null, "immediate sign-in success closes the dialog and refreshes usage");
            connect.PerformClick();
            Until(() => pending.Methods.Contains("account/login/start"));
            Field<LoginDialog>(context, "_login").Close();
            Until(() => pending.Disposed && State().Status == ConnectionStatus.NeedsLogin);
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
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);
}
