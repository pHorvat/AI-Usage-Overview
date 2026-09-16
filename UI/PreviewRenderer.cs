using System.Drawing.Imaging;

namespace CodexUsageNotch;

internal static class PreviewRenderer
{
    public static UsageState Sample => new(new(new(62, DateTimeOffset.UtcNow.AddHours(3.5), 300),
        new(10, DateTimeOffset.UtcNow.AddDays(4), 10080), "plus"), ConnectionStatus.Ready, DateTimeOffset.UtcNow);
    public static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var states = new Dictionary<string, UsageState>
        {
            ["normal"] = Sample,
            ["empty"] = Sample with { Snapshot = new(new(100, now.AddMinutes(-1), 300), null, null) },
            ["full"] = Sample with { Snapshot = new(new(0, now.AddDays(120), 10080), new(0, null, 720), null) },
            ["stale"] = Sample with { Status = ConnectionStatus.Retrying, UpdatedAt = now.AddMinutes(-12) },
            ["connecting"] = UsageState.Initial,
            ["missing"] = new(null, ConnectionStatus.MissingCodex, null),
            ["login"] = new(null, ConnectionStatus.NeedsLogin, null),
            ["login-failed"] = new(null, ConnectionStatus.LoginFailed, null),
            ["signing-in"] = new(null, ConnectionStatus.SigningIn, null),
            ["no-windows"] = new(new(null, null, null), ConnectionStatus.Ready, now),
            ["secondary-only"] = new(new(null, new(25, now.AddDays(2), 10080), null), ConnectionStatus.Ready, now),
            ["long-duration"] = Sample with { Snapshot = new(new(0, now.AddYears(100), long.MaxValue), null, null) }
        };
        var report = new List<string>();
        foreach (var (name, theme) in new[] { ("light", AppTheme.Light), ("dark", AppTheme.Night), ("contrast", AppTheme.Contrast) })
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        foreach (var textScale in new[] { 1f, 2f, 2.25f })
        foreach (var (scenario, state) in states)
        {
            using var card = new UsageCard();
            card.Present(state, theme, scale, textScale, now);
            using var image = new Bitmap(card.Width, card.Height);
            card.DrawToBitmap(image, card.ClientRectangle);
            var file = $"{name}-{scale * 100:0}-text{textScale * 100:0}-{scenario}.png";
            image.Save(Path.Combine(directory, file), ImageFormat.Png);
            if (card.TextBounds.Any(rect => !card.ClientRectangle.Contains(rect))) throw new InvalidOperationException("Text exceeds card bounds: " + file);
            for (var i = 0; i < card.TextBounds.Count; i++)
            for (var j = i + 1; j < card.TextBounds.Count; j++)
                if (card.TextBounds[i].IntersectsWith(card.TextBounds[j])) throw new InvalidOperationException("Overlapping text: " + file);
            report.Add($"PASS {file} {card.Width}x{card.Height}");
        }
        File.WriteAllLines(Path.Combine(directory, "layout-checks.txt"), report);
    }
}
