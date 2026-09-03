using DeployToolkit.Core.Backup;
using DeployToolkit.Core.Config;
using DeployToolkit.Core.Logging;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Core.Deployment;

/// <summary>
/// Everything a deployment run needs that this dependency-free project
/// can't provide directly (site stop/start needs Microsoft.Web.Administration
/// or a cloud SDK; DB execution needs Microsoft.Data.SqlClient). Supplying
/// these as delegates lets <see cref="DeploymentOrchestrator"/> be fully
/// built and tested here, with the real IIS/Azure/Plesk/SQL implementations
/// wired in once you're on a dev machine with NuGet access — this is the
/// shared "generic" flow every IDeploymentExecutor implementation reuses.
/// </summary>
public sealed record DeploymentHooks(
    Func<Task>? StopSite = null,
    Func<string, Task>? DatabaseBackup = null,
    Func<Task>? StartSite = null,
    Func<string, IReadOnlyList<DbScriptRef>, Task>? RunDbScripts = null,
    Func<string, Task<bool>>? HealthCheck = null);

public sealed record DeploymentRunRequest(
    string ZipPath,
    string SiteRoot,
    string AppSettingsPath,
    string Client,
    string Component,
    string DeployedBy,
    DeploymentHooks? Hooks = null);

public sealed record DeploymentRunResult(
    bool Success,
    string Message,
    bool RolledBack,
    string? BackupFolder,
    IReadOnlyList<string> Log);

/// <summary>
/// The generic deploy flow: verify package integrity -> backup (file backup,
/// then the database script backup hook — both before anything is touched) ->
/// stop -> extract files -> merge appsettings -> run DB scripts -> start ->
/// health check -> record result, rolling back automatically if anything
/// past the backup step fails or the health check doesn't pass.
/// </summary>
public sealed class DeploymentOrchestrator
{
    private readonly IRegistryStore _registry;
    private readonly BackupManager _backupManager;
    private readonly RunLogger? _logger;

    /// <param name="logger">Optional structured JSON-lines run logger
    /// (plan §8.6). When supplied, every progress message is also persisted
    /// and the log file path flows into DeploymentRuns.LogPath in the
    /// registry — the audit trail per client per deployment.</param>
    public DeploymentOrchestrator(IRegistryStore registry, BackupManager? backupManager = null, RunLogger? logger = null)
    {
        _registry = registry;
        _backupManager = backupManager ?? new BackupManager();
        _logger = logger;
    }

    public async Task<DeploymentRunResult> RunAsync(DeploymentRunRequest request, string packageId)
    {
        var log = new List<string>();
        void Log(string message)
        {
            log.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {message}");
            _logger?.Info(message);
        }
        void LogError(string message) => _logger?.Error(message);

        var hooks = request.Hooks ?? new DeploymentHooks();

        // 1. Integrity check — never touch anything on a bad package.
        Log("Verifying package integrity...");
        var integrity = PackageReader.VerifyIntegrity(request.ZipPath);
        if (!integrity.IsValid)
        {
            var problems = string.Join("; ", integrity.Problems);
            Log($"Integrity check FAILED: {problems}");
            LogError($"Integrity check failed: {problems}");
            return new DeploymentRunResult(false, $"Package integrity check failed: {problems}", false, null, log);
        }
        Log("Integrity check passed.");

        var manifest = PackageReader.ReadManifest(request.ZipPath);
        var run = await _registry.RecordRunStartAsync(packageId, DateTimeOffset.UtcNow);

        // 2. Backup — always, before anything is touched.
        Log("Backing up files about to be replaced...");
        var backupFolder = _backupManager.Backup(
            request.Client,
            request.Component,
            request.SiteRoot,
            manifest.Files.Select(f => f.Path).ToList());
        Log($"Backup written to {backupFolder}.");

        string? preMergeAppSettings = File.Exists(request.AppSettingsPath)
            ? await File.ReadAllTextAsync(request.AppSettingsPath)
            : null;

        try
        {
            // Database script backup runs here, inside the backup step, so a
            // failure aborts the run BEFORE the site is stopped or any file
            // is deployed. The pre-deploy DB state is also captured — not the
            // post-migration state the DB scripts step would leave behind.
            if (hooks.DatabaseBackup is not null)
            {
                Log("Generating database script backup (SMO)...");
                await hooks.DatabaseBackup(backupFolder);
                Log("Database script backup completed.");
            }

            if (hooks.StopSite is not null)
            {
                Log("Stopping site...");
                await hooks.StopSite();
            }

            Log("Deploying files...");
            var extracted = PackageReader.ExtractFiles(request.ZipPath, request.SiteRoot);
            Log($"Deployed {extracted.Count} file(s).");

            if (manifest.AppSettingsDelta.Count > 0)
            {
                Log("Merging appsettings...");
                var merged = AppSettingsMerger.Apply(preMergeAppSettings ?? "{}", manifest.AppSettingsDelta);
                await File.WriteAllTextAsync(request.AppSettingsPath, merged);
                Log($"Applied {manifest.AppSettingsDelta.Count} config key(s).");
            }

            if (manifest.DbScripts.Count > 0 && hooks.RunDbScripts is not null)
            {
                Log($"Running {manifest.DbScripts.Count} DB script(s)...");
                await hooks.RunDbScripts(request.ZipPath, manifest.DbScripts);
            }

            if (hooks.StartSite is not null)
            {
                Log("Starting site...");
                await hooks.StartSite();
            }

            var healthCheckPassed = true;
            if (!string.IsNullOrWhiteSpace(manifest.HealthCheckUrl) && hooks.HealthCheck is not null)
            {
                Log($"Running health check against {manifest.HealthCheckUrl}...");
                healthCheckPassed = await hooks.HealthCheck(manifest.HealthCheckUrl);
                Log(healthCheckPassed ? "Health check passed." : "Health check FAILED.");
            }

            if (!healthCheckPassed)
            {
                Log("Rolling back due to failed health check...");
                await RollBackAsync(backupFolder, request, preMergeAppSettings, hooks, Log);
                await _registry.RecordRunCompleteAsync(run.RunId, "RolledBack", false, _logger?.LogFilePath);
                return new DeploymentRunResult(false, "Health check failed after deploy — rolled back.", true, backupFolder, log);
            }

            await _registry.MarkDeployedAsync(packageId, request.DeployedBy, DateTimeOffset.UtcNow);
            await _registry.RecordRunCompleteAsync(run.RunId, "Success", true, _logger?.LogFilePath);
            Log("Deployment recorded as Deployed.");

            return new DeploymentRunResult(true, "Deployment succeeded.", false, backupFolder, log);
        }
        catch (Exception ex)
        {
            Log($"Exception during deploy: {ex.Message}. Rolling back...");
            LogError($"Deployment failed: {ex}");
            await RollBackAsync(backupFolder, request, preMergeAppSettings, hooks, Log);
            await _registry.RecordRunCompleteAsync(run.RunId, "Failed", null, _logger?.LogFilePath);
            return new DeploymentRunResult(false, $"Deployment failed: {ex.Message} — rolled back.", true, backupFolder, log);
        }
    }

    private static async Task RollBackAsync(
        string backupFolder,
        DeploymentRunRequest request,
        string? preMergeAppSettings,
        DeploymentHooks hooks,
        Action<string> log)
    {
        var backupMgr = new BackupManager();
        backupMgr.Rollback(backupFolder);
        log("Files restored from backup.");

        if (preMergeAppSettings is not null)
        {
            await File.WriteAllTextAsync(request.AppSettingsPath, preMergeAppSettings);
            log("appsettings.json restored to pre-deploy content.");
        }

        // Best-effort: bring the site back up even after a rollback, so a
        // failed deploy doesn't leave production down.
        if (hooks.StartSite is not null)
        {
            await hooks.StartSite();
            log("Site restarted after rollback.");
        }
    }
}
