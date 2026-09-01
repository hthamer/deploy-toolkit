namespace DeployToolkit.Core.Targets.Plesk;

/// <summary>
/// Drops/removes <c>app_offline.htm</c> at the site root — the well-known
/// ASP.NET filename that stops the application (and shows a maintenance
/// page) while files are being replaced. Works through the same
/// <see cref="IPleskFileUploader"/> seam as the deploy itself, so it is
/// fully testable without a server and needs no IIS/Plesk API rights.
///
/// Note (plan §7): this stops ASP.NET (Core/Framework) apps only. PHP or
/// other runtimes keep running — use <see cref="PleskRestartMode.XmlApi"/>
/// (after validation) for those.
/// </summary>
public static class PleskAppOfflineHelper
{
    /// <summary>The filename ASP.NET recognizes as "take the app offline".</summary>
    public const string AppOfflineFileName = "app_offline.htm";

    /// <summary>
    /// Friendly maintenance content served while the site is offline. Small
    /// on purpose: it is uploaded over SFTP on every AppOffline deploy.
    /// </summary>
    public const string DefaultOfflineContent =
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>Brief maintenance</title>
        </head>
        <body>
          <h1>We'll be back in a moment</h1>
          <p>This site is briefly in maintenance mode while an update is applied.
             Please refresh this page in a minute or two.</p>
        </body>
        </html>
        """;

    /// <summary>
    /// Uploads <c>{remoteRoot}/app_offline.htm</c>. The content is written to
    /// a local temp file, uploaded, and the temp file is always cleaned up.
    /// </summary>
    public static async Task DropAsync(IPleskFileUploader uploader, string remoteRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteRoot);

        var remotePath = PleskRemotePaths.Join(remoteRoot, AppOfflineFileName);

        var tempFile = Path.Combine(
            Path.GetTempPath(),
            "deploytoolkit-app_offline-" + Guid.NewGuid().ToString("N") + ".htm");
        try
        {
            await File.WriteAllTextAsync(tempFile, DefaultOfflineContent, cancellationToken).ConfigureAwait(false);
            // Ensure the root exists before the upload; the uploader's
            // segment walk makes this a no-op when it already does.
            await uploader.CreateDirectoryAsync(remoteRoot, cancellationToken).ConfigureAwait(false);
            await uploader.UploadFileAsync(tempFile, remotePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// Deletes <c>{remoteRoot}/app_offline.htm</c> — but only if it is
    /// actually there, so calling this on a site that was never dropped is a
    /// harmless no-op.
    /// </summary>
    public static async Task RemoveAsync(IPleskFileUploader uploader, string remoteRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteRoot);

        var remotePath = PleskRemotePaths.Join(remoteRoot, AppOfflineFileName);
        if (await uploader.FileExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            await uploader.DeleteFileAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }
    }
}
