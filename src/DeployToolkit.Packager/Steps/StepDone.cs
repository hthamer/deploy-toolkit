using DeployToolkit.AppKit;
using DeployToolkit.Core.Packaging;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 7: end-of-run summary (component, version, commit SHA, zip path,
/// package id, manifest counts, stale-package health) with shortcuts to
/// build another package for the same component or close the wizard.
/// </summary>
internal sealed class StepDone : WizardStep
{
    private readonly Label _summaryLabel;
    private readonly Label _staleLabel;
    private readonly Button _buildAnotherButton;
    private readonly Button _closeButton;

    public StepDone(PackagerWizardForm wizard, PackageDraft draft)
        : base(wizard, draft)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = "Package built.",
            AutoSize = true,
            Font = new Font(AppTheme.FontFamily, 12f, FontStyle.Bold),
        };
        layout.Controls.Add(heading);

        _summaryLabel = new Label
        {
            Text = string.Empty,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            ForeColor = Color.Black,
        };
        layout.Controls.Add(_summaryLabel);

        _staleLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 40,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkOrange,
            Visible = false,
        };
        layout.Controls.Add(_staleLabel);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0),
            WrapContents = false,
        };
        _buildAnotherButton = new Button { Text = "Build another package for this component" };
        AppTheme.StyleButton(_buildAnotherButton);
        _buildAnotherButton.Click += (_, _) => Wizard.RestartFromPublishStep();
        _closeButton = new Button { Text = "Close" };
        AppTheme.StyleButton(_closeButton);
        _closeButton.Click += (_, _) => Wizard.Close();
        buttons.Controls.Add(_buildAnotherButton);
        buttons.Controls.Add(_closeButton);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
    }

    public override string Title => "7. Done";

    public override string Hint =>
        "The package is recorded as Created in the registry. Copy it to the target (or deploy Azure directly).";

    public override bool CanProceed => false; // terminal step — the wizard's nav buttons hide here

    public override void OnEnter()
    {
        var component = Draft.Component;
        var result = Draft.BuildResult;

        if (component is null || result is null)
        {
            _summaryLabel.Text = "No build result to summarize.";
            return;
        }

        var manifest = result.Manifest;
        _summaryLabel.Text =
            $"Component:     {component.Name}    Version: {manifest.Version}\n" +
            $"Client:        {manifest.Client}\n" +
            $"Commit SHA:    {ShortSha(manifest.GitCommitSha)}\n" +
            $"Zip path:      {result.ZipPath}\n" +
            $"Package id:    {result.Record.PackageId}\n" +
            $"Manifest:      {manifest.Files.Count} changed/new file(s), {manifest.DeletedFiles.Count} deleted\n" +
            $"Baseline:      {manifest.BaselineManifest ?? "(none — first package for this component)"}";

        if (result.UnresolvedStalePackages.Count > 0)
        {
            var versions = string.Join(", ", result.UnresolvedStalePackages.Select(p => p.Version));
            _staleLabel.Text =
                $"{result.UnresolvedStalePackages.Count} earlier package(s) still Created ({versions}) — " +
                "they will not become diff baselines until resolved on the Clients screen.";
            _staleLabel.Visible = true;
        }
        else
        {
            _staleLabel.Visible = false;
        }
    }
}
