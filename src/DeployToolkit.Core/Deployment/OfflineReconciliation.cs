using System.Text.Json;
using System.Text.Json.Serialization;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Core.Deployment;

/// <summary>
/// The outcome of one deployment run, written by the Deployer when the
/// central registry is unreachable from inside the client's network —
/// the "offline fallback" from plan §2.2. The Packager reads these files
/// later with <see cref="OfflineReconciler"/> and replays them into the
/// registry, including the status flip to Deployed.
/// </summary>
public sealed record OfflineRunResult(
    int SchemaVersion,
    string PackageId,
    string ComponentId,          // registry lookup key (the registry is keyed by id, not name)
    string Client,
    string Component,            // display name, e.g. "CMS"
    string Result,               // Success | Failed | RolledBack (matches DeploymentRuns.Result)
    bool HealthCheckResult,
    string Message,
    string DeployedBy,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    IReadOnlyList<string> LogLines)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Writes/reads offline run results as one self-contained JSON file per
/// package in a folder of the operator's choosing (suggested default:
/// Documents\DeployToolkit\OfflineResults on the target machine).
/// </summary>
public static class OfflineResultWriter
{
    public const string FileSuffix = ".offline-result.json";
    public const string LogFileSuffix = ".deploy.log";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Writes {PackageId}.offline-result.json (atomically: temp
    /// file + move, so a half-written file can never be reconciled) and,
    /// when the run produced log lines, {PackageId}.deploy.log alongside
    /// it so the audit trail survives the offline trip.</summary>
    public static async Task<string> WriteAsync(string folder, OfflineRunResult result)
    {
        Directory.CreateDirectory(folder);

        var resultPath = Path.Combine(folder, result.PackageId + FileSuffix);
        var json = JsonSerializer.Serialize(result, Options);
        await WriteAtomicallyAsync(resultPath, json).ConfigureAwait(false);

        if (result.LogLines.Count > 0)
        {
            var logPath = Path.Combine(folder, result.PackageId + LogFileSuffix);
            await File.WriteAllLinesAsync(logPath, result.LogLines).ConfigureAwait(false);
        }

        return resultPath;
    }

    /// <summary>Returns null for a file that doesn't parse — the
    /// reconciler reports unparseable files as errors instead of guessing.</summary>
    public static async Task<OfflineRunResult?> TryReadAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JsonSerializer.Deserialize<OfflineRunResult>(json, Options);
        }
        catch (Exception) when (File.Exists(path))
        {
            return null;
        }
    }

    private static async Task WriteAtomicallyAsync(string targetPath, string content)
    {
        var tempPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(tempPath, content).ConfigureAwait(false);
        File.Move(tempPath, targetPath, overwrite: true);
    }
}

public sealed record OfflineReconciliationReport(
    int Reconciled,
    int Skipped,
    IReadOnlyList<string> Errors);

/// <summary>
/// Replays offline deployment results back into the central registry —
/// the Packager side of the offline fallback (plan §2.2 / §9). Safe to run
/// repeatedly:
///  - results already reconciled are skipped via a {PackageId}.reconciled
///    marker written next to the result file, and
///  - a package that is already Deployed in the registry is skipped even
///    without a marker (e.g. reconciled on another machine).
/// Only a successful run flips the package to Deployed — Failed/RolledBack
/// runs are recorded as run history while the package stays Created so it
/// can be redeployed.
/// </summary>
public sealed class OfflineReconciler(IRegistryStore registry)
{
    private const string ReconciledMarkerSuffix = ".reconciled";

    public async Task<OfflineReconciliationReport> ReconcileAsync(
        string folder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
            return new OfflineReconciliationReport(0, 0, new[] { $"Offline results folder does not exist: {folder}" });

        int reconciled = 0, skipped = 0;
        var errors = new List<string>();

        var resultFiles = Directory.GetFiles(folder, "*" + OfflineResultWriter.FileSuffix);
        progress?.Report($"Found {resultFiles.Length} offline result file(s) in {folder}.");

        foreach (var path in resultFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ReconcileOneAsync(path, folder, progress, cancellationToken).ConfigureAwait(false);
                switch (outcome)
                {
                    case ReconcileOutcome.Reconciled: reconciled++; break;
                    case ReconcileOutcome.Skipped: skipped++; break;
                }
            }
            catch (Exception ex)
            {
                var message = $"Failed to reconcile '{Path.GetFileName(path)}': {ex.Message}";
                errors.Add(message);
                progress?.Report(message);
            }
        }

        return new OfflineReconciliationReport(reconciled, skipped, errors);
    }

    private async Task<ReconcileOutcome> ReconcileOneAsync(
        string path, string folder, IProgress<string>? progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);

        var marker = Path.Combine(folder, fileName[..^OfflineResultWriter.FileSuffix.Length] + ReconciledMarkerSuffix);
        if (File.Exists(marker))
        {
            progress?.Report($"Skipping {fileName} — already reconciled.");
            return ReconcileOutcome.Skipped;
        }

        var result = await OfflineResultWriter.TryReadAsync(path).ConfigureAwait(false);
        if (result is null)
            throw new InvalidDataException("File is not a readable offline run result.");
        if (string.IsNullOrWhiteSpace(result.PackageId))
            throw new InvalidDataException("Offline run result has no PackageId.");

        // Locate the package in the registry. The registry never lists
        // everything (by design), so look at the two places a package can be:
        // among the component's undeployed packages, or already deployed.
        var undeployed = await registry.GetUndeployedPackagesAsync(result.ComponentId).ConfigureAwait(false);
        var package = undeployed.FirstOrDefault(p => p.PackageId == result.PackageId);

        if (package is null)
        {
            var latestDeployed = await registry.GetLatestDeployedPackageAsync(result.ComponentId).ConfigureAwait(false);
            if (latestDeployed is not null && latestDeployed.PackageId == result.PackageId)
            {
                WriteMarker(marker);
                progress?.Report($"Skipping {fileName} — package already Deployed in registry.");
                return ReconcileOutcome.Skipped;
            }
            throw new KeyNotFoundException(
                $"Package {result.PackageId} not found for component '{result.ComponentId}' ({result.Client}/{result.Component}). " +
                "The package may belong to a different registry.");
        }

        // Run history: recreate the run record so DeploymentRuns tells the
        // whole story even though the deploy happened while offline.
        var run = await registry.RecordRunStartAsync(result.PackageId, result.StartedUtc).ConfigureAwait(false);
        var logPath = Path.Combine(folder, result.PackageId + OfflineResultWriter.LogFileSuffix);
        await registry.RecordRunCompleteAsync(
            run.RunId,
            result.Result,
            result.HealthCheckResult,
            File.Exists(logPath) ? logPath : null).ConfigureAwait(false);

        if (string.Equals(result.Result, "Success", StringComparison.OrdinalIgnoreCase))
        {
            await registry.MarkDeployedAsync(
                result.PackageId,
                result.DeployedBy,
                result.CompletedUtc ?? result.StartedUtc).ConfigureAwait(false);
            progress?.Report($"Marked package {result.PackageId} ({result.Component} {result.Client}) as Deployed.");
        }
        else
        {
            progress?.Report(
                $"Recorded unsuccessful run for package {result.PackageId} (result={result.Result}); " +
                "package left as Created so it can be redeployed.");
        }

        WriteMarker(marker);
        return ReconcileOutcome.Reconciled;
    }

    private static void WriteMarker(string markerPath) =>
        File.WriteAllText(markerPath, $"reconciled {DateTimeOffset.UtcNow:O}");
}

internal enum ReconcileOutcome
{
    Reconciled,
    Skipped,
}
