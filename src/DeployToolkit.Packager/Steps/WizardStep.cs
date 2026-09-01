using DeployToolkit.AppKit;

namespace DeployToolkit.Packager;

/// <summary>
/// Base type for the seven plan §10 wizard steps. Each step owns its panel
/// content, declares when the user may proceed, and reports state changes to
/// the wizard (<c>Wizard.OnDraftChanged()</c>) so Back/Next/Finish stay
/// honest. All registry/IO work inside steps goes through
/// <see cref="Guard.RunAsync"/> (or the streaming publish path, which has
/// its own error surface).
/// </summary>
internal abstract class WizardStep : UserControl
{
    protected WizardStep(PackagerWizardForm wizard, PackageDraft draft)
    {
        Wizard = wizard;
        Draft = draft;
        Dock = DockStyle.Fill;
    }

    protected PackagerWizardForm Wizard { get; }

    protected PackageDraft Draft { get; }

    /// <summary>Step name shown in the left-hand step list.</summary>
    public abstract string Title { get; }

    /// <summary>One-line guidance shown in the wizard's bottom hint label.</summary>
    public abstract string Hint { get; }

    /// <summary>Whether Back/Next navigation may leave this step forward.</summary>
    public abstract bool CanProceed { get; }

    /// <summary>Called when the step becomes active (each time it is entered).</summary>
    public virtual void OnEnter()
    {
    }

    /// <summary>Called before the step stops being active.</summary>
    public virtual void OnLeave()
    {
    }

    // ---------------------------------------------------------------
    // Small shared layout helpers (the steps share the same look).

    /// <summary>Short commit-SHA rendering shared by several steps.</summary>
    protected static string ShortSha(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha.Length <= 12 ? sha : sha[..12];

    protected static TableLayoutPanel MakeFieldLayout(int labelWidth = 150)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    protected static void AddField(TableLayoutPanel layout, ref int row, string label, Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 6, 8, 2),
        }, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(2, 2, 12, 6);
        layout.Controls.Add(control, 1, row);
        row++;
    }
}
