using System.IO.Compression;
using System.Text.Json;

namespace DeployToolkit.Core.Backup;

/// <summary>
/// Records exactly what a backup covered, so <see cref="BackupManager.Rollback"/>
/// knows what to restore without guessing.
/// </summary>
public sealed class BackupManifest
{
    public required string Client { get; init; }
    public required string Component { get; init; }
    public required DateTimeOffset TakenUtc { get; init; }
    public required string SiteRoot { get; init; }
    public List<string> BackedUpFiles { get; init; } = new();
    public Dictionary<string, string?> PreMergeAppSettingsValues { get; init; } = new();
    public List<string> PendingDbScripts { get; init; } = new();
}

/// <summary>
/// Backs up files about to be overwritten to
/// %USERPROFILE%\Documents\Backups\{yyyyMMdd}\{HHmm}-{component}\ on the
/// target machine, and can restore from that backup later. Pure
/// System.IO/System.IO.Compression — no external tools, consistent with the
/// "no scripts on target machines" constraint.
/// </summary>
public sealed class BackupManager
{
    private readonly string _backupsRoot;

    /// <param name="backupsRoot">
    /// Defaults to Documents\Backups under the current user's profile,
    /// matching the plan's convention. Overridable for testing.
    /// </param>
    public BackupManager(string? backupsRoot = null)
    {
        _backupsRoot = backupsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Backups");
    }

    /// <summary>
    /// Backs up the given files (paths relative to <paramref name="siteRoot"/>)
    /// into today's dated folder, then writes a backup-manifest.json next to
    /// the zip. Returns the backup folder path.
    /// </summary>
    public string Backup(
        string client,
        string component,
        string siteRoot,
        IReadOnlyList<string> relativeFilePaths,
        DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.Now;
        var dayFolder = Path.Combine(_backupsRoot, timestamp.ToString("yyyyMMdd"));
        var runFolder = Path.Combine(dayFolder, $"{timestamp:HHmm}-{component}");
        Directory.CreateDirectory(runFolder);

        var zipPath = Path.Combine(runFolder, "files.zip");
        var manifest = new BackupManifest
        {
            Client = client,
            Component = component,
            TakenUtc = timestamp,
            SiteRoot = siteRoot,
        };

        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (var relativePath in relativeFilePaths)
            {
                var sourcePath = Path.Combine(siteRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(sourcePath))
                    continue; // new file with nothing to back up yet — fine

                archive.CreateEntryFromFile(sourcePath, relativePath.Replace('\\', '/'), CompressionLevel.Optimal);
                manifest.BackedUpFiles.Add(relativePath);
            }
        }

        File.WriteAllText(
            Path.Combine(runFolder, "backup-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return runFolder;
    }

    /// <summary>
    /// Restores files from a previously-taken backup folder back into the
    /// site root recorded in its manifest.
    /// </summary>
    public void Rollback(string backupFolder)
    {
        var manifestPath = Path.Combine(backupFolder, "backup-manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("No backup-manifest.json found in backup folder.", manifestPath);

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("backup-manifest.json was empty or invalid.");

        var zipPath = Path.Combine(backupFolder, "files.zip");
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;

            var destinationPath = Path.Combine(manifest.SiteRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }
}
