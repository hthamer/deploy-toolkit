using System.IO.Compression;
using DeployToolkit.Core.Manifest;

namespace DeployToolkit.Core.Packaging;

/// <summary>
/// Builds a delta.zip with the layout from the implementation plan:
///
///   delta.zip
///   ├── manifest.json
///   ├── files/   (only changed/new files, mirroring publish-output relative paths)
///   └── db/      (attached .sql scripts)
/// </summary>
public static class PackageWriter
{
    /// <summary>
    /// Writes a package zip to <paramref name="outputZipPath"/>.
    /// </summary>
    /// <param name="manifest">The manifest to embed at the zip root.</param>
    /// <param name="publishOutputRoot">
    /// The folder the manifest's file paths are relative to (i.e. the dotnet
    /// publish output) — only the files named in
    /// <paramref name="filesToInclude"/> are copied in, not everything under
    /// this root.
    /// </param>
    /// <param name="filesToInclude">
    /// Typically the ChangedOrNewFiles from a <see cref="ManifestDiffResult"/>.
    /// </param>
    /// <param name="dbScriptSourcePaths">
    /// Maps each DbScriptRef.File name to its actual source path on disk.
    /// </param>
    public static void Write(
        ComponentManifest manifest,
        string publishOutputRoot,
        IReadOnlyList<ManifestFile> filesToInclude,
        IReadOnlyDictionary<string, string>? dbScriptSourcePaths = null,
        string outputZipPath = "")
    {
        if (string.IsNullOrWhiteSpace(outputZipPath))
            throw new ArgumentException("outputZipPath is required.", nameof(outputZipPath));

        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputZipPath));
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        if (File.Exists(outputZipPath))
            File.Delete(outputZipPath);

        using var zipStream = new FileStream(outputZipPath, FileMode.CreateNew);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // manifest.json at the root
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var entryStream = manifestEntry.Open())
        using (var writer = new StreamWriter(entryStream))
        {
            writer.Write(ManifestSerializer.Serialize(manifest));
        }

        // files/ — only the changed/new set, never the whole publish output
        var root = Path.GetFullPath(publishOutputRoot);
        foreach (var file in filesToInclude)
        {
            var sourcePath = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Manifest references a file that doesn't exist on disk: {sourcePath}");

            var entryName = "files/" + file.Path;
            archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
        }

        // db/ — attached scripts, if any
        if (dbScriptSourcePaths is not null)
        {
            foreach (var script in manifest.DbScripts)
            {
                if (!dbScriptSourcePaths.TryGetValue(script.File, out var sourcePath))
                    throw new InvalidOperationException($"No source path supplied for DB script '{script.File}'.");

                archive.CreateEntryFromFile(sourcePath, "db/" + script.File, CompressionLevel.Optimal);
            }
        }
    }
}
