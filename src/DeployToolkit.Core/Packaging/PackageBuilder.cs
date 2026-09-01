using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;

namespace DeployToolkit.Core.Packaging;

/// <summary>
/// Thrown when a folder has never been mapped to a component and the caller
/// (UI) needs to ask the user to pick or create one, rather than the
/// builder silently guessing.
/// </summary>
public sealed class ComponentNotResolvedException : Exception
{
    public ComponentNotResolvedException(string folderPath)
        : base($"No component is mapped to '{folderPath}' yet. Prompt the user to select or create one, then call RegisterFolderMapping.")
    {
    }
}

public sealed record PackageBuildRequest(
    string ComponentId,
    string Version,
    string PublishOutputRoot,
    string OutputZipPath,
    string? GitCommitSha = null,
    IReadOnlyDictionary<string, object?>? AppSettingsDelta = null,
    IReadOnlyList<DbScriptRef>? DbScripts = null,
    IReadOnlyDictionary<string, string>? DbScriptSourcePaths = null,
    /// <summary>
    /// Paths (relative, forward-slash) to leave out of the delta — the
    /// Packager diff-preview grid's manual exclude option.
    /// </summary>
    IReadOnlyCollection<string>? ExcludedPaths = null);

public sealed record PackageBuildResult(
    ComponentManifest Manifest,
    string ZipPath,
    PackageRecord Record,
    IReadOnlyList<PackageRecord> UnresolvedStalePackages);

/// <summary>
/// Ties together folder resolution, baseline lookup, hashing/diffing, and
/// package writing — the Packager app's core workflow (plan §5 and §10),
/// minus the WinForms UI and the actual `dotnet publish`/git pull calls
/// (those belong to the UI layer and the git-integration project
/// respectively, neither of which this dependency-free project can host).
/// </summary>
public sealed class PackageBuilder
{
    private readonly IRegistryStore _registry;
    private readonly ILocalProjectMappingStore _mapping;

    public PackageBuilder(IRegistryStore registry, ILocalProjectMappingStore mapping)
    {
        _registry = registry;
        _mapping = mapping;
    }

    /// <summary>
    /// Resolves the component for a local project folder. Throws
    /// <see cref="ComponentNotResolvedException"/> the first time a folder
    /// is seen — the UI catches that, prompts the user to pick/create a
    /// client+component, then calls <see cref="RegisterFolderMappingAsync"/>
    /// before retrying.
    /// </summary>
    public async Task<DeploymentComponent> ResolveComponentForFolderAsync(string localFolderPath)
    {
        var componentId = await _mapping.FindComponentIdAsync(localFolderPath)
            ?? throw new ComponentNotResolvedException(localFolderPath);

        return await _registry.GetComponentAsync(componentId)
            ?? throw new InvalidOperationException(
                $"Folder '{localFolderPath}' is mapped to component '{componentId}', but that component no longer exists in the registry.");
    }

    /// <summary>
    /// Persists the folder -> component mapping after the user has picked
    /// or created one, so future selections of this folder auto-resolve.
    /// </summary>
    public Task RegisterFolderMappingAsync(string localFolderPath, string componentId)
        => _mapping.RememberAsync(localFolderPath, componentId);

    /// <summary>
    /// Creates a brand-new client + component and maps this folder to it —
    /// the "auto-register the client if not exist" path from the plan.
    /// </summary>
    public async Task<DeploymentComponent> CreateClientAndComponentAsync(
        string localFolderPath,
        string clientName,
        string componentName,
        TargetType targetType,
        string targetFramework,
        bool isSelfContained,
        string? healthCheckUrl = null)
    {
        var client = await _registry.FindClientByNameAsync(clientName)
            ?? await _registry.CreateClientAsync(clientName);

        var component = await _registry.CreateComponentAsync(new DeploymentComponent
        {
            ComponentId = Guid.NewGuid().ToString("N"),
            ClientId = client.ClientId,
            Name = componentName,
            TargetType = targetType,
            TargetFramework = targetFramework,
            IsSelfContained = isSelfContained,
            HealthCheckUrl = healthCheckUrl,
        });

        await RegisterFolderMappingAsync(localFolderPath, component.ComponentId);
        return component;
    }

    /// <summary>
    /// Returns any packages for this component that were built but never
    /// confirmed deployed. The UI should surface these before building a
    /// new one — Abandon / Mark Deployed / Ignore — per plan §9, so an
    /// undeployed package can never silently become a later diff baseline.
    /// </summary>
    public Task<IReadOnlyList<PackageRecord>> CheckForStalePackagesAsync(string componentId)
        => _registry.GetUndeployedPackagesAsync(componentId);

    /// <summary>
    /// Hashes the publish output, diffs it against the most recent
    /// <b>Deployed</b> package for this component (never the most recently
    /// created one — that's the whole point), writes the delta package, and
    /// records it in the registry as Created.
    /// </summary>
    public async Task<PackageBuildResult> BuildAsync(PackageBuildRequest request)
    {
        var component = await _registry.GetComponentAsync(request.ComponentId)
            ?? throw new InvalidOperationException($"Unknown component '{request.ComponentId}'.");

        var client = await _registry.GetClientAsync(component.ClientId)
            ?? throw new InvalidOperationException($"Component '{request.ComponentId}' references a missing client '{component.ClientId}'.");

        var stalePackages = await CheckForStalePackagesAsync(request.ComponentId);

        var baselinePackage = await _registry.GetLatestDeployedPackageAsync(request.ComponentId);
        var baselineManifest = baselinePackage is null ? null : ManifestSerializer.Deserialize(baselinePackage.ManifestJson);

        var currentFiles = ManifestHasher.HashFolder(request.PublishOutputRoot);
        var diff = ManifestDiffEngine.Diff(currentFiles, baselineManifest);

        // Manual exclusions (plan §10 step 4): the UI's diff-preview grid lets
        // the operator leave individual paths out of the delta — both changed
        // files AND deletions (a deliberately-kept deleted file must not
        // silently reappear in the manifest's DeletedFiles either).
        var excludedPaths = request.ExcludedPaths ?? Array.Empty<string>();

        // Sensitive-file policy (user request): appsettings.json / web.config /
        // app.config / per-environment appsettings.*.json / connectionstrings.json
        // / secrets.json are NEVER packaged — overwriting them on the target
        // server is dangerous (the build-machine copies may carry local dev
        // secrets, and a delta package has no trace of the production values).
        // Enforced centrally here so the UI can NEVER bypass it, even if the
        // user checks "Include" on a sensitive row. The diff-step UI renders
        // these rows as permanently-excluded (disabled checkbox + 'sensitive'
        // change) so the policy is visible, not silent.
        var changedOrNewFiles = diff.ChangedOrNewFiles
            .Where(f => !IsExcluded(f.Path, excludedPaths) && !SensitiveFileFilter.IsSensitiveOrAppSettingsVariant(f.Path))
            .ToList();
        var deletedFiles = diff.DeletedFiles
            .Where(p => !IsExcluded(p, excludedPaths) && !SensitiveFileFilter.IsSensitiveOrAppSettingsVariant(p))
            .ToList();

        var manifest = new ComponentManifest
        {
            ComponentId = request.ComponentId,
            Client = client.Name,
            Component = component.Name,
            Version = request.Version,
            CreatedUtc = DateTimeOffset.UtcNow,
            GitCommitSha = request.GitCommitSha,
            TargetFramework = component.TargetFramework,
            IsSelfContained = component.IsSelfContained,
            BaselineManifest = baselinePackage?.PackageId,
            Files = changedOrNewFiles,
            DeletedFiles = deletedFiles,
            AppSettingsDelta = request.AppSettingsDelta ?? new Dictionary<string, object?>(),
            DbScripts = request.DbScripts ?? Array.Empty<DbScriptRef>(),
            HealthCheckUrl = component.HealthCheckUrl,
        };

        PackageWriter.Write(
            manifest,
            request.PublishOutputRoot,
            changedOrNewFiles,
            request.DbScriptSourcePaths,
            request.OutputZipPath);

        var record = await _registry.CreatePackageAsync(request.ComponentId, manifest);

        return new PackageBuildResult(manifest, request.OutputZipPath, record, stalePackages);
    }

    /// <summary>
    /// Case-insensitive match of <paramref name="path"/> against the request's
    /// exclusion list. Both sides are normalized the way
    /// <see cref="ManifestHasher.HashFolder"/> normalizes manifest paths —
    /// backslashes folded to forward slashes, leading slashes trimmed — so
    /// "bin/App.dll", "bin\\App.dll" and "/bin/App.dll" all compare equal.
    /// </summary>
    private static bool IsExcluded(string path, IReadOnlyCollection<string> excludedPaths)
    {
        if (excludedPaths.Count == 0)
            return false;

        var normalized = NormalizeManifestPath(path);
        foreach (var excluded in excludedPaths)
        {
            if (string.IsNullOrWhiteSpace(excluded))
                continue; // tolerate blank entries from loose UI input

            if (string.Equals(normalized, NormalizeManifestPath(excluded), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeManifestPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
