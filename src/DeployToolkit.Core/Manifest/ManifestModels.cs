namespace DeployToolkit.Core.Manifest;

/// <summary>
/// One file inside a package, identified by its path relative to the publish
/// output root and its content hash.
/// </summary>
public sealed record ManifestFile(string Path, string Hash, long SizeBytes);

/// <summary>
/// One database script attached to a release.
/// </summary>
public enum DbScriptKind
{
    Schema,
    Data
}

public sealed record DbScriptRef(string File, DbScriptKind Kind);

/// <summary>
/// The lifecycle status of a package. The diff/baseline rule in
/// <see cref="ManifestDiffEngine"/> only ever trusts packages whose status is
/// <see cref="Deployed"/> — a package that was built but never shipped must
/// never become the baseline for a later diff.
/// </summary>
public enum PackageStatus
{
    Created,
    Deployed,
    Superseded,
    Abandoned
}

/// <summary>
/// The full manifest for one release of one component. This is what gets
/// serialized to manifest.json and embedded at the root of the package zip.
/// </summary>
public sealed class ComponentManifest
{
    public required string ComponentId { get; init; }
    public required string Client { get; init; }
    public required string Component { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public string? GitCommitSha { get; init; }
    public required string TargetFramework { get; init; }
    public bool IsSelfContained { get; init; }
    public string? BaselineManifest { get; init; }

    public IReadOnlyList<ManifestFile> Files { get; init; } = Array.Empty<ManifestFile>();
    public IReadOnlyList<string> DeletedFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Flat dotted-key delta applied on top of whatever appsettings.json (or
    /// Azure App Service Configuration) already exists on the target — e.g.
    /// "Smtp:Host" -> "smtp.newhost.com". Only these keys are ever touched.
    /// </summary>
    public IReadOnlyDictionary<string, object?> AppSettingsDelta { get; init; } =
        new Dictionary<string, object?>();

    public IReadOnlyList<DbScriptRef> DbScripts { get; init; } = Array.Empty<DbScriptRef>();
    public string? HealthCheckUrl { get; init; }

    /// <summary>
    /// Not part of the on-disk JSON — tracked separately in the registry, but
    /// convenient to carry alongside an in-memory manifest instance.
    /// </summary>
    public PackageStatus Status { get; set; } = PackageStatus.Created;
}
