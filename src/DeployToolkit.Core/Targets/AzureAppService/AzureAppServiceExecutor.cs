using System.IO.Compression;
using DeployToolkit.Core.Manifest;

namespace DeployToolkit.Core.Targets.AzureAppService;

/// <summary>
/// The Wave-2 Azure App Service executor (plan §7): streams a zip of the
/// package's files to Kudu ZipDeploy, then applies the manifest's
/// appsettings delta through the ARM Configuration API. Pure HttpClient —
/// runs from the operator's machine with no RDP session involved.
///
/// Health checking is deliberately NOT part of the executor: the Deployer
/// orchestrator owns the stop/health/record flow (and Azure apps don't
/// need a stop step — Kudu handles atomicity per deployment).
/// </summary>
public sealed class AzureAppServiceExecutor(
    KuduClient kudu,
    AzureAppSettingsClient? appSettingsClient = null,
    AzureTargetSettings? armTarget = null) : IDeploymentExecutor
{
    public TargetType TargetType => TargetType.AzureAppService;

    public async Task<DeploymentResult> DeployAsync(
        ComponentManifest manifest,
        string extractedFilesRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        progress?.Report($"Preparing zip with {manifest.Files.Count} file(s) for Kudu zip deploy...");
        using var zipStream = BuildZipFromPackageFiles(extractedFilesRoot, manifest.Files);

        progress?.Report("Uploading to Kudu zip deploy (isAsync=false)...");
        var deploy = await kudu.DeployZipAsync(zipStream, isAsync: false, cancellationToken).ConfigureAwait(false);
        if (!deploy.Success)
            return new DeploymentResult(
                Success: false,
                Message: $"Kudu zip deploy failed (HTTP {deploy.HttpStatus}).{(string.IsNullOrWhiteSpace(deploy.ResponseBody) ? "" : " Response: " + Truncate(deploy.ResponseBody))}",
                HealthCheckPassed: false);
        progress?.Report(deploy.DeploymentId is null
            ? "Zip deploy accepted by Kudu."
            : $"Zip deploy accepted by Kudu (deployment {deploy.DeploymentId}).");

        if (manifest.AppSettingsDelta.Count > 0)
        {
            if (appSettingsClient is null || armTarget is null)
            {
                progress?.Report(
                    $"Files deployed, but {manifest.AppSettingsDelta.Count} appsettings delta key(s) were NOT applied — " +
                    "no ARM settings client/target configured.");
            }
            else
            {
                progress?.Report("Fetching current app settings from ARM...");
                var current = await appSettingsClient.GetAppSettingsAsync(armTarget, cancellationToken).ConfigureAwait(false);
                var merged = AzureAppSettingsClient.MergeDelta(current, manifest.AppSettingsDelta);

                progress?.Report($"Putting {merged.Count} app setting(s) back via ARM (delta: {manifest.AppSettingsDelta.Count} key(s))...");
                var putOk = await appSettingsClient.PutAppSettingsAsync(armTarget, merged, cancellationToken).ConfigureAwait(false);
                if (!putOk)
                    return new DeploymentResult(
                        Success: false,
                        Message: "Files deployed, but the app settings update via ARM failed. " +
                                 "Files are in place — retry just the settings step before restarting traffic.",
                        HealthCheckPassed: false);
                progress?.Report("App settings applied.");
            }
        }

        return new DeploymentResult(
            Success: true,
            Message: $"Deployed {manifest.Files.Count} file(s) to Azure App Service via Kudu zip deploy.",
            HealthCheckPassed: false);
    }

    /// <summary>Packages exactly the manifest's files (relative, forward-
    /// slash paths preserved as zip entry names — Kudu's deployment script
    /// syncs the zip content over the wwwroot). Fails loudly on a missing
    /// local file: a partial upload would look like a successful deploy.</summary>
    internal static MemoryStream BuildZipFromPackageFiles(string extractedFilesRoot, IReadOnlyList<ManifestFile> files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var localPath = Path.Combine(extractedFilesRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(localPath))
                    throw new FileNotFoundException($"Package file missing locally: {localPath}", localPath);
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var source = File.OpenRead(localPath);
                source.CopyTo(entryStream);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";
}
