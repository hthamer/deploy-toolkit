using System.Text;
using Renci.SshNet;

namespace DeployToolkit.Core.Targets.Plesk;

/// <summary>
/// Real <see cref="IPleskFileUploader"/> backed by SSH.NET's
/// <see cref="Renci.SshNet.SftpClient"/> (package "SSH.NET", namespace
/// Renci.SshNet).
///
/// Plan §1 constraint, restated: this class NEVER spawns a process, never
/// runs PowerShell/batch/bash, never shells out to ftp.exe/pscp.exe — all
/// traffic is the SFTP protocol spoken in-process by the pure managed SSH.NET
/// library. Nothing at all is required on the target Plesk server beyond the
/// SFTP account every Plesk plan exposes.
///
/// Connection behavior: lazy — the SSH session is opened on the first
/// operation, <see cref="Dispose"/> closes it. Operations are offloaded to
/// the thread pool so a UI (Packager/Deployer) thread never blocks on
/// network I/O; note SSH.NET's synchronous API cannot abort mid-operation
/// on cancellation, so the token is honoured between operations.
/// </summary>
public sealed class SftpFileUploader : IPleskFileUploader
{
    private readonly PleskConnectionOptions _options;
    private SftpClient? _client;

    public SftpFileUploader(PleskConnectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        // Guard BEFORE any connection attempt — a missing local file is a
        // local packaging mistake and must not open an SSH session.
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException($"Local file to upload was not found: {localPath}", localPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() =>
        {
            var client = GetConnectedClient();
            using var input = File.OpenRead(localPath);
            // canOverride: true — a delta deployment overwrites changed files.
            client.UploadFile(input, remotePath, canOverride: true);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() =>
        {
            var client = GetConnectedClient();
            // Walk each POSIX segment and mkdir only what's missing — makes
            // the operation idempotent and safe to call once per distinct
            // directory per deployment.
            var current = new StringBuilder();
            foreach (var segment in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current.Append('/').Append(segment);
                var directory = current.ToString();
                if (!client.Exists(directory))
                {
                    client.CreateDirectory(directory);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => GetConnectedClient().Exists(remotePath), cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => GetConnectedClient().DeleteFile(remotePath), cancellationToken);
    }

    private SftpClient GetConnectedClient()
    {
        if (_client is null)
        {
            _client = new SftpClient(BuildConnectionInfo());
        }

        if (!_client.IsConnected)
        {
            _client.Connect();
        }

        return _client;
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        // Private key when configured (preferred), otherwise password auth.
        AuthenticationMethod authentication = string.IsNullOrWhiteSpace(_options.PrivateKeyPath)
            ? new PasswordAuthenticationMethod(_options.Username, _options.Password ?? string.Empty)
            : new PrivateKeyAuthenticationMethod(_options.Username, new PrivateKeyFile(_options.PrivateKeyPath));

        return new ConnectionInfo(_options.Host, _options.Port, _options.Username, authentication);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}
