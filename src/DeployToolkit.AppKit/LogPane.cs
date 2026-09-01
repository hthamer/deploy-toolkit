namespace DeployToolkit.AppKit;

/// <summary>
/// Read-only scrolling log pane used by the Deployer shell for live run
/// output (plan §8.6 "live log pane") and by <see cref="RunLogViewerForm"/>
/// for replaying a finished run. Light theme (white background, black text,
/// Consolas 9pt). <see cref="AppendLine"/> is thread-safe: worker threads
/// marshal through <see cref="Control.BeginInvoke"/>. Line history is capped
/// (~4000 lines) so a chatty deployment cannot grow memory without bound.
/// </summary>
public sealed class LogPane : UserControl
{
    private const int MaxLines = 4000;
    private const int TrimTarget = 3000; // trim down to this when the cap is hit

    private readonly TextBox _text;
    private int _lineCount;

    public LogPane()
    {
        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BackColor = Color.White,
            ForeColor = Color.Black,
            Font = new Font("Consolas", 9f),
            TabStop = false,
        };
        Controls.Add(_text);
    }

    /// <summary>Appends one line. Safe to call from any thread (once the
    /// handle exists); before the control is shown it appends directly.</summary>
    public void AppendLine(string line)
    {
        if (InvokeRequired)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action<string>(AppendLine), line ?? string.Empty);
            return;
        }

        _text.AppendText((line ?? string.Empty) + Environment.NewLine);
        _lineCount++;
        AutoScrollToEnd();

        if (_lineCount > MaxLines)
            TrimHistory();
    }

    /// <summary>Clears the pane. Safe to call from any thread.</summary>
    public void ClearAll()
    {
        if (InvokeRequired)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(ClearAll);
            return;
        }

        _text.Clear();
        _lineCount = 0;
    }

    private void AutoScrollToEnd()
    {
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
    }

    private void TrimHistory()
    {
        var lines = _text.Lines;
        if (lines.Length <= TrimTarget)
        {
            _lineCount = lines.Length;
            return;
        }

        _text.Lines = lines[^TrimTarget..];
        _lineCount = TrimTarget;
        AutoScrollToEnd();
    }
}
