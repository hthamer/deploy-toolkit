using DeployToolkit.AppKit;
using DeployToolkit.Core.Backup;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.Sdk.Sfc;

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
                var dbName = ExtractDatabaseName(dbConn) ?? "database";
                var scriptPath = Path.Combine(backupFolder, $"{dbName}-backup.sql");
                GenerateDatabaseScriptSMO(dbConn, dbName, scriptPath);
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

    /// <summary>Generates a full database script via SMO (same engine SSMS uses
    /// for "Generate Scripts"). Writes schema + data + triggers + SPs +
    /// indexes + constraints + FKs + views + UDFs directly to file.</summary>
    private static void GenerateDatabaseScriptSMO(string connectionString, string dbName, string outputFilePath)
    {
        using var sqlConn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        sqlConn.Open();

        var serverConnection = new ServerConnection(sqlConn);
        var server = new Server(serverConnection);
        var database = server.Databases[dbName];

        if (database is null)
            throw new InvalidOperationException($"Database '{dbName}' not found on the server.");

        var scripter = new Scripter(server);
        scripter.Options = new ScriptingOptions
        {
            ScriptSchema = true,
            ScriptData = true,
            ScriptDrops = false,
            WithDependencies = true,
            Indexes = true,
            DriAllConstraints = true,
            Triggers = true,
            FileName = outputFilePath,
            ToFileOnly = true,
            EnforceScriptingOptions = true,
            ScriptBatchTerminator = true,
            AnsiFile = true,
            IncludeDatabaseContext = false,
        };

        var urns = new List<Urn>();
        foreach (Table table in database.Tables)
            if (!table.IsSystemObject) urns.Add(table.Urn);
        foreach (View view in database.Views)
            if (!view.IsSystemObject) urns.Add(view.Urn);
        foreach (StoredProcedure sp in database.StoredProcedures)
            if (!sp.IsSystemObject) urns.Add(sp.Urn);
        foreach (UserDefinedFunction udf in database.UserDefinedFunctions)
            if (!udf.IsSystemObject) urns.Add(udf.Urn);

        scripter.Script(urns.ToArray());
        serverConnection.Disconnect();
    }

    private static string? ExtractDatabaseName(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = part.IndexOf('=');
            if (sep <= 0) continue;
            var key = part[..sep].Trim();
            var value = part[(sep + 1)..].Trim();
            if (key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase) || key.Equals("Database", StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }
}
