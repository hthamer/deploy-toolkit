namespace DeployToolkit.Deployer;

/// <summary>
/// One place for every filesystem location the Deployer owns. All roots fall
/// back to the current directory on hosts without the matching special
/// folder (same defensive pattern as the Packager shell) so the app always
/// starts, even on stripped-down servers.
/// </summary>
internal static class DeployerPaths
{
    /// <summary>Documents\DeployToolkit\Logs — the RunLogger root (plan §8.6
    /// "structured log per run"; actual files live under
    /// {client}/{component}/{timestamp}-{id}.log beneath this root).</summary>
    public static string LogsRoot => CombineWithDocuments("DeployToolkit", "Logs");

    /// <summary>Documents\DeployToolkit\OfflineResults — where offline-mode
    /// run results are written for the Packager's "Reconcile Offline
    /// Results…" flow (plan §2.2 / §10).</summary>
    public static string OfflineResultsRoot => CombineWithDocuments("DeployToolkit", "OfflineResults");

    /// <summary>Documents\Backups — the standalone-rollback browser's
    /// default folder; matches BackupManager's own convention (plan §4).</summary>
    public static string DefaultBackupsRoot => CombineWithDocuments("Backups");

    /// <summary>
    /// %APPDATA%\DeployToolkit\deployer-registry.json — the Deployer's own
    /// registry connection settings (same shape as the Packager's, separate
    /// file so each tool remembers its own last connection).
    /// </summary>
    public static string SettingsPath => CombineWithAppData("DeployToolkit", "deployer-registry.json");

    /// <summary>
    /// %APPDATA%\DeployToolkit\deployer-vault.json — the local SecretVault
    /// file holding encrypted secrets referenced by a component's
    /// <c>DbConnectionRef</c> (vault://name, plan §2.2). Lives only on the
    /// target machine; the registry never holds plaintext secrets.
    /// </summary>
    public static string VaultPath => CombineWithAppData("DeployToolkit", "deployer-vault.json");

    /// <summary>%LOCALAPPDATA%\DeployToolkit\iis-targets.json — machine-local
    /// component → IIS site/app mappings (plan §6), the location suggested by
    /// <see cref="DeployToolkit.Core.IisControl.IisTargetMappingStore"/>.</summary>
    public static string IisTargetsPath => CombineWithLocalAppData("DeployToolkit", "iis-targets.json");

    /// <summary>%TEMP%\DeployToolkit\deploy\{packageId} — extraction root for
    /// executor deployments (Azure/Plesk): PackageReader.ExtractFiles writes
    /// the package's files here before upload. Fresh per run.</summary>
    public static string TempExtractRoot(string packageId) =>
        CombineWithTemp("DeployToolkit", "deploy", packageId);

    // ---------------------------------------------------------------
    // Helpers

    private static string CombineWithDocuments(params string[] parts)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.CurrentDirectory;
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }

    private static string CombineWithAppData(params string[] parts)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.CurrentDirectory;
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }

    private static string CombineWithLocalAppData(params string[] parts)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.CurrentDirectory;
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }

    private static string CombineWithTemp(params string[] parts)
    {
        var root = Path.GetTempPath();
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.CurrentDirectory;
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }
}
