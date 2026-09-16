using System.Drawing.Drawing2D;

namespace CodexUsageNotch;

internal sealed class UsageCard : Control
{
    private sealed record TextRun(string Text, Font Font, Rectangle Bounds, Color Color, TextFormatFlags Flags);
    private readonly List<TextRun> _runs = new();
    private readonly List<Font> _fonts = new();
    private Rectangle _progress;
    private Rectangle _dot;
    private UsageState _state = UsageState.Initial;
    private AppTheme _theme = AppTheme.Current;
    private float _scale = 1;
    private float _textScale = 1;
    private Color _accent;
    public IReadOnlyList<Rectangle> TextBounds => _runs.Select(x => x.Bounds).ToArray();
    public UsageCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = "Codex usage";
        TabStop = false;
    }
    public void Present(UsageState state, AppTheme theme, float scale, float textScale, DateTimeOffset now)
    {
        _state = state; _theme = theme; _scale = scale; _textScale = textScale;
        foreach (var font in _fonts) font.Dispose();
        _fonts.Clear(); _runs.Clear();
        BackColor = theme.Surface;
        var pad = Px(20);
        var width = Px(332 * Math.Max(1, textScale * .85f));
        var inner = width - pad * 2;
        var header = MakeFont(11, FontStyle.Bold);
        var body = MakeFont(12);
        var bold = MakeFont(12, FontStyle.Bold);
        var small = MakeFont(10.5f);
        var metric = MakeFont(36, FontStyle.Bold);
        var caption = MakeFont(10, FontStyle.Bold);
        using var graphics = CreateGraphics();
        var y = Px(17);
        _accent = state.IsStale(now) ? theme.Muted : state.Snapshot?.Primary is { } p ? theme.Accent(p.Remaining) : theme.Muted;
        _dot = new Rectangle(pad, y + Px(4), Px(6), Px(6));
        Add("CODEX USAGE", header, pad + Px(14), y, inner - Px(14), theme.Muted);
        y += HeightOf(header) + Px(7);
        if (state.Snapshot?.Primary is { } primary)
        {
            var value = primary.Remaining + "%";
            var size = TextRenderer.MeasureText(graphics, value, metric, Size.Empty, TextFormatFlags.NoPadding);
            Add(value, metric, pad, y, size.Width, theme.Foreground);
            Add(state.IsStale(now) ? "LAST KNOWN" : "AVAILABLE", caption, pad + size.Width + Px(9),
                y + size.Height - HeightOf(caption) - Px(8), inner - size.Width - Px(9), theme.Muted);
            y += size.Height + Px(7);
            _progress = new Rectangle(pad, y, inner, Px(6));
            y += Px(24);
            Row(UsageText.Window(primary), UsageText.Reset(primary.ResetsAt, now));
            y += Px(12);
            var secondary = state.Snapshot.Secondary;
            Row(secondary is null ? "Long window" : UsageText.Window(secondary), secondary is null ? "Not available" : $"{secondary.Remaining}% left");
            if (secondary?.ResetsAt is not null)
            {
                y += Px(4);
                y += Add(UsageText.Reset(secondary.ResetsAt, now), small, pad, y, inner, theme.Muted);
            }
            y += Px(16);
        }
        else
        {
            _progress = Rectangle.Empty;
            var title = state.Status switch
            {
                ConnectionStatus.MissingCodex => "Install Codex",
                ConnectionStatus.NeedsLogin or ConnectionStatus.SigningIn or ConnectionStatus.LoginFailed => "Connect ChatGPT",
                ConnectionStatus.Retrying => "Connection interrupted",
                ConnectionStatus.Ready => "Usage unavailable",
                _ => "Getting your usage"
            };
            y += Add(title, MakeFont(20, FontStyle.Bold), pad, y + Px(4), inner, theme.Foreground) + Px(18);
            if (state.Snapshot?.Secondary is { } secondary)
            {
                Row(UsageText.Window(secondary), $"{secondary.Remaining}% left");
                y += Px(4);
                y += Add(UsageText.Reset(secondary.ResetsAt, now), small, pad, y, inner, theme.Muted) + Px(16);
            }
        }
        y += Add(state.StatusText(now), small, pad, y, inner, theme.Muted);
        Size = new Size(width, y + Px(18));
        AccessibleDescription = string.Join(". ", _runs.Select(x => x.Text));
        Invalidate();

        int HeightOf(Font font) => TextRenderer.MeasureText(graphics, "Ag", font, Size.Empty, TextFormatFlags.NoPadding).Height;
        int Add(string text, Font font, int x, int top, int available, Color color)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
            var measured = TextRenderer.MeasureText(graphics, text, font, new Size(Math.Max(1, available), int.MaxValue), flags);
            _runs.Add(new(text, font, new Rectangle(x, top, Math.Max(1, available), measured.Height), color, flags));
            return measured.Height;
        }
        void Row(string label, string value)
        {
            var a = TextRenderer.MeasureText(graphics, label, body, Size.Empty, TextFormatFlags.NoPadding);
            var b = TextRenderer.MeasureText(graphics, value, bold, Size.Empty, TextFormatFlags.NoPadding);
            if (a.Width + b.Width + Px(14) <= inner)
            {
                Add(label, body, pad, y, a.Width, theme.Muted);
                Add(value, bold, width - pad - b.Width, y, b.Width, theme.Foreground);
                y += Math.Max(a.Height, b.Height);
            }
            else
            {
                y += Add(label, body, pad, y, inner, theme.Muted) + Px(4);
                y += Add(value, bold, pad, y, inner, theme.Foreground);
            }
        }
    }
    private Font MakeFont(float size, FontStyle style = FontStyle.Regular)
    { var font = new Font("Segoe UI", size * _scale * _textScale, style, GraphicsUnit.Pixel); _fonts.Add(font); return font; }
    private int Px(float value) => Math.Max(1, (int)Math.Round(value * _scale));
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_theme.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var dot = new SolidBrush(_accent);
        e.Graphics.FillEllipse(dot, _dot);
        if (!_progress.IsEmpty && _state.Snapshot?.Primary is { } primary)
        {
            using var track = new SolidBrush(_theme.Track);
            using var path = Shape.Round(_progress, _progress.Height / 2);
            e.Graphics.FillPath(track, path);
            if (primary.Remaining > 0)
            {
                var fill = _progress with { Width = Math.Max(1, (int)Math.Round(_progress.Width * primary.Remaining / 100.0)) };
                using var filled = Shape.Round(fill, Math.Min(fill.Height, fill.Width) / 2);
                e.Graphics.FillPath(dot, filled);
            }
        }
        foreach (var run in _runs) TextRenderer.DrawText(e.Graphics, run.Text, run.Font, run.Bounds, run.Color, run.Flags);
        using var border = new Pen(_theme.Border);
        using var outline = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), Px(12));
        e.Graphics.DrawPath(border, outline);
    }
    protected override void Dispose(bool disposing)
    { if (disposing) foreach (var font in _fonts) font.Dispose(); base.Dispose(disposing); }
}

internal static class Shape
{
    public static GraphicsPath Round(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
        if (diameter <= 1) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
