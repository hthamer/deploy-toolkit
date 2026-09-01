using System.Security.Cryptography;

namespace DeployToolkit.Core.Manifest;

/// <summary>
/// Walks a publish-output folder and produces the file list + hashes that go
/// into a <see cref="ComponentManifest"/>. Pure BCL — no external
/// dependencies — so it works identically whether it runs from the Packager
/// (against a fresh dotnet publish output) or a test harness.
/// </summary>
public static class ManifestHasher
{
    /// <summary>
    /// Computes a stable "sha256:&lt;hex&gt;" hash for a single file.
    /// </summary>
    public static string HashFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Recursively hashes every file under <paramref name="rootFolder"/> and
    /// returns them as <see cref="ManifestFile"/> records with paths
    /// relative to that root, using forward slashes so manifests are
    /// portable across OSes.
    /// </summary>
    public static IReadOnlyList<ManifestFile> HashFolder(string rootFolder)
    {
        if (!Directory.Exists(rootFolder))
            throw new DirectoryNotFoundException($"Publish output folder not found: {rootFolder}");

        var root = Path.GetFullPath(rootFolder);
        var results = new List<ManifestFile>();

        foreach (var filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
            var hash = HashFile(filePath);
            var size = new FileInfo(filePath).Length;
            results.Add(new ManifestFile(relativePath, hash, size));
        }

        return results
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
    }
}
