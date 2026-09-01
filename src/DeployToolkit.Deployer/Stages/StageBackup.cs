using DeployToolkit.AppKit;
using DeployToolkit.Core.Backup;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Plan §11 step 4 — ADVISORY ONLY in v1: the orchestrator's run already
/// performs the backup as its first internal step (verify → backup → stop →
/// …), so this stage exists to give the operator an explicit, early "take a
/// backup now" click plus visibility of where backups land. For executor
/// targets (Azure/Plesk) nothing is modified locally, so the button is
/// disabled with an explanation.
/// </summary>
internal sealed class StageBackup : StagePanel
{
    private readonly TextBox _resultBox;
    private readonly Button _backupButton;
    private readonly Button _skipButton;

    public StageBackup(MainForm shell)
        : base(shell)
    {
        var layout = MakeVerticalLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(AppTheme.MakeSectionLabel("Pre-flight backup (the deploy run also backs up automatically)"));

        layout.Controls.Add(new Label
        {
            Text = "Backups go to Documents\\Backups\\{yyyyMMdd}\\{HHmm}-{component}\\ and contain every file the package " +
                   "is about to replace, plus a backup-manifest.json — this is what rollback (and the standalone " +
                   "Rollback… menu) restores from. This step is advisory: skipping it is safe because the deploy " +
                   "run backs up before touching anything.",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 0, 2, 6),
        });

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 4, 0, 4),
            WrapContents = false,
        };
        _backupButton = new Button { Text = "Pre-flight backup now" };
        AppTheme.StyleButton(_backupButton);
        _backupButton.Click += (_, _) => Guard.RunAsync(Shell, "Backing up…", RunBackupAsync);
        _skipButton = new Button { Text = "Skip — go to Deploy" };
        AppTheme.StyleButton(_skipButton);
        _skipButton.Click += (_, _) => Shell.ShowDeployStage();
        buttons.Controls.Add(_backupButton);
        buttons.Controls.Add(_skipButton);
        layout.Controls.Add(buttons);

        _resultBox = MakeReadOnlySummaryBox(120);
        layout.Controls.Add(_resultBox);

        Controls.Add(layout);
    }

    public override string Title => "4. Backup";

    public override void OnEnter()
    {
        if (Context is not { } context)
        {
            _backupButton.Enabled = false;
            _resultBox.Text = string.Empty;
            return;
        }

        var iis = context.TargetType == Core.Targets.TargetType.IisLocal && context.SiteRoot is not null;
        _backupButton.Enabled = iis;
        _resultBox.Text = iis
            ? string.Empty
            : "Backup is not applicable for this target: Azure/Plesk runs upload from a local temp copy and never " +
              "modify local files (server-side / remote files are the host's responsibility).";
    }

    private Task RunBackupAsync()
    {
        if (Context is not { } context || context.SiteRoot is null)
        {
            AppTheme.Error(this, "No site root available — resolve the target and complete pre-flight first.");
            return Task.CompletedTask;
        }

        var manifest = context.Manifest;
        var backupFolder = new BackupManager().Backup(
            manifest.Client,
            manifest.Component,
            context.SiteRoot,
            manifest.Files.Select(f => f.Path).ToList());

        _resultBox.Text = $"Backup taken: {backupFolder}\n" +
                          $"Files: {manifest.Files.Count} (missing files had nothing to back up yet — normal for new files).";
        Shell.AppendLog($"Pre-flight backup written to {backupFolder}.");
        return Task.CompletedTask;
    }
}
