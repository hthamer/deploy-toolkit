using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Targets;

namespace DeployToolkit.Core.Targets.Plesk;

/// <summary>
/// Deployment target executor for Plesk shared hosting (plan §7 / Phase 13).
///
/// Strategy: upload the delta files over SFTP (every Plesk plan exposes it),
/// optionally make the restart explicit via app_offline.htm or the Plesk XML
/// API. Pure in-process .NET + SSH.NET — no scripts, no shell-outs, ever.
///
/// Restart behavior is the least certain part of this target (plan §7): it
/// varies a lot by Plesk configuration, so every mode is cleanly pluggable
/// and documented as needing validation against a real client. See README.
///
/// Path contract (the key tested logic): manifest paths are POSIX
/// ("bin/App.dll", forward slashes, relative to the publish root). Local
/// resolution joins them with the platform separator onto
/// <paramref name="extractedFilesRoot"/>; remote paths are
/// <c>RemoteRootPath.TrimEnd('/') + "/" + manifestPath</c>, always forward
/// slashes.
///
/// Guarantees:
///  - app_offline.htm is NEVER left on the server after a failed deploy
///    (steps are wrapped; the removal runs in every failure path).
///  - a failed restart does not throw away the deployed file state — the
///    result is Success=false with "files are deployed but restart failed",
///    because the new files are already in place.
///  - HealthCheckPassed is always false: the health check belongs to the
///    orchestrator/UI layer (manifest.HealthCheckUrl), not the executor.
///  - the executor never disposes the uploader — the caller owns it.
/// </summary>
public sealed class PleskExecutor : IDeploymentExecutor
{
    private readonly IPleskFileUploader _uploader;
    private readonly PleskDeployOptions _options;
    private readonly HttpClient? _xmlApiHttpClient;

    /// <param name="uploader">
    /// File transfer seam — use <see cref="SftpFileUploader"/> in production,
    /// a recording fake in tests.
    /// </param>
    /// <param name="options">Target options (validated as a pre-flight step).</param>
    /// <param name="xmlApiHttpClient">
    /// Optional HttpClient for <see cref="PleskRestartMode.XmlApi"/> restarts
    /// (injectable for tests). When null and XmlApi mode is selected, a
    /// short-lived HttpClient is created and disposed per deployment — fine
    /// for a one-shot deploy tool.
    /// </param>
    public PleskExecutor(IPleskFileUploader uploader, PleskDeployOptions options, HttpClient? xmlApiHttpClient = null)
    {
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _xmlApiHttpClient = xmlApiHttpClient;
    }

    /// <inheritdoc/>
    public TargetType TargetType => TargetType.Plesk;

    /// <inheritdoc/>
    public async Task<DeploymentResult> DeployAsync(
        ComponentManifest manifest,
        string extractedFilesRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        // Pre-flight: full option validation (root path + restart settings)
        // BEFORE anything is uploaded.
        try
        {
            _options.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Failure($"Plesk options are invalid: {ex.Message}");
        }

        // ---- Step 0: maintenance page (AppOffline restart mode only) ----
        var offlineDropped = false;
        if (_options.RestartMode == PleskRestartMode.AppOffline)
        {
            try
            {
                await PleskAppOfflineHelper.DropAsync(_uploader, _options.RemoteRootPath, cancellationToken).ConfigureAwait(false);
                offlineDropped = true;
                progress?.Report($"Plesk: dropped app_offline.htm at {_options.RemoteRootPath} (site in maintenance mode)");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // DropAsync may have failed mid-upload, leaving a partial
                // app_offline.htm — attempt the removal regardless.
                var cleanup = await TryRemoveOfflineAsync(attempt: true).ConfigureAwait(false);
                return Failure($"Deployment to Plesk failed while dropping app_offline.htm: {ex.Message}{cleanup}");
            }
        }

        // ---- Steps 1+2: upload changed files, delete removed files ----
        var uploaded = 0;
        var deleted = 0;
        try
        {
            // One mkdir per distinct parent directory across the whole
            // deployment; the uploader's per-segment walk keeps it idempotent.
            var createdDirectories = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Manifest → local: forward slashes become platform separators.
                var localPath = Path.Combine(extractedFilesRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
                // Manifest → remote: keep forward slashes exactly as stored.
                var remotePath = PleskRemotePaths.Join(_options.RemoteRootPath, file.Path);

                var parentDirectory = PleskRemotePaths.ParentDirectory(remotePath);
                if (parentDirectory.Length > 0 && createdDirectories.Add(parentDirectory))
                {
                    await _uploader.CreateDirectoryAsync(parentDirectory, cancellationToken).ConfigureAwait(false);
                }

                await _uploader.UploadFileAsync(localPath, remotePath, cancellationToken).ConfigureAwait(false);
                uploaded++;
                progress?.Report($"Plesk: uploaded {file.Path} -> {remotePath}");
            }

            foreach (var deletedFile in manifest.DeletedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remotePath = PleskRemotePaths.Join(_options.RemoteRootPath, deletedFile);
                if (await _uploader.FileExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
                {
                    await _uploader.DeleteFileAsync(remotePath, cancellationToken).ConfigureAwait(false);
                    deleted++;
                    progress?.Report($"Plesk: deleted {remotePath}");
                }
                else
                {
                    progress?.Report($"Plesk: skipped delete of {remotePath} (not present on server)");
                }
            }
        }
        catch (OperationCanceledException)
        {
            await TryRemoveOfflineAsync(offlineDropped).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var cleanup = await TryRemoveOfflineAsync(offlineDropped).ConfigureAwait(false);
            return Failure(
                $"Deployment to Plesk failed after {uploaded} of {manifest.Files.Count} file(s) uploaded: {ex.Message}{cleanup}");
        }

        // ---- Step 3: restart / bring the site back ----
        switch (_options.RestartMode)
        {
            case PleskRestartMode.None:
                break;

            case PleskRestartMode.AppOffline:
                try
                {
                    await PleskAppOfflineHelper.RemoveAsync(_uploader, _options.RemoteRootPath, cancellationToken).ConfigureAwait(false);
                    progress?.Report("Plesk: removed app_offline.htm (site back online)");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new DeploymentResult(
                        Success: false,
                        Message: $"Files are deployed to {_options.RemoteRootPath} ({uploaded} uploaded, {deleted} deleted) but app_offline.htm could not be removed afterwards: {ex.Message} — the site is still in maintenance mode; delete app_offline.htm via the Plesk file manager.",
                        HealthCheckPassed: false);
                }

                break;

            case PleskRestartMode.XmlApi:
                var (restartOk, restartMessage) = await RestartViaXmlApiAsync(cancellationToken).ConfigureAwait(false);
                if (!restartOk)
                {
                    // Files are already in place — do not throw that state
                    // away silently; tell the operator exactly that.
                    return new DeploymentResult(
                        Success: false,
                        Message: $"Files are deployed to {_options.RemoteRootPath} ({uploaded} uploaded, {deleted} deleted) but the Plesk XML API restart failed: {restartMessage} The new files are already in place — restart the site from the Plesk panel and re-run the health check.",
                        HealthCheckPassed: false);
                }

                break;

            default:
                return Failure($"Unknown PleskRestartMode: {_options.RestartMode}");
        }

        return new DeploymentResult(
            Success: true,
            Message: $"Deployed to Plesk {_options.RemoteRootPath}: {uploaded} file(s) uploaded, {deleted} file(s) deleted, restart mode {_options.RestartMode}. Health check is performed by the orchestrator/UI layer, not the executor.",
            HealthCheckPassed: false);
    }

    private async Task<(bool Ok, string Message)> RestartViaXmlApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var apiClient = _xmlApiHttpClient is not null
                ? new PleskXmlApiClient(_xmlApiHttpClient)
                : new PleskXmlApiClient(new HttpClient(), ownsHttpClient: true);

            var result = await apiClient.SendRestartRequestAsync(_options, cancellationToken).ConfigureAwait(false);
            return result.Success
                ? (true, $"HTTP {result.HttpStatus}")
                : (false, $"HTTP {result.HttpStatus}: {TruncateForLog(result.ResponseBody)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Best-effort removal of app_offline.htm after ANY failure — the site
    /// must never stay in maintenance mode because a deploy went wrong.
    /// Uses CancellationToken.None so the cleanup also runs on cancellation.
    /// Returns a warning string for the failure message when the cleanup
    /// itself failed, null on success / nothing to do.
    /// </summary>
    /// <param name="attempt">
    /// True when an app_offline.htm was (or may have been) dropped in this
    /// run — including a partially failed DropAsync, which can leave the
    /// file behind.
    /// </param>
    private async Task<string?> TryRemoveOfflineAsync(bool attempt)
    {
        if (!attempt)
        {
            return null;
        }

        try
        {
            await PleskAppOfflineHelper.RemoveAsync(_uploader, _options.RemoteRootPath).ConfigureAwait(false);
            return null;
        }
        catch (Exception cleanupEx)
        {
            return $" WARNING: app_offline.htm could not be removed afterwards ({cleanupEx.Message}) — the site may still show the maintenance page; delete {_options.RemoteRootPath}/app_offline.htm via the Plesk file manager.";
        }
    }

    private static string TruncateForLog(string value, int maxLength = 400) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static DeploymentResult Failure(string message) => new(Success: false, Message: message, HealthCheckPassed: false);
}
