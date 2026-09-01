using DeployToolkit.AppKit;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 4 (plan §10 step 5): this release's appsettings key/value delta,
/// edited through AppKit's <see cref="KeyValueDeltaGrid"/> (not raw JSON).
/// The Deployer merges these keys into appsettings.json on the target after
/// showing a before/after diff; Azure targets receive them via the
/// Configuration API instead.
/// </summary>
internal sealed class StepDelta : WizardStep
{
    private readonly KeyValueDeltaGrid _deltaGrid;

    public StepDelta(PackagerWizardForm wizard, PackageDraft draft)
        : base(wizard, draft)
    {
        _deltaGrid = new KeyValueDeltaGrid { Dock = DockStyle.Fill };

        var note = new Label
        {
            Text = "Values are stored as JSON when they parse (numbers, true/false, null removes the key, {…}/[…]); " +
                   "otherwise as strings. These keys are merged into appsettings.json by the Deployer after showing a diff.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 40,
            ForeColor = Color.DimGray,
            Padding = new Padding(2, 2, 2, 6),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(note);
        layout.Controls.Add(_deltaGrid);

        Controls.Add(layout);
    }

    public override string Title => "4. App settings delta";

    public override string Hint =>
        "Optional. Add only the keys that change in this release — everything else on the target is preserved.";

    public override bool CanProceed => true;

    public override void OnEnter() => _deltaGrid.LoadDelta(Draft.AppSettingsDelta);

    public override void OnLeave() => Commit();

    /// <summary>Wires the grid contents into the draft (also called right
    /// before a build so the very last edit is always captured).</summary>
    public void Commit() => Draft.AppSettingsDelta = _deltaGrid.GetDelta();
}
