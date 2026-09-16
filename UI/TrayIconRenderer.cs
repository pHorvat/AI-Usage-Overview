using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexUsageNotch;

internal static class TrayIconRenderer
{
    public static Icon Create(UsageState state, AppTheme theme, bool ring)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = state.IsStale(DateTimeOffset.UtcNow) || state.Snapshot?.Primary is null ? theme.Muted : theme.Accent(state.Snapshot.Primary.Remaining);
        if (ring)
        {
            using var track = new Pen(theme.Border, 3);
            using var fill = new Pen(color, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawEllipse(track, 2, 2, 28, 28);
            if (state.Snapshot?.Primary is { Remaining: > 0 } primary) graphics.DrawArc(fill, new Rectangle(2, 2, 28, 28), -90, primary.Remaining * 3.6f);
        }
        using var stream = typeof(TrayIconRenderer).Assembly.GetManifestResourceStream("CodexUsageNotch.Assets.CodexIcon.png")!;
        using var source = new Bitmap(stream);
        using var attributes = new ImageAttributes();
        // Preserve the supplied mark's alpha while adapting its ink to the Windows theme.
        var ink = theme.Foreground;
        attributes.SetColorMatrix(new ColorMatrix(new[]
        {
            new float[] {0,0,0,0,0}, new float[] {0,0,0,0,0}, new float[] {0,0,0,0,0},
            new float[] {0,0,0,1,0}, new float[] {ink.R / 255f,ink.G / 255f,ink.B / 255f,0,1}
        }));
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, ring ? new Rectangle(7, 7, 18, 18) : new Rectangle(3, 3, 26, 26), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
