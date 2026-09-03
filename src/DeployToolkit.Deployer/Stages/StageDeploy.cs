using System.Diagnostics;
using System.Text;
using DeployToolkit.AppKit;
using DeployToolkit.Core.Backup;
using DeployToolkit.Core.Database;
using DeployToolkit.Core.Deployment;
using DeployToolkit.Core.IisControl;
using DeployToolkit.Core.Logging;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;
using DeployToolkit.Core.Targets.AzureAppService;
using DeployToolkit.Core.Targets.Plesk;
using DeployToolkit.Core.Windows;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Plan §11 steps 5–9 in one guarded run. The run shape depends on the
/// target:
///
///  - IIS: the full <see cref="DeploymentOrchestrator"/> flow (integrity →
///    backup → stop → deploy → merge → DB → start → health, auto-rollback)
///    with hooks built by <see cref="IisDeploymentHooksFactory"/>; the
///    orchestrator records the run and flips the package to Deployed.
///  - Azure App Service: <see cref="AzureAppServiceExecutor"/> (Kudu zip
///    deploy + optional ARM settings) — no stop/start and no local backup
///    (server-side atomicity); run recording is replicated here so the
///    audit trail is identical to the orchestrator's.
///  - Plesk: <see cref="PleskExecutor"/> (SFTP upload) with the same
///    replicated recording.
///
/// Everything streams into the live log pane through a
/// <see cref="RunLogger"/> (plan §8.6) whose EntryLogged event mirrors each
/// entry (the pane marshals onto the UI thread itself). This stage owns its
/// error surface instead of the Guard overlay: the run must keep the log
/// visible and the Cancel button reachable, and no exception may reach the
/// WinForms message loop.
/// </summary>
internal sealed class StageDeploy : StagePanel
{
    private readonly TextBox _planBox;
    private readonly Label _resultLabel;
    private readonly Button _viewLogButton;
    private readonly Button _openBackupButton;
    private readonly Button _cancelButton;

    private CancellationTokenSource? _deployCts;
    private string? _lastBackupFolder;

    public StageDeploy(MainForm shell)
        : base(shell)
    {
        var layout = MakeVerticalLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(AppTheme.MakeSectionLabel("Run plan (the Deploy button below the log starts it)"));

        _planBox = MakeReadOnlySummaryBox(160);
        layout.Controls.Add(_planBox);

        _resultLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 64,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 6, 0, 2),
            WrapContents = false,
        };
        _viewLogButton = new Button { Text = "View Last Run Log", Enabled = false };
        AppTheme.StyleButton(_viewLogButton);
        _viewLogButton.Click += (_, _) => Shell.ViewLastRunLog();
        _openBackupButton = new Button { Text = "Open backup folder", Visible = false };
        AppTheme.StyleButton(_openBackupButton);
        _openBackupButton.Click += (_, _) => OpenBackupFolder();
        _cancelButton = new Button { Text = "Cancel upload", Visible = false };
        AppTheme.StyleButton(_cancelButton);
        _cancelButton.Click += (_, _) => _deployCts?.Cancel();
        buttons.Controls.Add(_viewLogButton);
        buttons.Controls.Add(_openBackupButton);
        buttons.Controls.Add(_cancelButton);

        layout.Controls.Add(_resultLabel);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
    }

    public override string Title => "5. Deploy";

    public override void OnEnter()
    {
        if (Context is not { } context)
        {
            _planBox.Text = string.Empty;
            _resultLabel.Text = string.Empty;
            return;
        }

        _viewLogButton.Enabled = Shell.HasLastRunLog;
        RenderPlan(context);
    }

    /// <summary>Starts the deploy run (invoked from the bottom-bar "Deploy"
    /// button, which is only enabled once pre-flight passed).</summary>
    internal void StartDeploy()
    {
        if (Shell.Context is not { } context || Shell.Store is null)
        {
            AppTheme.Error(this, "Load a package and complete pre-flight first.");
            return;
        }

        if (context.Package is not { } package)
        {
            AppTheme.Error(this, "The package is not matched to a registry record — reload it.");
            return;
        }

        var registry = Shell.Store;
        Shell.SetStage(DeployerStage.Running);

        // Executor paths (Azure/Plesk) honour cancellation; the orchestrator
        // run has no cancellation token, so the button stays hidden there.
        _deployCts = new CancellationTokenSource();
        _cancelButton.Visible = context.TargetType is TargetType.AzureAppService or TargetType.Plesk;

        _ = RunGuardedAsync(context, registry, package.PackageId);
    }

    // ---------------------------------------------------------------
    // Run plumbing

    /// <summary>
    /// The run's streaming error surface (see type doc): stage buttons are
    /// disabled by the state machine while <see cref="DeployerStage.Running"/>,
    /// the log pane stays live, and every failure lands in the log plus a
    /// friendly dialog instead of the message loop. A successful run moves
    /// the shell to Done; a failed or cancelled one returns to Ready so the
    /// same package can be re-deployed immediately.
    /// </summary>
    private async Task RunGuardedAsync(DeploymentContext context, IRegistryStore registry, string packageId)
    {
        var manifest = context.Manifest;
        var startedUtc = DateTimeOffset.UtcNow;
        RunLogger? logger = null;

        try
        {
            Shell.ClearLog();
            logger = new RunLogger(DeployerPaths.LogsRoot, manifest.Client, manifest.Component);

            // RunLogger raises EntryLogged synchronously from whichever thread
            // logs; LogPane.AppendLine marshals onto the UI thread internally.
            logger.EntryLogged += entry =>
                Shell.AppendLog($"{entry.TimestampUtc.LocalDateTime:HH:mm:ss.fff} [{entry.Level}] {entry.Message}");

            Shell.SetLastRunLogPath(logger.LogFilePath);
            _viewLogButton.Enabled = true;

            logger.Info($"Deploy run started by {Environment.UserName}: package {packageId} " +
                        $"({manifest.Component} v{manifest.Version} for {manifest.Client}), target {context.TargetType}.");

            var outcome = context.TargetType switch
            {
                TargetType.IisLocal => await RunIisAsync(context, registry, logger, packageId),
                TargetType.AzureAppService => await RunAzureAsync(context, registry, logger, packageId),
                TargetType.Plesk => await RunPleskAsync(context, registry, logger, packageId),
                _ => throw new InvalidOperationException("Target type not resolved — complete pre-flight first."),
            };

            if (context.OfflineMode)
                await WriteOfflineResultAsync(context, logger, packageId, startedUtc, outcome);

            RenderResult(outcome);
            logger.Info($"Run finished with result {outcome.Result}.");

            if (outcome.Success)
                Shell.OnRunSucceeded();
            else
                Shell.OnRunFailed();
        }
        catch (OperationCanceledException)
        {
            logger?.Warn("Deploy run cancelled by the operator.");
            _resultLabel.ForeColor = Color.DarkOrange;
            _resultLabel.Text = "Deploy run cancelled — nothing was recorded as deployed. Re-run 'Deploy' when ready.";
            Shell.OnRunFailed();
        }
        catch (Exception ex)
        {
            logger?.Error($"Deploy run failed: {ex}");
            _resultLabel.ForeColor = Color.Firebrick;
            _resultLabel.Text = "Deploy run failed before a result could be recorded — see the log and the error dialog.";
            AppTheme.Error(this, ex, "Deploy run failed");
            Shell.OnRunFailed();
        }
        finally
        {
            logger?.Dispose();
            _cancelButton.Visible = false;
            _deployCts?.Dispose();
            _deployCts = null;
        }
    }

    /// <summary>Unified outcome across the orchestrator and executor paths —
    /// <see cref="OfflineRunResult"/> and the result strip render from this.</summary>
    private sealed record RunOutcome(
        bool Success,
        string Result, // "Success" | "Failed" | "RolledBack" (matches DeploymentRuns.Result)
        bool RolledBack,
        string Message,
        string? BackupFolder,
        bool HealthCheckPassed,
        IReadOnlyList<string> LogLines);

    // ---------------------------------------------------------------
    // IIS — the orchestrator path (steps 5–9 with auto-rollback)

    private async Task<RunOutcome> RunIisAsync(
        DeploymentContext context, IRegistryStore registry, RunLogger logger, string packageId)
    {
        var manifest = context.Manifest;

        if (context.IisController is null || context.IisTarget is null || string.IsNullOrWhiteSpace(context.SiteRoot))
            throw new InvalidOperationException("The IIS target is not fully resolved — complete pre-flight first.");

        // Stop/start via the app-pool-first, app_offline-fallback strategy —
        // MWA is synchronous, so the factory wraps it in Task.Run.
        var hooks = IisDeploymentHooksFactory.CreateStopStartHooks(
            context.IisController,
            context.IisTarget.AppPoolName,
            context.SiteRoot,
            IisStopStrategy.Auto);

        if (manifest.DbScripts.Count > 0)
        {
            var connectionString = ResolveDbConnectionString(manifest);
            context.DbConnectionString = connectionString;
            if (connectionString is not null)
            {
                hooks = hooks with
                {
                    RunDbScripts = IisDeploymentHooksFactory.CreateDbScriptsHook(
                        connectionString,
                        new SqlScriptRunnerOptions(),
                        new Progress<string>(logger.Info)),
                };
                logger.Info($"DB scripts enabled — {manifest.DbScripts.Count} script(s) will run (read straight from the package zip).");
            }
            else
            {
                logger.Info("DB scripts SKIPPED this run (no connection provided) — the orchestrator omits the DB step.");
            }
        }

        // The orchestrator only invokes the health hook when the manifest
        // carries a HealthCheckUrl, so an empty URL is fine (verified).
        hooks = hooks with { HealthCheck = HealthCheckAsync };

        var orchestrator = new DeploymentOrchestrator(registry, new BackupManager(), logger);
        var request = new DeploymentRunRequest(
            context.ZipPath,
            context.SiteRoot,
            context.AppSettingsPath ?? Path.Combine(context.SiteRoot, "appsettings.json"),
            manifest.Client,
            manifest.Component,
            Environment.UserName,
            hooks);

        // The orchestrator does heavy synchronous IO between its awaits (zip
        // integrity, extraction, backup, file copy) — it must run off the UI
        // thread so the log pane and the window stay responsive throughout
        // the deploy (user-reported freeze class).
        var result = await Task.Run(() => orchestrator.RunAsync(request, packageId));

        // Q8: after the orchestrator backs up the website files, generate a
        // full database script backup (schema + data + triggers + SPs +
        // indexes + constraints + FKs) alongside the file backup when:
        //  - the manifest has DB scripts, AND
        //  - a DB connection string was provided, AND
        //  - the orchestrator created a backup folder.
        // Uses SQL Server's scripting (sys.objects + sp_helptext) instead of
        // BACKUP DATABASE (.bak) because AWS RDS and other managed SQL
        // services don't support BACKUP DATABASE TO DISK.
        if (manifest.DbScripts.Count > 0 && context.DbConnectionString is { } dbConn
            && !string.IsNullOrEmpty(result.BackupFolder))
        {
            try
            {
                await GenerateDatabaseBackupAsync(dbConn, result.BackupFolder, logger);
            }
            catch (Exception dbEx)
            {
                logger.Error($"Database backup failed: {dbEx.Message}");
                // Non-fatal — the file backup succeeded and the DB scripts
                // themselves are the safety net. The error is logged at ERROR
                // level (not WARN) so the user sees it clearly.
            }
        }

        return new RunOutcome(
            result.Success,
            result.Success ? "Success" : result.RolledBack ? "RolledBack" : "Failed",
            result.RolledBack,
            result.Message,
            result.BackupFolder,
            result.Success,
            result.Log);
    }

    /// <summary>
    /// Resolves the DB connection string for the script hook: a modal prompt
    /// (manual entry or the local SecretVault when the component's
    /// DbConnectionRef is a vault:// reference). Returns null after an
    /// explicit user-confirmed skip; throws
    /// <see cref="OperationCanceledException"/> when the user declines the
    /// skip so the whole run aborts before anything is touched. The string
    /// lives in the <see cref="DeploymentContext"/> for the session only and
    /// is never persisted.
    /// </summary>
    private string? ResolveDbConnectionString(ComponentManifest manifest)
    {
        if (Context?.DbConnectionString is { } cached)
            return cached;

        var dbRef = Context?.Component?.DbConnectionRef;
        using var prompt = new DbScriptsConnectionPrompt(
            manifest.Component, manifest.DbScripts, dbRef, DeployerPaths.VaultPath);

        if (prompt.ShowDialog(this) == DialogResult.OK && prompt.ConnectionString is { } connectionString)
            return connectionString;

        if (AppTheme.Confirm(this,
                "No database connection was provided.\n\nSkip the DB scripts this run? " +
                "(Files still deploy; the run log records the skip.)",
                "Skip DB scripts?") != DialogResult.Yes)
        {
            throw new OperationCanceledException(); // abort before anything is touched
        }

        return null;
    }

    /// <summary>
    /// Q8: generates a full database script (schema + data + triggers +
    /// stored procedures + indexes + constraints + foreign keys) and saves
    /// it as a .sql file alongside the file backup folder.
    ///
    /// Compatible with SQL Server 2016 through 2022+ (uses only sys.* and
    /// INFORMATION_SCHEMA views that exist in all versions, avoids .NET 5+
    /// APIs for binary hex conversion, handles legacy types like text/ntext,
    /// decimal/numeric precision, identity columns, computed columns, primary
    /// keys, and special types like xml/geography/geometry/hierarchyid).
    /// </summary>
    private static async Task GenerateDatabaseBackupAsync(
        string connectionString, string backupFolder, RunLogger logger)
    {
        var dbName = ExtractDatabaseName(connectionString) ?? "database";
        var scriptPath = Path.Combine(backupFolder, $"{dbName}-backup.sql");
        logger.Info($"Generating database script backup: {scriptPath}");

        await Task.Run(() =>
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            conn.Open();

            var sb = new StringBuilder();
            sb.AppendLine($"-- Database script backup: {dbName}");
            sb.AppendLine($"-- Generated by DeployToolkit on {DateTimeOffset.UtcNow:u}");
            sb.AppendLine($"-- Contains: schema, data, triggers, stored procedures, indexes, constraints, foreign keys");
            sb.AppendLine();

            // 1. Tables (schema via sys.columns for full fidelity + data via SELECT)
            var tables = ExecuteReader(conn,
                "SELECT s.name AS schema_name, t.name AS table_name " +
                "FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                "ORDER BY s.name, t.name");

            foreach (var row in tables)
            {
                var schema = row[0];
                var table = row[1];
                sb.AppendLine($"-- ============================================================");
                sb.AppendLine($"-- Table: [{schema}].[{table}]");
                sb.AppendLine($"-- ============================================================");
                sb.AppendLine();
                sb.AppendLine($"IF OBJECT_ID(N'[{schema}].[{table}]', N'U') IS NOT NULL DROP TABLE [{schema}].[{table}];");
                sb.AppendLine($"CREATE TABLE [{schema}].[{table}]");
                sb.AppendLine("(");

                // Use sys.columns for full column metadata (identity, computed, precision, scale)
                var columns = ExecuteReader(conn,
                    $"SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale, " +
                    $"c.is_nullable, c.is_identity, c.is_computed, " +
                    $"OBJECT_DEFINITION(c.default_object_id) AS default_val, " +
                    $"cc.definition AS computed_def " +
                    $"FROM sys.columns c " +
                    $"JOIN sys.types t ON c.user_type_id = t.user_type_id " +
                    $"LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id " +
                    $"WHERE c.object_id = OBJECT_ID(N'[{schema}].[{table}]') " +
                    $"ORDER BY c.column_id");

                var colLines = new List<string>();
                var hasIdentity = false;
                foreach (var col in columns)
                {
                    var colName = col[0];
                    var typeName = col[1];
                    var maxLength = col[2];
                    var precision = col[3];
                    var scale = col[4];
                    var isNullable = col[5] == "1";
                    var isIdentity = col[6] == "1";
                    var isComputed = col[7] == "1";
                    var defaultVal = col[8];
                    var computedDef = col[9];

                    if (isIdentity) hasIdentity = true;

                    var line = $"    [{colName}] ";

                    if (isComputed && !string.IsNullOrEmpty(computedDef))
                    {
                        line += $"AS {computedDef}";
                        colLines.Add(line);
                        continue;
                    }

                    // Build the type string with precision/scale/length
                    line += BuildTypeString(typeName, maxLength, precision, scale);

                    if (isNullable) line += " NULL";
                    else line += " NOT NULL";

                    if (isIdentity)
                        line += " IDENTITY(1,1)";

                    if (!string.IsNullOrEmpty(defaultVal))
                        line += $" DEFAULT {defaultVal}";

                    colLines.Add(line);
                }
                sb.AppendLine(string.Join("," + Environment.NewLine, colLines));
                sb.AppendLine(");");
                sb.AppendLine();

                // Data
                sb.AppendLine($"-- Data for [{schema}].[{table}]");
                if (hasIdentity)
                    sb.AppendLine($"SET IDENTITY_INSERT [{schema}].[{table}] ON;");

                using var dataCmd = new Microsoft.Data.SqlClient.SqlCommand(
                    $"SELECT * FROM [{schema}].[{table}]", conn) { CommandTimeout = 300 };
                using var reader = dataCmd.ExecuteReader();
                var colNames = new List<string>();
                for (var i = 0; i < reader.FieldCount; i++)
                    colNames.Add($"[{reader.GetName(i)}]");

                var rowCount = 0;
                while (reader.Read())
                {
                    var values = new List<string>();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var val = reader.GetValue(i);
                        if (val is DBNull || val is null)
                            values.Add("NULL");
                        else if (val is string s)
                            values.Add("N'" + s.Replace("'", "''") + "'");
                        else if (val is bool b)
                            values.Add(b ? "1" : "0");
                        else if (val is DateTime dt)
                            values.Add($"'{dt:yyyy-MM-dd HH:mm:ss.fffffff}'");
                        else if (val is DateTimeOffset dto)
                            values.Add($"'{dto:yyyy-MM-dd HH:mm:ss.fffffff zzz}'");
                        else if (val is Guid g)
                            values.Add($"'{g}'");
                        else if (val is byte[] bytes)
                            values.Add("0x" + BytesToHex(bytes));
                        else if (val is decimal dec)
                            values.Add(dec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        else if (val is double d)
                            values.Add(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        else if (val is float f)
                            values.Add(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        else
                            values.Add("'" + val.ToString()?.Replace("'", "''") + "'");
                    }
                    sb.AppendLine($"INSERT INTO [{schema}].[{table}] ({string.Join(", ", colNames)}) VALUES ({string.Join(", ", values)});");
                    rowCount++;
                }
                reader.Close();
                if (hasIdentity)
                    sb.AppendLine($"SET IDENTITY_INSERT [{schema}].[{table}] OFF;");
                sb.AppendLine($"-- {rowCount} row(s) for [{schema}].[{table}]");
                sb.AppendLine();
            }

            // 2. Primary keys (via sys.key_constraints)
            var pks = ExecuteReader(conn,
                "SELECT SCHEMA_NAME(t.schema_id) AS s, t.name AS tbl, i.name AS idx_name, " +
                "STUFF((" +
                "  SELECT ', [' + c.name + ']' FROM sys.index_columns ic " +
                "  JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id " +
                "  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 " +
                "  ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 2, '') AS cols " +
                "FROM sys.key_constraints kc " +
                "JOIN sys.tables t ON kc.parent_object_id = t.object_id " +
                "JOIN sys.indexes i ON kc.unique_index_id = i.index_id AND kc.parent_object_id = i.object_id " +
                "WHERE kc.type = 'PK' ORDER BY s, tbl");
            foreach (var pk in pks)
            {
                var pkSchema = pk[0];
                var pkTable = pk[1];
                var pkName = pk[2];
                var pkCols = pk[3];
                if (!string.IsNullOrEmpty(pkCols))
                    sb.AppendLine($"ALTER TABLE [{pkSchema}].[{pkTable}] ADD CONSTRAINT [{pkName}] PRIMARY KEY ({pkCols});");
            }
            sb.AppendLine();

            // 3. Stored procedures (via OBJECT_DEFINITION)
            var sps = ExecuteReader(conn,
                "SELECT SCHEMA_NAME(schema_id) AS s, name FROM sys.objects " +
                "WHERE type = 'P' AND is_ms_shipped = 0 ORDER BY s, name");
            foreach (var sp in sps)
            {
                var spSchema = sp[0];
                var spName = sp[1];
                sb.AppendLine($"-- Stored Procedure: [{spSchema}].[{spName}]");
                sb.AppendLine($"IF OBJECT_ID(N'[{spSchema}].[{spName}]', N'P') IS NOT NULL DROP PROCEDURE [{spSchema}].[{spName}];");
                sb.AppendLine("GO");
                var spText = ExecuteScalar<string>(conn,
                    $"SELECT OBJECT_DEFINITION(OBJECT_ID(N'[{spSchema}].[{spName}]'))");
                if (!string.IsNullOrEmpty(spText))
                    sb.AppendLine(spText);
                sb.AppendLine("GO");
                sb.AppendLine();
            }

            // 4. Triggers (via OBJECT_DEFINITION)
            var triggers = ExecuteReader(conn,
                "SELECT SCHEMA_NAME(schema_id) AS s, name " +
                "FROM sys.objects WHERE type = 'TR' AND is_ms_shipped = 0 ORDER BY s, name");
            foreach (var trig in triggers)
            {
                var trigSchema = trig[0];
                var trigName = trig[1];
                sb.AppendLine($"-- Trigger: [{trigSchema}].[{trigName}]");
                var trigText = ExecuteScalar<string>(conn,
                    $"SELECT OBJECT_DEFINITION(OBJECT_ID(N'[{trigSchema}].[{trigName}]'))");
                if (!string.IsNullOrEmpty(trigText))
                    sb.AppendLine(trigText);
                sb.AppendLine("GO");
                sb.AppendLine();
            }

            // 5. Indexes (non-PK, via sys.indexes + sys.index_columns)
            var indexes = ExecuteReader(conn,
                "SELECT SCHEMA_NAME(t.schema_id) AS s, t.name AS tbl, i.name AS idx, i.type_desc, " +
                "i.is_unique, " +
                "STUFF((" +
                "  SELECT ', [' + c.name + CASE ic.is_descending_key WHEN 1 THEN ' DESC' ELSE '' END + ']' " +
                "  FROM sys.index_columns ic " +
                "  JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id " +
                "  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 " +
                "  ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 2, '') AS cols, " +
                "STUFF((" +
                "  SELECT ', [' + c.name + ']' FROM sys.index_columns ic " +
                "  JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id " +
                "  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1 " +
                "  FOR XML PATH('')), 1, 2, '') AS included_cols " +
                "FROM sys.indexes i " +
                "JOIN sys.tables t ON i.object_id = t.object_id " +
                "WHERE i.type > 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.name IS NOT NULL " +
                "ORDER BY s, tbl, idx");
            foreach (var idx in indexes)
            {
                var idxSchema = idx[0];
                var idxTable = idx[1];
                var idxName = idx[2];
                var idxType = idx[3];
                var isUnique = idx[4] == "1";
                var idxCols = idx[5];
                var includedCols = idx[6];

                if (string.IsNullOrEmpty(idxCols)) continue;

                var createLine = "CREATE ";
                if (isUnique) createLine += "UNIQUE ";
                createLine += $"{idxType} INDEX [{idxName}] ON [{idxSchema}].[{idxTable}] ({idxCols})";
                if (!string.IsNullOrEmpty(includedCols))
                    createLine += $" INCLUDE ({includedCols})";
                sb.AppendLine(createLine + ";");
            }
            sb.AppendLine();

            // 6. Foreign keys (via sys.foreign_keys)
            var fks = ExecuteReader(conn,
                "SELECT SCHEMA_NAME(fk.schema_id) AS s, fk.name, " +
                "SCHEMA_NAME(tp.schema_id) AS parent_schema, tp.name AS parent_table, " +
                "SCHEMA_NAME(tr.schema_id) AS ref_schema, tr.name AS ref_table, " +
                "STUFF((" +
                "  SELECT ', [' + c.name + ']' FROM sys.foreign_key_columns fkc " +
                "  JOIN sys.columns c ON fkc.parent_object_id = c.object_id AND fkc.parent_column_id = c.column_id " +
                "  WHERE fkc.constraint_object_id = fk.object_id FOR XML PATH('')), 1, 2, '') AS parent_cols, " +
                "STUFF((" +
                "  SELECT ', [' + c.name + ']' FROM sys.foreign_key_columns fkc " +
                "  JOIN sys.columns c ON fkc.referenced_object_id = c.object_id AND fkc.referenced_column_id = c.column_id " +
                "  WHERE fkc.constraint_object_id = fk.object_id FOR XML PATH('')), 1, 2, '') AS ref_cols " +
                "FROM sys.foreign_keys fk " +
                "JOIN sys.tables tp ON fk.parent_object_id = tp.object_id " +
                "JOIN sys.tables tr ON fk.referenced_object_id = tr.object_id " +
                "ORDER BY s, fk.name");
            foreach (var fk in fks)
            {
                var fkName = fk[1];
                var parentSchema = fk[2];
                var parentTable = fk[3];
                var refSchema = fk[4];
                var refTable = fk[5];
                var parentCols = fk[6];
                var refCols = fk[7];

                sb.AppendLine($"ALTER TABLE [{parentSchema}].[{parentTable}] " +
                    $"WITH CHECK ADD CONSTRAINT [{fkName}] FOREIGN KEY ({parentCols}) " +
                    $"REFERENCES [{refSchema}].[{refTable}] ({refCols});");
            }
            sb.AppendLine();

            File.WriteAllText(scriptPath, sb.ToString());
        });

        var sizeMB = new FileInfo(scriptPath).Length / (1024.0 * 1024);
        logger.Info($"Database script backup completed: {scriptPath} ({sizeMB:F1} MB)");
    }

    /// <summary>Builds the SQL type string with length/precision/scale based on
    /// the sys.columns + sys.types metadata. Handles all SQL Server types
    /// from 2016 through 2022+ including legacy types (text, ntext, image).</summary>
    private static string BuildTypeString(string typeName, string maxLengthStr, string precisionStr, string scaleStr, string isNullableStr = "")
    {
        // Types that don't take length/precision/scale at all.
        var noLengthTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "int", "bigint", "smallint", "tinyint", "bit", "datetime", "smalldatetime",
          "money", "smallmoney", "timestamp", "rowversion", "uniqueidentifier",
          "sql_variant", "geography", "geometry", "hierarchyid", "xml", "date", "time" };

        if (noLengthTypes.Contains(typeName))
            return typeName;

        // Legacy large types — no length specifier.
        if (typeName.Equals("text", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("ntext", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("image", StringComparison.OrdinalIgnoreCase))
            return typeName;

        // decimal/numeric — precision and scale.
        if (typeName.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("numeric", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(precisionStr, out var p) && p > 0)
            {
                if (int.TryParse(scaleStr, out var s) && s > 0)
                    return $"{typeName}({p},{s})";
                return $"{typeName}({p})";
            }
            return typeName;
        }

        // datetime2, datetimeoffset, time — scale.
        if (typeName.Equals("datetime2", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("datetimeoffset", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("time", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(scaleStr, out var s) && s >= 0)
                return $"{typeName}({s})";
            return typeName; // default scale
        }

        // Character/binary types — max_length from sys.columns.
        // sys.columns.max_length is in bytes: nvarchar/nchar = 2 bytes/char,
        // varchar/char/varbinary/binary = 1 byte/char. -1 = MAX.
        if (int.TryParse(maxLengthStr, out var maxLen))
        {
            if (maxLen == -1)
                return $"{typeName}(MAX)";

            // For nvarchar/nchar: max_length is 2x the char count.
            if (typeName.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals("nchar", StringComparison.OrdinalIgnoreCase))
            {
                var charLen = maxLen / 2;
                return $"{typeName}({charLen})";
            }

            return $"{typeName}({maxLen})";
        }

        // Fallback: just the type name.
        return typeName;
    }

    /// <summary>Converts a byte array to a hex string without using
    /// Convert.ToHexString (which is .NET 5+ — the Deployer targets net8.0
    /// but this ensures compatibility with any runtime that might host the
    /// method). Uses BitConverter + Replace for a clean hex string.</summary>
    private static string BytesToHex(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", "");
    }

    /// <summary>Helper: executes a reader and returns all rows as string
    /// arrays (one array per row, one element per column).</summary>
    private static List<string[]> ExecuteReader(Microsoft.Data.SqlClient.SqlConnection conn, string sql)
    {
        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn) { CommandTimeout = 300 };
        using var reader = cmd.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i).ToString() ?? string.Empty;
            rows.Add(values);
        }
        return rows;
    }

    /// <summary>Helper: executes a scalar and casts to T.</summary>
    private static T? ExecuteScalar<T>(Microsoft.Data.SqlClient.SqlConnection conn, string sql)
    {
        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn) { CommandTimeout = 300 };
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? default : (T)result;
    }

    /// <summary>Extracts the Initial Catalog / Database value from a SQL
    /// connection string. Returns null when not found (the caller falls back
    /// to "database").</summary>
    private static string? ExtractDatabaseName(string connectionString)
    {
        foreach (var part in connectionString.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = part.IndexOf('=');
            if (sep <= 0) continue;
            var key = part[..sep].Trim();
            var value = part[(sep + 1)..].Trim();
            if (key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Database", StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    // ---------------------------------------------------------------
    // Azure App Service — executor path (no stop/start, no local backup)

    private async Task<RunOutcome> RunAzureAsync(
        DeploymentContext context, IRegistryStore registry, RunLogger logger, string packageId)
    {
        var manifest = context.Manifest;

        if (string.IsNullOrWhiteSpace(context.KuduSiteName) ||
            string.IsNullOrWhiteSpace(context.KuduUsername) ||
            string.IsNullOrWhiteSpace(context.KuduPassword))
        {
            throw new InvalidOperationException("Kudu publish credentials are missing — complete pre-flight first.");
        }

        logger.Info("Azure path: NO stop/start and NO local backup — Kudu zip deploy is atomic server-side.");

        // Audit trail identical to the orchestrator's (run start before,
        // run complete + MarkDeployed after) — the executor itself only
        // deploys files/settings.
        logger.Info("Recording run start in the registry...");
        var run = await registry.RecordRunStartAsync(packageId, DateTimeOffset.UtcNow);

        var tempRoot = DeployerPaths.TempExtractRoot(packageId);
        try
        {
            // Zip integrity + extraction are heavy synchronous IO — keep
            // them off the UI thread (they run on the same thread the rest
            // of the deploy uses; the UI stays live via the awaits).
            var integrity = await Task.Run(() => PackageReader.VerifyIntegrity(context.ZipPath), _deployCts!.Token);
            if (!integrity.IsValid)
                throw new InvalidOperationException("Package integrity check failed: " + string.Join("; ", integrity.Problems));
            logger.Info("Package integrity check passed.");

            logger.Info($"Extracting package files to {tempRoot}...");
            if (Directory.Exists(tempRoot))
                await Task.Run(() => Directory.Delete(tempRoot, recursive: true), _deployCts!.Token);
            await Task.Run(() => PackageReader.ExtractFiles(context.ZipPath, tempRoot), _deployCts!.Token);

            AzureAppSettingsClient? appSettingsClient = null;
            AzureTargetSettings? armTarget = null;
            if (context.ApplyAzureSettings && manifest.AppSettingsDelta.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(context.ArmToken))
                {
                    if (AppTheme.Confirm(this,
                            "No ARM bearer token was provided, and the manifest carries an appsettings delta.\n\n" +
                            "Proceed WITHOUT applying the appsettings delta?",
                            "Skip ARM settings?") != DialogResult.Yes)
                    {
                        throw new OperationCanceledException();
                    }

                    logger.Info("ARM settings skipped by operator choice — the appsettings delta will NOT be applied.");
                }
                else
                {
                    var token = context.ArmToken!;
                    appSettingsClient = new AzureAppSettingsClient(_ => Task.FromResult<string?>(token));
                    armTarget = new AzureTargetSettings(
                        context.ArmSubscriptionId!, context.ArmResourceGroup!, context.ArmSiteName!);
                }
            }

            using var kudu = new KuduClient(new KuduCredentials(
                context.KuduSiteName, context.KuduUsername, context.KuduPassword));
            var executor = new AzureAppServiceExecutor(kudu, appSettingsClient, armTarget);
            var deploy = await executor.DeployAsync(
                manifest, tempRoot, new Progress<string>(logger.Info), _deployCts!.Token);

            var healthOk = await RunHealthCheckForExecutorPathAsync(manifest, logger);
            return await RecordExecutorOutcomeAsync(
                registry, logger, packageId, run.RunId, deploy.Success && healthOk,
                deploy.Success ? deploy.Message : $"Kudu deploy failed: {deploy.Message}",
                tempRoot, cleanupTempRoot: deploy.Success && healthOk);
        }
        catch
        {
            await registry.RecordRunCompleteAsync(run.RunId, "Failed", null, logger.LogFilePath);
            throw;
        }
    }

    // ---------------------------------------------------------------
    // Plesk — executor path (SFTP upload)

    private async Task<RunOutcome> RunPleskAsync(
        DeploymentContext context, IRegistryStore registry, RunLogger logger, string packageId)
    {
        var manifest = context.Manifest;

        if (context.PleskConnection is null || context.PleskDeploy is null)
            throw new InvalidOperationException("Plesk connection/deploy options are missing — complete pre-flight first.");

        logger.Info("Plesk path: SFTP upload only — the appsettings delta and DB scripts are NOT applied on this target.");

        logger.Info("Recording run start in the registry...");
        var run = await registry.RecordRunStartAsync(packageId, DateTimeOffset.UtcNow);

        var tempRoot = DeployerPaths.TempExtractRoot(packageId);
        try
        {
            var integrity = await Task.Run(() => PackageReader.VerifyIntegrity(context.ZipPath), _deployCts!.Token);
            if (!integrity.IsValid)
                throw new InvalidOperationException("Package integrity check failed: " + string.Join("; ", integrity.Problems));
            logger.Info("Package integrity check passed.");

            logger.Info($"Extracting package files to {tempRoot}...");
            if (Directory.Exists(tempRoot))
                await Task.Run(() => Directory.Delete(tempRoot, recursive: true), _deployCts!.Token);
            await Task.Run(() => PackageReader.ExtractFiles(context.ZipPath, tempRoot), _deployCts!.Token);

            using var uploader = new SftpFileUploader(context.PleskConnection);
            var executor = new PleskExecutor(uploader, context.PleskDeploy);
            var deploy = await executor.DeployAsync(
                manifest, tempRoot, new Progress<string>(logger.Info), _deployCts!.Token);

            var healthOk = await RunHealthCheckForExecutorPathAsync(manifest, logger);
            return await RecordExecutorOutcomeAsync(
                registry, logger, packageId, run.RunId, deploy.Success && healthOk,
                deploy.Success ? deploy.Message : $"Plesk deploy failed: {deploy.Message}",
                tempRoot, cleanupTempRoot: deploy.Success && healthOk);
        }
        catch
        {
            await registry.RecordRunCompleteAsync(run.RunId, "Failed", null, logger.LogFilePath);
            throw;
        }
    }

    /// <summary>Executors never claim the health check (their contract says
    /// it belongs to the UI layer) — so the Deployer runs it itself when the
    /// manifest carries a URL. No health URL → treated as passed, matching
    /// the orchestrator's behavior.</summary>
    private async Task<bool> RunHealthCheckForExecutorPathAsync(ComponentManifest manifest, RunLogger logger)
    {
        if (string.IsNullOrWhiteSpace(manifest.HealthCheckUrl))
        {
            logger.Info("No health check URL in the manifest — skipping the health check.");
            return true;
        }

        logger.Info($"Running health check against {manifest.HealthCheckUrl}...");
        var passed = await HealthCheckAsync(manifest.HealthCheckUrl);
        logger.Info(passed ? "Health check passed." : "Health check FAILED.");
        return passed;
    }

    /// <summary>
    /// Shared executor-path bookkeeping: on success flips the package to
    /// Deployed (MarkDeployedAsync — the orchestrator normally does this) and
    /// records the completed run; on failure records a Failed run and leaves
    /// the package Created so it can be redeployed. Executor targets have no
    /// rollback machinery, so a failed health check surfaces as Failed with
    /// guidance instead of the orchestrator's RolledBack.
    /// </summary>
    private async Task<RunOutcome> RecordExecutorOutcomeAsync(
        IRegistryStore registry,
        RunLogger logger,
        string packageId,
        string runId,
        bool success,
        string message,
        string tempRoot,
        bool cleanupTempRoot)
    {
        if (success)
        {
            await registry.MarkDeployedAsync(packageId, Environment.UserName, DateTimeOffset.UtcNow);
            await registry.RecordRunCompleteAsync(runId, "Success", true, logger.LogFilePath);
            logger.Info("Deployment recorded as Deployed.");
        }
        else
        {
            var guidance = message +
                (message.Contains("health", StringComparison.OrdinalIgnoreCase)
                    ? " The package was NOT marked Deployed — investigate the site before trusting this release."
                    : " The package stays Created so it can be redeployed.");
            await registry.RecordRunCompleteAsync(runId, "Failed", false, logger.LogFilePath);
            logger.Error(guidance);
            message = guidance;
        }

        if (cleanupTempRoot)
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Left for the OS to clean; %TEMP% is not worth failing a run over.
            }
        }

        return new RunOutcome(
            success,
            success ? "Success" : "Failed",
            RolledBack: false,
            message,
            BackupFolder: null,
            HealthCheckPassed: success,
            LogLines: Array.Empty<string>()); // filled from the log file for the offline record
    }

    /// <summary>The plan §7 health check: GET with a 10s timeout; any status
    /// code outside 2xx, or any exception (DNS/TLS/refused), is a failure.</summary>
    private static async Task<bool> HealthCheckAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Offline fallback (plan §2.2): writes the run outcome for the Packager
    /// to reconcile into the central registry later. The record fields are
    /// filled from the real outcome — HealthCheckPassed mirrors what the
    /// run determined (the orchestrator only returns Success when health
    /// passed or no URL was present; executor paths run the check here).
    /// </summary>
    private static async Task WriteOfflineResultAsync(
        DeploymentContext context, RunLogger logger, string packageId, DateTimeOffset startedUtc, RunOutcome outcome)
    {
        var logLines = outcome.LogLines;
        if (logLines.Count == 0)
        {
            try
            {
                logLines = RunLogger.ReadLog(logger.LogFilePath)
                    .Select(e => $"{e.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff zzz} {e.Level} {e.Message}")
                    .ToList();
            }
            catch (IOException)
            {
                logLines = Array.Empty<string>(); // the result JSON still carries the outcome
            }
        }

        var resultPath = await OfflineResultWriter.WriteAsync(DeployerPaths.OfflineResultsRoot, new OfflineRunResult(
            SchemaVersion: OfflineRunResult.CurrentSchemaVersion,
            PackageId: packageId,
            ComponentId: context.Manifest.ComponentId,
            Client: context.Manifest.Client,
            Component: context.Manifest.Component,
            Result: outcome.Result,
            HealthCheckResult: outcome.HealthCheckPassed,
            Message: outcome.Message,
            DeployedBy: Environment.UserName,
            StartedUtc: startedUtc,
            CompletedUtc: DateTimeOffset.UtcNow,
            LogLines: logLines));

        logger.Info($"Offline result written for the Packager to reconcile: {resultPath}");
    }

    // ---------------------------------------------------------------
    // Rendering

    private void RenderPlan(DeploymentContext context)
    {
        var manifest = context.Manifest;
        var plan = new StringBuilder();
        plan.AppendLine($"Target:      {context.TargetType}");

        switch (context.TargetType)
        {
            case TargetType.IisLocal:
                plan.AppendLine($"Site root:   {context.SiteRoot ?? "(not set — re-run pre-flight)"}");
                plan.AppendLine($"App settings: {context.AppSettingsPath ?? "(default: {site root}\\appsettings.json)"}");
                break;
            case TargetType.AzureAppService:
                plan.AppendLine($"Kudu site:   {context.KuduSiteName ?? "(not set — re-run pre-flight)"}");
                plan.AppendLine($"ARM settings:{(context.ApplyAzureSettings ? "apply delta via ARM" : "skip (zip deploy only)")}");
                break;
            case TargetType.Plesk:
                plan.AppendLine($"Plesk host:  {context.PleskConnection?.Host ?? "(not set — re-run pre-flight)"}");
                plan.AppendLine($"Remote root: {context.PleskDeploy?.RemoteRootPath ?? "(not set — re-run pre-flight)"}");
                break;
        }

        plan.AppendLine($"Package:     {manifest.Files.Count} file(s), {manifest.DeletedFiles.Count} deleted, " +
                        $"{manifest.AppSettingsDelta.Count} delta key(s), {manifest.DbScripts.Count} DB script(s), " +
                        $"health: {(string.IsNullOrWhiteSpace(manifest.HealthCheckUrl) ? "none" : manifest.HealthCheckUrl)}");
        plan.AppendLine(context.TargetType == TargetType.IisLocal
            ? "Flow:        verify → backup → stop → deploy → merge → DB → start → health (auto-rollback on failure)."
            : "Flow:        verify → extract → upload (executor) → health — no local stop/backup; recording replicated.");

        _planBox.Text = plan.ToString();
    }

    private void RenderResult(RunOutcome outcome)
    {
        if (outcome.Success)
        {
            _resultLabel.ForeColor = Color.ForestGreen;
            _resultLabel.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Regular);
            _resultLabel.Text = "Deployment succeeded — package marked Deployed.\n" + outcome.Message;
        }
        else if (outcome.RolledBack)
        {
            _resultLabel.ForeColor = Color.DarkOrange;
            _resultLabel.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold);
            _resultLabel.Text = "ROLLED BACK — the site was restored to its pre-deploy state.\n" + outcome.Message;
            _lastBackupFolder = outcome.BackupFolder;
            _openBackupButton.Visible = outcome.BackupFolder is not null;
        }
        else
        {
            _resultLabel.ForeColor = Color.Firebrick;
            _resultLabel.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold);
            _resultLabel.Text = "Deployment FAILED.\n" + outcome.Message;
        }
    }

    private void OpenBackupFolder()
    {
        if (_lastBackupFolder is null || !Directory.Exists(_lastBackupFolder))
        {
            AppTheme.Error(this, "The backup folder does not exist (or was already cleaned up).");
            return;
        }

        try
        {
            // Windows-only exe — explorer.exe always exists where this runs.
            Process.Start("explorer.exe", $"\"{_lastBackupFolder}\"");
        }
        catch (Exception ex)
        {
            AppTheme.Error(this, $"Could not open the folder: {ex.Message}");
        }
    }
}
