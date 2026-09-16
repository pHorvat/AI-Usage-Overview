using System.Security.Principal;

namespace CodexUsageNotch;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        if (args is ["--render-previews", var directory])
        {
            PreviewRenderer.Render(directory);
            return 0;
        }
        var smoke = args is ["--smoke-test", _];
        using var mutex = new Mutex(true, "Local\\CodexUsageNotch-" + WindowsIdentity.GetCurrent().User?.Value + (smoke ? "-smoke" : ""), out var first);
        if (!first) return 0;
        try
        {
            using var context = new NotchApplicationContext(smoke ? args[1] : null);
            var uiFailed = false;
            ThreadExceptionEventHandler handler = async (_, e) =>
            {
                uiFailed = true;
                Diagnostics.Write("ui-error", e.Exception);
                await context.ExitAsync();
            };
            Application.ThreadException += handler;
            try { Application.Run(context); }
            finally { Application.ThreadException -= handler; }
            if (uiFailed) return 1;
            return 0;
        }
        catch (Exception error)
        {
            Diagnostics.Write("application", error);
            if (smoke) File.AppendAllText(args[1], "error=" + error.GetType().Name + "\n" + error.StackTrace + "\n");
            if (!smoke) MessageBox.Show("Codex Usage Notch could not start. See diagnostics.log in %LocalAppData%\\CodexUsageNotch.",
                "Codex Usage Notch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
    }
}
