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
        // full database backup (.bak) alongside the file backup when:
        //  - the manifest has DB scripts, AND
        //  - a DB connection string was provided (from preflight or the
        //    deploy prompt), AND
        //  - the orchestrator created a backup folder.
        // The backup runs BEFORE the return so the .bak lands in the same
        // folder as the file backup. Best-effort — a backup failure is logged
        // but doesn't fail the deploy (the file backup + the DB scripts
        // themselves are the safety net).
        if (manifest.DbScripts.Count > 0 && context.DbConnectionString is { } dbConn
            && !string.IsNullOrEmpty(result.BackupFolder))
        {
            try
            {
                await GenerateDatabaseBackupAsync(dbConn, result.BackupFolder, logger);
            }
            catch (Exception dbEx)
            {
                logger.Warn($"Database backup failed (non-fatal — file backup succeeded): {dbEx.Message}");
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
    /// Q8: generates a full database backup (.bak) via <c>BACKUP DATABASE
    /// TO DISK</c> and saves it alongside the file backup folder. Runs off
    /// the UI thread (SQL Server backup is blocking IO). Best-effort — a
    /// failure is logged but doesn't fail the deploy.
    /// </summary>
    private static async Task GenerateDatabaseBackupAsync(
        string connectionString, string backupFolder, RunLogger logger)
    {
        // Extract the database name from the connection string to name the
        // .bak file and to reference it in the BACKUP command.
        var dbName = ExtractDatabaseName(connectionString) ?? "database";
        var bakPath = Path.Combine(backupFolder, $"{dbName}.bak");
        logger.Info($"Generating database backup: {bakPath}");

        await Task.Run(() =>
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            conn.Open();

            // BACKUP DATABASE with INIT (overwrite — this is a fresh backup
            // folder) and COMPRESSION (smaller file for < 2GB DBs). The
            // timeout is generous (10 min) since a 2GB DB backup can take a
            // few minutes on a slow disk.
            var sql = $"BACKUP DATABASE [{dbName}] TO DISK = N'{bakPath}' " +
                      $"WITH INIT, COMPRESSION, NAME = N'DeployToolkit pre-deploy backup'";
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn)
            {
                CommandTimeout = 600, // 10 minutes
            };
            cmd.ExecuteNonQuery();
        });

        var sizeMB = new FileInfo(bakPath).Length / (1024.0 * 1024);
        logger.Info($"Database backup completed: {bakPath} ({sizeMB:F1} MB)");
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
