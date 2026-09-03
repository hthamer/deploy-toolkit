using System.Text.RegularExpressions;

namespace DeployToolkit.AppKit;

/// <summary>
/// Read-only scrolling log pane used by the Deployer shell for live run
/// output (plan §8.6 "live log pane") and by <see cref="RunLogViewerForm"/>
/// for replaying a finished run. Color-coded by level:
///  - INFO  → eye-friendly blue (steel blue #4682B4)
///  - WARN  → eye-friendly orange (dark orange #E8820E)
///  - ERROR → eye-friendly red (firebrick #CD5C5C)
///  - other → black
/// <see cref="AppendLine"/> is thread-safe: worker threads marshal through
/// <see cref="Control.BeginInvoke"/>. Line history is capped (~4000 lines).
/// </summary>
public sealed class LogPane : UserControl
{
    private const int MaxLines = 4000;
    private const int TrimTarget = 3000;

    private readonly RichTextBox _text;
    private int _lineCount;

    // Eye-friendly log level colors.
    private static readonly Color InfoColor = ColorTranslator.FromHtml("#4682B4");  // steel blue
    private static readonly Color WarnColor = ColorTranslator.FromHtml("#E8820E");  // dark orange
    private static readonly Color ErrorColor = ColorTranslator.FromHtml("#CD5C5C"); // firebrick
    private static readonly Color DefaultColor = Color.Black;

    public LogPane()
    {
        _text = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = false,
            BackColor = Color.White,
            Font = new Font("Consolas", 9f),
            TabStop = false,
            BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(_text);
    }

    /// <summary>Appends one line with color-coding by log level. Detects
    /// [INFO], [WARN], [ERROR] prefixes (from the RunLogger format) and
    /// colors accordingly. Safe to call from any thread.</summary>
    public void AppendLine(string line)
    {
        if (InvokeRequired)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action<string>(AppendLine), line ?? string.Empty);
            return;
        }

        var text = line ?? string.Empty;
        var color = GetColorForLine(text);

        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.SelectionColor = color;
        _text.AppendText(text + Environment.NewLine);
        _text.SelectionColor = DefaultColor;

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

    private static Color GetColorForLine(string line)
    {
        if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error", StringComparison.OrdinalIgnoreCase))
            return ErrorColor;

        if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("warning", StringComparison.OrdinalIgnoreCase))
            return WarnColor;

        if (line.Contains("[INFO]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[ OK ]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[PASS]", StringComparison.OrdinalIgnoreCase))
            return InfoColor;

        return DefaultColor;
    }

    private void AutoScrollToEnd()
    {
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
    }

    private void TrimHistory()
    {
        // RichTextBox doesn't support Lines setter well; use Select + Cut.
        if (_text.TextLength > TrimTarget * 80) // rough estimate: ~80 chars/line
        {
            var excessLen = _text.TextLength - TrimTarget * 80;
            _text.Select(0, excessLen);
            _text.Cut();
            _lineCount = TrimTarget;
        }
        AutoScrollToEnd();
    }
}
