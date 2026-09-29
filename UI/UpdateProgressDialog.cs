namespace CodexUsageNotch;

internal sealed class UpdateProgressDialog : Form
{
    private readonly Label _status = new() { AutoSize = true, Text = "Downloading update…" };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };
    public event Action? CancelRequested;

    public UpdateProgressDialog(Version version, AppTheme theme)
    {
        Text = $"Updating to v{version.ToString(3)}";
        AccessibleName = Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(380, 120);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_progress, 0, 1);
        layout.Controls.Add(_cancel, 0, 2);
        Controls.Add(layout);
        _cancel.Anchor = AnchorStyles.Right;
        _cancel.Click += (_, _) => RequestCancel();
        FormClosing += (_, e) => { if (_cancel.Enabled && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; RequestCancel(); } };
        theme.Apply(this);
    }

    public void SetProgress(int percent)
    {
        if (IsDisposed) return;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = Math.Clamp(percent, 0, 100);
        _status.Text = $"Downloading update… {_progress.Value}%";
    }

    public void Finish() { _cancel.Enabled = false; Close(); }

    private void RequestCancel()
    {
        _cancel.Enabled = false;
        _status.Text = "Cancelling download…";
        CancelRequested?.Invoke();
    }
}
