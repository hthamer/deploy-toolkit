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
    /// <summary>
    /// The registry package row this manifest belongs to
    /// (<c>Packages.PackageId</c> — N-format GUID). Written into
    /// manifest.json by the Packager (the row's ID is generated BEFORE the
    /// zip is written, precisely so it can be embedded) and read back by the
    /// Deployer, so the deploy report sent to the central API always carries
    /// the exact PackageId the registry knows — never a locally-invented or
    /// heuristically-matched one. Null only for legacy packages built before
    /// this field existed (those fall back to version+hash matching).
    /// </summary>
    public string? PackageId { get; init; }

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
    /// The set of EF Core migration NAMES that have been applied (deployed) to
    /// the target database as of this package — the cumulative set, tracked
    /// across packages so the next build knows exactly which migrations are
    /// still pending (user request: "track the deployed migrations in the
    /// manifest; for new packages retrieve only the migrations that are not
    /// applied — don't rely on first-to-last, as there may be some migrations
    /// in the middle that are added later which are not deployed").
    /// <para>
    /// A migration is "pending" (auto-checked in the DB-scripts step) when its
    /// name is NOT in this set. The new package's
    /// <see cref="AppliedMigrations"/> = the previous set ∪ the migrations
    /// the user selected this build. Never serialized as a delta — always the
    /// full cumulative set (the manifest is the audit record).
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AppliedMigrations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Not part of the on-disk JSON — tracked separately in the registry, but
    /// convenient to carry alongside an in-memory manifest instance.
    /// </summary>
    public PackageStatus Status { get; set; } = PackageStatus.Created;
}
