using DeployToolkit.Core.Logging;

namespace DeployToolkit.AppKit;

/// <summary>
/// Modal viewer for a finished deployment run's JSON-lines log file
/// (plan §8.6 audit trail). Shows every entry as
/// "Timestamp | Level | Message" in a <see cref="LogPane"/>. Missing or
/// unreadable files are reported inside the pane (never thrown).
/// </summary>
public sealed class RunLogViewerForm : Form
{
    public RunLogViewerForm(string logFilePath)
    {
        Text = $"Run log — {Path.GetFileName(logFilePath)}";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        Size = new Size(920, 620);
        AppTheme.Apply(this);

        var pane = new LogPane { Dock = DockStyle.Fill };

        var closeButton = new Button { Text = "Close", DialogResult = DialogResult.Cancel };
        AppTheme.StyleButton(closeButton);
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
            Height = 44,
        };
        bottom.Controls.Add(closeButton);

        Controls.Add(pane);
        Controls.Add(bottom);

        CancelButton = closeButton;

        try
        {
            var entries = RunLogger.ReadLog(logFilePath);
            if (entries.Count == 0)
            {
                pane.AppendLine($"(no log entries found in '{logFilePath}')");
            }
            foreach (var entry in entries)
                pane.AppendLine(
                    $"{entry.TimestampUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} | {entry.Level,-5} | {entry.Message}");
        }
        catch (Exception ex)
        {
            pane.AppendLine($"Could not read log file '{logFilePath}': {ex.Message}");
        }
    }
}
