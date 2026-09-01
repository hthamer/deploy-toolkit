namespace DeployToolkit.Core.Manifest;

/// <summary>
/// The result of diffing a fresh publish output against a baseline manifest:
/// what needs to go into the delta package.
/// </summary>
public sealed record ManifestDiffResult(
    IReadOnlyList<ManifestFile> ChangedOrNewFiles,
    IReadOnlyList<string> DeletedFiles)
{
    public bool HasChanges => ChangedOrNewFiles.Count > 0 || DeletedFiles.Count > 0;
}

/// <summary>
/// Computes the delta between a fresh set of hashed files and a baseline.
///
/// Baseline rule (see the implementation plan, section 9 "Package Lifecycle
/// &amp; Status Tracking"): callers must always pass the baseline from the
/// most recent package whose Status == Deployed for this component — never
/// the most recently *created* one. This class doesn't enforce that itself
/// (it just diffs two file lists) — baseline *selection* is the registry's
/// job (see IRegistryStore.GetLatestDeployedManifestAsync), by design, so
/// this engine stays trivially testable without a database.
/// </summary>
public static class ManifestDiffEngine
{
    public static ManifestDiffResult Diff(
        IReadOnlyList<ManifestFile> currentFiles,
        IReadOnlyList<ManifestFile>? baselineFiles)
    {
        var baseline = baselineFiles is null
            ? new Dictionary<string, string>()
            : baselineFiles.ToDictionary(f => f.Path, f => f.Hash, StringComparer.Ordinal);

        var current = currentFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);

        var changedOrNew = currentFiles
            .Where(f => !baseline.TryGetValue(f.Path, out var baselineHash) || baselineHash != f.Hash)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        var deleted = baseline.Keys
            .Where(path => !current.ContainsKey(path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return new ManifestDiffResult(changedOrNew, deleted);
    }

    /// <summary>
    /// Convenience overload: diff directly against a previous
    /// <see cref="ComponentManifest"/> (or null, for a component's very
    /// first release — everything is "new" in that case).
    /// </summary>
    public static ManifestDiffResult Diff(
        IReadOnlyList<ManifestFile> currentFiles,
        ComponentManifest? baselineManifest)
        => Diff(currentFiles, baselineManifest?.Files);
}
