namespace DeployToolkit.Core.Targets.Plesk;

/// <summary>
/// The seam that makes the whole Plesk target testable: the executor only
/// ever talks to this abstraction, so self-tests run against a recording
/// fake while production runs <see cref="SftpFileUploader"/> over SSH.NET.
///
/// Every path accepted/returned here is a REMOTE POSIX path (forward
/// slashes), except <see cref="UploadFileAsync"/>'s <paramref name="localPath"/>,
/// which is a local filesystem path using the platform's separators.
///
/// Implementations may be single-use-per-deployment; the executor creates
/// one per run and does NOT dispose it — the caller owns the lifetime.
/// </summary>
public interface IPleskFileUploader : IDisposable
{
    /// <summary>Uploads (and overwrites) a local file at the remote path.</summary>
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the remote directory (and every missing parent segment)
    /// exists. Must be idempotent — the executor calls it once per distinct
    /// directory per deployment.
    /// </summary>
    Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>True when the remote path exists.</summary>
    Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Deletes the remote file.</summary>
    Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default);
}
