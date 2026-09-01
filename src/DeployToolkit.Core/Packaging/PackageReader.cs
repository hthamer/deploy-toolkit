using System.IO.Compression;
using System.Security.Cryptography;
using DeployToolkit.Core.Manifest;

namespace DeployToolkit.Core.Packaging;

public sealed record PackageIntegrityResult(bool IsValid, IReadOnlyList<string> Problems)
{
    public static PackageIntegrityResult Ok { get; } = new(true, Array.Empty<string>());
}

/// <summary>
/// Reads a package built by <see cref="PackageWriter"/>: loads the manifest,
/// verifies file hashes match before anything is trusted, and extracts files
/// into place. This is the integrity check called out in the plan's Security
/// Considerations section — packages travel over RDP clipboard/file share or
/// HTTPS, so a corrupted/partial copy must fail loudly, not deploy garbage.
/// </summary>
public static class PackageReader
{
    public static ComponentManifest ReadManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("Package is missing manifest.json.");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return ManifestSerializer.Deserialize(reader.ReadToEnd());
    }

    /// <summary>
    /// Verifies every file listed in the manifest exists in the zip's
    /// files/ folder and hashes to exactly what the manifest claims.
    /// </summary>
    public static PackageIntegrityResult VerifyIntegrity(string zipPath)
    {
        var problems = new List<string>();
        ComponentManifest manifest;

        try
        {
            manifest = ReadManifest(zipPath);
        }
        catch (Exception ex)
        {
            return new PackageIntegrityResult(false, new[] { $"Could not read manifest.json: {ex.Message}" });
        }

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var file in manifest.Files)
        {
            var entryName = "files/" + file.Path;
            var entry = archive.GetEntry(entryName);
            if (entry is null)
            {
                problems.Add($"Missing file in package: {entryName}");
                continue;
            }

            using var entryStream = entry.Open();
            var actualHash = "sha256:" + Convert.ToHexString(SHA256.HashData(entryStream)).ToLowerInvariant();
            if (!string.Equals(actualHash, file.Hash, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Hash mismatch for {file.Path}: manifest says {file.Hash}, actual is {actualHash}");
            }
        }

        foreach (var script in manifest.DbScripts)
        {
            var entryName = "db/" + script.File;
            if (archive.GetEntry(entryName) is null)
                problems.Add($"Missing DB script in package: {entryName}");
        }

        return problems.Count == 0 ? PackageIntegrityResult.Ok : new PackageIntegrityResult(false, problems);
    }

    /// <summary>
    /// Reads a single text entry out of the package without extracting the
    /// whole thing — used by the DB script executor (scripts live under
    /// db/ inside the zip) and by UIs that want to preview a script before
    /// running it (plan §7 step "script preview, explicit confirm").
    /// </summary>
    public static string ReadEntryText(string zipPath, string relativePath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var normalized = relativePath.Replace('\\', '/');
        var entry = archive.GetEntry(normalized)
            ?? throw new FileNotFoundException($"Entry '{relativePath}' not found in package '{zipPath}'.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Extracts the files/ entries into <paramref name="destinationRoot"/>,
    /// preserving relative paths. Does not touch db/ scripts (those are
    /// executed directly from the zip stream by the DB executor, not
    /// extracted to disk) or the app-pool/site — that's the Deployer's job,
    /// this method only moves bytes.
    /// </summary>
    public static IReadOnlyList<string> ExtractFiles(string zipPath, string destinationRoot)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var extracted = new List<string>();

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("files/", StringComparison.Ordinal) || entry.FullName.EndsWith('/'))
                continue;

            var relativePath = entry.FullName["files/".Length..];
            var destinationPath = Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            entry.ExtractToFile(destinationPath, overwrite: true);
            extracted.Add(relativePath);
        }

        return extracted;
    }
}
