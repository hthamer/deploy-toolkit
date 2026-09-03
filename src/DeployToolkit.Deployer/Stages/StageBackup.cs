using DeployToolkit.AppKit;
using DeployToolkit.Core.Backup;
using DeployToolkit.Core.Database;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Plan §11 step 4: pre-flight backup. Backs up the website files AND
/// generates a full database script backup (schema + data) via SMO when
/// DB scripts are present and a DB connection string was set in pre-flight.
/// </summary>
internal sealed class StageBackup : StagePanel
{
    private readonly TextBox _resultBox;
    private readonly Button _backupButton;
    private readonly Button _skipButton;

    public StageBackup(MainForm shell) : base(shell)
    {
        var layout = MakeVerticalLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(AppTheme.MakeSectionLabel("Pre-flight backup (file backup + database script backup)"));

        layout.Controls.Add(new Label
        {
            Text = "Backs up website files AND generates a full database script backup (schema + data + triggers + " +
                   "stored procedures + indexes + constraints + foreign keys) via SMO when DB scripts are present. " +
                   "The deploy run also backs up automatically, so this step is advisory.",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 0, 2, 6),
        });

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, Dock = DockStyle.Fill,
            Padding = new Padding(0, 4, 0, 4), WrapContents = false,
        };
        _backupButton = new Button { Text = "Backup now" };
        AppTheme.StyleButton(_backupButton);
        _backupButton.Click += (_, _) => Guard.RunAsync(Shell, "Backing up…", RunBackupAsync);
        _skipButton = new Button { Text = "Skip — go to Deploy" };
        AppTheme.StyleButton(_skipButton);
        _skipButton.Click += (_, _) => Shell.ShowDeployStage();
        buttons.Controls.Add(_backupButton);
        buttons.Controls.Add(_skipButton);
        layout.Controls.Add(buttons);

        _resultBox = MakeReadOnlySummaryBox(0);
        layout.Controls.Add(_resultBox);
        Controls.Add(layout);
    }

    public override string Title => "4. Backup";

    internal void StartBackup() => Guard.RunAsync(Shell, "Backing up…", RunBackupAsync);

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
        _resultBox.Text = iis ? string.Empty
            : "Backup requires an IIS local target with a resolved site root.";
    }

    private Task RunBackupAsync()
    {
        if (Context is not { } context || context.SiteRoot is null)
        {
            AppTheme.Error(this, "No site root — resolve the target and complete pre-flight first.");
            return Task.CompletedTask;
        }

        var manifest = context.Manifest;
        var backupFolder = new BackupManager().Backup(
            manifest.Client, manifest.Component, context.SiteRoot,
            manifest.Files.Select(f => f.Path).ToList());

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"File backup: {backupFolder}");
        sb.AppendLine($"Files: {manifest.Files.Count}");

        // DB script backup via SMO (same engine SSMS uses).
        if (manifest.DbScripts.Count > 0 && context.DbConnectionString is { } dbConn)
        {
            sb.AppendLine();
            sb.AppendLine("Generating database script backup (SMO)…");
            try
            {
                var scriptPath = SmoDatabaseScriptBackup.WriteScriptBackup(dbConn, backupFolder);
                var sizeMB = new FileInfo(scriptPath).Length / (1024.0 * 1024);
                sb.AppendLine($"Database script backup completed: {scriptPath} ({sizeMB:F1} MB)");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Database script backup FAILED: {ex.Message}");
            }
        }
        else if (manifest.DbScripts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("DB scripts present but no connection string — DB backup skipped.");
        }

        _resultBox.Text = sb.ToString();
        Shell.AppendLog($"Pre-flight backup written to {backupFolder}.");
        return Task.CompletedTask;
    }
}
