namespace DeployToolkit.Core.Targets.Plesk;

/// <summary>
/// How the executor handles "restart the app after the changed files are in
/// place". Plan §7 flags restart behavior as the least certain part of the
/// Plesk target: it varies a lot by Plesk configuration — some setups recycle
/// the application automatically when files change, others need an explicit
/// kick. Every non-<see cref="None"/> mode is cleanly pluggable and MUST be
/// validated against a real client before being trusted (see the README).
/// </summary>
public enum PleskRestartMode
{
    /// <summary>
    /// Do nothing after uploading. Use when the hosting setup recycles the
    /// app on file change (common on Plesk) or for purely static content.
    /// </summary>
    None,

    /// <summary>
    /// Drop <c>app_offline.htm</c> into the site root before uploading and
    /// remove it afterwards. Well-known ASP.NET shutdown mechanism that also
    /// guarantees visitors see a maintenance page during the deploy. Pure
    /// file transfer, no Plesk API involved — but it only stops ASP.NET
    /// (Core/Framework) apps, not PHP/Node/etc.
    /// </summary>
    AppOffline,

    /// <summary>
    /// POST a restart request packet to the Plesk XML API
    /// (<c>/enterprise/control/agent.php</c>) after uploading. The exact
    /// packet needs validation against a real Plesk instance — it is a
    /// one-line-swappable public constant, see
    /// <see cref="PleskXmlApiClient.DefaultRestartPacketTemplate"/>.
    /// </summary>
    XmlApi,
}

/// <summary>
/// SSH/SFTP endpoint credentials for one Plesk subscription. Plesk exposes
/// SFTP on essentially every plan (Websites &amp; Domains → FTP/SSH access);
/// a private key is preferred over a password when the client allows it.
/// </summary>
public sealed record PleskConnectionOptions(
    string Host,
    int Port = 22,
    string Username = "",
    string? Password = null,
    string? PrivateKeyPath = null);

/// <summary>
/// Deployment options for one Plesk target.
/// </summary>
/// <param name="RemoteRootPath">
/// Absolute POSIX path of the site root on the server, e.g. "/httpdocs"
/// (main subscription) or "/subdomains/foo/httpdocs". Always forward
/// slashes — this is a remote SFTP path, never a local one.
/// </param>
/// <param name="RestartMode">See <see cref="PleskRestartMode"/>.</param>
/// <param name="XmlApiBaseUrl">
/// Plesk panel base URL for XML API calls, e.g. "https://plesk.example.com:8443".
/// Required when <paramref name="RestartMode"/> is <see cref="PleskRestartMode.XmlApi"/>.
/// </param>
/// <param name="XmlApiLogin">Plesk API login (required for XmlApi mode).</param>
/// <param name="XmlApiPassword">Plesk API password / secret key (required for XmlApi mode).</param>
/// <param name="SiteId">
/// Informational Plesk site identifier; included verbatim in the API packet
/// via the {{SITE_ID}} placeholder of the restart template.
/// </param>
public sealed record PleskDeployOptions(
    string RemoteRootPath,
    PleskRestartMode RestartMode = PleskRestartMode.None,
    string? XmlApiBaseUrl = null,
    string? XmlApiLogin = null,
    string? XmlApiPassword = null,
    string? SiteId = null)
{
    // Construction-time guard: an invalid remote root is a configuration
    // error and must fail immediately, not mid-deployment. (Note: a
    // `with { RemoteRootPath = ... }` mutation bypasses this initializer —
    // Validate() below re-checks and is called by the executor.)
    public string RemoteRootPath { get; init; } = ValidateRemoteRootPath(RemoteRootPath);

    private static string ValidateRemoteRootPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('/'))
        {
            throw new ArgumentException(
                $"RemoteRootPath must be an absolute POSIX path starting with '/' (e.g. \"/httpdocs\"); got \"{value}\".",
                nameof(RemoteRootPath));
        }

        return value;
    }

    /// <summary>
    /// Full validation, including the restart-mode-specific settings. The
    /// executor calls this as a pre-flight step. Throws
    /// <see cref="ArgumentException"/> / <see cref="InvalidOperationException"/>
    /// with an actionable message when something is wrong.
    /// </summary>
    public void Validate()
    {
        ValidateRemoteRootPath(RemoteRootPath);

        if (RestartMode == PleskRestartMode.XmlApi)
        {
            if (string.IsNullOrWhiteSpace(XmlApiBaseUrl))
            {
                throw new InvalidOperationException(
                    "RestartMode.XmlApi requires XmlApiBaseUrl (e.g. \"https://plesk.example.com:8443\").");
            }

            if (string.IsNullOrEmpty(XmlApiLogin) || XmlApiPassword is null)
            {
                throw new InvalidOperationException(
                    "RestartMode.XmlApi requires XmlApiLogin and XmlApiPassword (Plesk panel → profile → API keys, or a Plesk user's credentials).");
            }
        }
    }
}

/// <summary>
/// Remote-path helpers. All Plesk remote paths are POSIX (forward slashes),
/// exactly as the manifest stores them; local paths use the platform's
/// directory separator. This mapping is the core contract of the Plesk
/// target and is covered by self-tests.
/// </summary>
internal static class PleskRemotePaths
{
    /// <summary>
    /// Joins the remote root ("/httpdocs") with a manifest-relative path
    /// ("bin/App.dll") into "/httpdocs/bin/App.dll".
    /// </summary>
    public static string Join(string root, string relativePath)
    {
        var trimmedRoot = root.TrimEnd('/');
        return trimmedRoot.Length == 0
            ? "/" + relativePath // degenerate root "/"
            : trimmedRoot + "/" + relativePath;
    }

    /// <summary>
    /// Parent directory of a remote path ("/httpdocs/bin/App.dll" →
    /// "/httpdocs/bin"), or empty when the file sits directly under the
    /// filesystem root.
    /// </summary>
    public static string ParentDirectory(string remotePath)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : remotePath[..lastSlash];
    }
}
