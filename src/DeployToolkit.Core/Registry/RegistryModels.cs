using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Targets;

namespace DeployToolkit.Core.Registry;

public sealed class Client
{
    public required string ClientId { get; init; }
    public required string Name { get; set; }
    public string? Notes { get; set; }

    // ---------------------------------------------------------------
    // Client profile (added pre-WinForms: full client management).
    // Everything below is editable through IRegistryStore.UpdateClientAsync;
    // properties are settable for exactly that reason.

    /// <summary>Contact phone (free-form — international format encouraged, not enforced).</summary>
    public string? ContactPhone { get; set; }

    /// <summary>Contact email. Light format check only (see <see cref="NormalizeAndValidate"/>).</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Git repository the client's source lives in (the Packager pulls from here, plan §5).</summary>
    public string? GitRepositoryUrl { get; set; }

    /// <summary>Branch the Packager deploys from (e.g. "main", "release").</summary>
    public string? DeploymentBranch { get; set; }

    /// <summary>
    /// Client-level default .NET publish shape (deployment type, target
    /// runtime, extra publish options) as canonical JSON produced by
    /// <see cref="PublishConfigurationSerializer"/>. Same pattern as
    /// ManifestJson on PackageRecord: one serializer everywhere.
    /// </summary>
    public string? PublishConfigurationJson { get; set; }

    /// <summary>Whether this client has an active AMC (annual maintenance contract).</summary>
    public bool HasAmc { get; set; }

    /// <summary>AMC expiry date (date only — there is no meaningful time-of-day). Only relevant when <see cref="HasAmc"/> is true.</summary>
    public DateOnly? AmcExpiryDate { get; set; }

    /// <summary>Who manages the client's infrastructure (servers/VMs/network): Boxon or the client itself.</summary>
    public ManagedBy? InfrastructureManagedBy { get; set; }

    /// <summary>
    /// Who manages the hosting account (e.g. "Boxon", "Client", or a more
    /// specific note like "Client — Mr. Saleh"). Free text on purpose: the
    /// business answer is not always one of two fixed values.
    /// </summary>
    public string? HostingAccountManagedBy { get; set; }

    /// <summary>
    /// Typed convenience accessor over <see cref="PublishConfigurationJson"/>.
    /// Not mapped by EF — the JSON string column is the stored truth; this
    /// only parses/serializes it (same trade-off the manifest makes).
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public PublishConfiguration? PublishConfiguration
    {
        get => PublishConfigurationSerializer.Parse(PublishConfigurationJson);
        set => PublishConfigurationJson = PublishConfigurationSerializer.Serialize(value);
    }

    /// <summary>
    /// Trims the string fields and applies the light format rules the
    /// registry enforces on its own data (name required, e-mail plausible,
    /// git URL parseable). Stores call this on create AND update so bad data
    /// can never enter the registry through either path.
    /// </summary>
    public void NormalizeAndValidate()
    {
        Name = Name?.Trim() ?? string.Empty;
        Notes = Notes?.Trim();
        ContactPhone = ContactPhone?.Trim();
        ContactEmail = ContactEmail?.Trim();
        GitRepositoryUrl = GitRepositoryUrl?.Trim();
        DeploymentBranch = DeploymentBranch?.Trim();
        HostingAccountManagedBy = HostingAccountManagedBy?.Trim();

        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Client name is required.");

        if (!string.IsNullOrEmpty(ContactEmail) &&
            (ContactEmail.Contains(' ') || !ContactEmail.Contains('@')))
            throw new ArgumentException($"ContactEmail '{ContactEmail}' does not look like an e-mail address.");

        if (!string.IsNullOrEmpty(GitRepositoryUrl) &&
            !Uri.TryCreate(GitRepositoryUrl, UriKind.Absolute, out _))
            throw new ArgumentException($"GitRepositoryUrl '{GitRepositoryUrl}' is not an absolute URL.");

        if (!string.IsNullOrEmpty(DeploymentBranch) && DeploymentBranch.Contains(' '))
            throw new ArgumentException($"DeploymentBranch '{DeploymentBranch}' must not contain spaces (git branch names cannot).");
    }
}

public sealed class DeploymentComponent
{
    public required string ComponentId { get; init; }
    public required string ClientId { get; init; }
    public required string Name { get; init; } // e.g. "CMS", "Website"
    public required TargetType TargetType { get; init; }
    public required string TargetFramework { get; init; }
    public bool IsSelfContained { get; init; }

    public string? IisSiteName { get; init; }
    public string? IisAppPath { get; init; }
    public string? AzureAppServiceName { get; init; }
    public string? AzureResourceGroup { get; init; }
    public string? PleskHost { get; init; }
    public string? PleskSiteId { get; init; }

    public string? HealthCheckUrl { get; init; }
    public string? DbConnectionRef { get; init; } // pointer to encrypted secret, never the secret itself
}

public sealed class PackageRecord
{
    public required string PackageId { get; init; }
    public required string ComponentId { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string ManifestJson { get; init; }
    public string? GitCommitSha { get; init; }
    public PackageStatus Status { get; set; } = PackageStatus.Created;
    public DateTimeOffset? DeployedUtc { get; set; }
    public string? DeployedBy { get; set; }
}

public sealed class DeploymentRunRecord
{
    public required string RunId { get; init; }
    public required string PackageId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? Result { get; set; } // Success | Failed | RolledBack
    public string? LogPath { get; set; }
    public bool? HealthCheckResult { get; set; }
}

/// <summary>
/// Data access contract for the central registry described in the plan.
/// The real implementation (EF Core against Azure SQL) is added once NuGet
/// access is available — this interface is what the Packager/Deployer UIs
/// are built against in the meantime, and it's also what makes the
/// "offline mode" fallback possible: a local-file-backed implementation of
/// this same interface can stand in when the registry DB is unreachable.
/// </summary>
public interface IRegistryStore
{
    Task<Client?> FindClientByNameAsync(string name);

    Task<Client?> GetClientAsync(string clientId);

    Task<Client> CreateClientAsync(string name, string? notes = null);

    Task<DeploymentComponent?> GetComponentAsync(string componentId);

    Task<IReadOnlyList<DeploymentComponent>> GetComponentsForClientAsync(string clientId);

    Task<DeploymentComponent> CreateComponentAsync(DeploymentComponent component);

    /// <summary>
    /// Persists changes to an existing component (publish settings —
    /// TargetFramework / IsSelfContained — are the fields the Packager's
    /// publish step edits). <see cref="DeploymentComponent"/> is init-only,
    /// so callers pass a rebuilt instance carrying the same ComponentId.
    /// Throws <see cref="InvalidOperationException"/> when the component
    /// does not exist.
    /// </summary>
    Task<DeploymentComponent> UpdateComponentAsync(DeploymentComponent component);

    Task<PackageRecord?> GetLatestDeployedPackageAsync(string componentId);

    Task<IReadOnlyList<PackageRecord>> GetUndeployedPackagesAsync(string componentId);

    Task<PackageRecord> CreatePackageAsync(string componentId, ComponentManifest manifest);

    Task MarkDeployedAsync(string packageId, string deployedBy, DateTimeOffset deployedUtc);

    Task MarkStatusAsync(string packageId, PackageStatus status);

    Task<DeploymentRunRecord> RecordRunStartAsync(string packageId, DateTimeOffset startedUtc);

    Task RecordRunCompleteAsync(string runId, string result, bool? healthCheckResult, string? logPath);

    // ---------------------------------------------------------------
    // Client & package management (added pre-WinForms: the Clients screen
    // needs full CRUD on client profiles and explicit lifecycle control of
    // packages). All implementations must keep identical semantics.

    /// <summary>All clients, ordered by name (case-insensitive) — the Clients screen list.</summary>
    Task<IReadOnlyList<Client>> GetAllClientsAsync();

    /// <summary>
    /// Persists the full client profile (all fields). The passed client is
    /// normalized/validated first; throws <see cref="ArgumentException"/> on
    /// invalid data and <see cref="InvalidOperationException"/> when the
    /// client does not exist or the (unique) name is taken by another client.
    /// </summary>
    Task<Client> UpdateClientAsync(Client client);

    /// <summary>
    /// Deletes a client. Refuses (throws <see cref="InvalidOperationException"/>)
    /// while the client still has components — the registry is an audit
    /// trail, so parents with children are never silently cascade-deleted.
    /// </summary>
    Task DeleteClientAsync(string clientId);

    /// <summary>
    /// Deletes a component. Refuses (throws <see cref="InvalidOperationException"/>)
    /// while the component still has packages — same audit-trail rule as
    /// <see cref="DeleteClientAsync"/>: parents with children are never
    /// silently cascade-deleted. Use <see cref="DeletePackageAsync"/> to
    /// remove the packages first.
    /// </summary>
    Task DeleteComponentAsync(string componentId);

    /// <summary>One package by id, any status, or null.</summary>
    Task<PackageRecord?> GetPackageAsync(string packageId);

    /// <summary>All packages of a component (any status), newest first — the package-management grid.</summary>
    Task<IReadOnlyList<PackageRecord>> GetPackagesForComponentAsync(string componentId);

    /// <summary>
    /// Deletes a package record. Refuses while deployment run records exist
    /// for it unless <paramref name="deleteRunHistory"/> is true, in which
    /// case the runs are removed together with the package (irreversible —
    /// the manifest audit trail goes with it).
    /// </summary>
    Task DeletePackageAsync(string packageId, bool deleteRunHistory = false);
}
