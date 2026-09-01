using DeployToolkit.Core.Database;
using DeployToolkit.Core.Deployment;
using DeployToolkit.Core.IisControl;
using DeployToolkit.Core.Packaging;

namespace DeployToolkit.Core.Windows;

/// <summary>
/// Wires the tested engines into the exact <see cref="DeploymentHooks"/> the
/// <see cref="DeploymentOrchestrator"/> consumes for an IIS deployment —
/// this is the composition root for the Deployer's IIS flow (plan §7),
/// previously only assembled ad hoc in tests. Everything Windows-specific
/// funnels through <see cref="IIisController"/> and
/// <see cref="SqlServerScriptRunner"/>, both injectable for headless tests.
/// </summary>
public static class IisDeploymentHooksFactory
{
    /// <summary>Stop/start hooks driven by the app-pool-first,
    /// app_offline-fallback strategy. The controller is invoked on a worker
    /// thread (MWA is synchronous) so the UI thread stays responsive.</summary>
    public static DeploymentHooks CreateStopStartHooks(
        IIisController controller,
        string? appPoolName,
        string siteRoot,
        IisStopStrategy strategy = IisStopStrategy.Auto)
    {
        var stopController = new IisSiteStopController(controller, appPoolName, siteRoot, strategy);
        return new DeploymentHooks(
            StopSite: () => Task.Run(() => stopController.Stop()),
            StartSite: () => Task.Run(() => stopController.Start()));
    }

    /// <summary>
    /// The plan §7 step 7 hook: for each manifest DbScriptRef, reads the
    /// script text straight out of the package zip (db/{file}) — scripts
    /// are never extracted to disk on the target machine — and runs it
    /// through the Phase 8 runner against the given connection string.
    /// Throws on the first failed script so the orchestrator's rollback
    /// path kicks in.
    /// </summary>
    /// <param name="connectionString">Target database connection string
    /// (kept in a SecretVault, resolved at deploy time — never stored in
    /// the registry in plain text).</param>
    /// <param name="options">Runner options (transaction policy, timeout,
    /// continue-on-error).</param>
    /// <param name="progress">Optional progress sink for the Deployer UI.</param>
    public static Func<string, IReadOnlyList<Core.Manifest.DbScriptRef>, Task> CreateDbScriptsHook(
        string connectionString,
        SqlScriptRunnerOptions? options = null,
        IProgress<string>? progress = null)
    {
        return async (zipPath, scripts) =>
        {
            foreach (var script in scripts)
            {
                var entryPath = "db/" + script.File.Replace('\\', '/');
                progress?.Report($"Reading {entryPath} from package...");
                var scriptText = PackageReader.ReadEntryText(zipPath, entryPath);

                progress?.Report($"Executing {script.File} ({script.Kind}) against SQL Server...");
                var report = await SqlServerScriptRunner.ExecuteAsync(
                    connectionString, scriptText, script.File, options, null).ConfigureAwait(false);

                if (!report.Success)
                    throw new InvalidOperationException(
                        $"DB script '{script.File}' failed: {report.FirstError} " +
                        $"(batches: {report.Batches.Count}, rolled back: {report.RolledBack}). " +
                        "Deployment aborted — restore the DB from the pre-deploy snapshot/scripts if needed.");

                progress?.Report(
                    $"{script.File}: {report.Batches.Count} batch(es) in {report.TotalDuration.TotalSeconds:F1}s " +
                    $"(transaction: {!report.RolledBack}).");
            }
        };
    }
}
