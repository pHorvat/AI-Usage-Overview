using Microsoft.Win32;

namespace CodexUsageNotch;

internal sealed record AppTheme(Color Surface, Color Foreground, Color Muted, Color Border, Color Track, bool Dark, bool HighContrast = false)
{
    public static AppTheme Light => new(Color.FromArgb(250, 251, 252), Color.FromArgb(24, 32, 40), Color.FromArgb(87, 101, 112), Color.FromArgb(215, 223, 228), Color.FromArgb(225, 232, 237), false);
    public static AppTheme Night => new(Color.FromArgb(16, 22, 29), Color.FromArgb(240, 246, 249), Color.FromArgb(157, 175, 186), Color.FromArgb(48, 63, 75), Color.FromArgb(43, 57, 69), true);
    public static AppTheme Contrast => new(SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText, SystemColors.WindowText, SystemColors.ControlDark, false, true);
    public static AppTheme Current => ReadWindowsTheme("AppsUseLightTheme");
    public static AppTheme Taskbar => ReadWindowsTheme("SystemUsesLightTheme");
    private static AppTheme ReadWindowsTheme(string setting)
    {
        if (SystemInformation.HighContrast) return Contrast;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(setting) is int n && n == 0 ? Night : Light;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return Light; }
    }
    public static float TextScale
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
                return key?.GetValue("TextScaleFactor") is int n ? Math.Clamp(n / 100f, 1, 2.25f) : 1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return 1; }
        }
    }
    public Color Accent(int remaining) => HighContrast ? SystemColors.Highlight : remaining <= 10
        ? Dark ? Color.FromArgb(255, 126, 120) : Color.FromArgb(182, 44, 44)
        : remaining <= 25 ? Dark ? Color.FromArgb(241, 188, 88) : Color.FromArgb(144, 92, 10)
        : Dark ? Color.FromArgb(87, 219, 193) : Color.FromArgb(0, 122, 102);
    public void Apply(Control control)
    {
        control.BackColor = Surface;
        control.ForeColor = Foreground;
        foreach (Control child in control.Controls) Apply(child);
        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Track;
        }
    }
    public void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = HighContrast ? new ToolStripSystemRenderer() : new ToolStripProfessionalRenderer(new ThemeTable(this));
        menu.BackColor = Surface;
        menu.ForeColor = Foreground;
        foreach (ToolStripItem item in menu.Items) item.ForeColor = Foreground;
    }
    private sealed class ThemeTable(AppTheme theme) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => theme.Surface;
        public override Color ImageMarginGradientBegin => theme.Surface;
        public override Color ImageMarginGradientMiddle => theme.Surface;
        public override Color ImageMarginGradientEnd => theme.Surface;
        public override Color MenuItemSelected => theme.Track;
        public override Color MenuItemBorder => theme.Border;
        public override Color MenuBorder => theme.Border;
        public override Color SeparatorDark => theme.Border;
        public override Color SeparatorLight => theme.Surface;
        public override Color CheckBackground => theme.Track;
        public override Color CheckSelectedBackground => theme.Track;
    }
}
