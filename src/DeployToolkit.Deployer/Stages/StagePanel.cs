using DeployToolkit.AppKit;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Base type for the plan §11 stage panels hosted by <see cref="MainForm"/>.
/// Each panel owns the content of one step (load → resolve → pre-flight →
/// backup → deploy); the bottom-bar stage buttons drive the flow and the
/// state machine on <c>MainForm</c> decides which buttons are enabled. All
/// registry/IO work inside stages goes through <see cref="Guard.RunAsync"/>
/// (or the deploy run, which has its own streaming error surface).
/// </summary>
internal abstract class StagePanel : UserControl
{
    protected StagePanel(MainForm shell)
    {
        Shell = shell;
        Dock = DockStyle.Fill;
    }

    protected MainForm Shell { get; }

    /// <summary>The package currently being deployed; null before step 1
    /// completes (stages must re-read this on every <see cref="OnEnter"/>
    /// — the context is recreated per package).</summary>
    protected DeploymentContext? Context => Shell.Context;

    /// <summary>Short step name (used in the run plan / documentation).</summary>
    public abstract string Title { get; }

    /// <summary>Called each time the panel becomes visible.</summary>
    public virtual void OnEnter()
    {
    }

    // ---------------------------------------------------------------
    // Small shared layout helpers (the stages share the same look).

    protected static TableLayoutPanel MakeVerticalLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
        };
        return layout;
    }

    protected static TextBox MakeReadOnlySummaryBox(int height)
    {
        return new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
            Dock = DockStyle.Fill,
            Height = height,
            TabStop = false,
        };
    }
}
