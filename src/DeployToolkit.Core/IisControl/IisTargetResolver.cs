using System.Text.Json;
using System.Text.Json.Serialization;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Core.IisControl;

/// <summary>Machine-local mapping of a component to a concrete IIS target
/// (plan §6: "the first time a mapping doesn't exist yet on a given
/// machine, it enumerates IIS sites/applications... lets you pick, then
/// saves the mapping for next time"). Machine-local on purpose — site
/// names/paths differ per server, unlike the central registry.</summary>
public sealed record IisTargetMapping(string SiteName, string AppPath, string? AppPoolName);

/// <summary>JSON file backing <see cref="IisTargetMapping"/>s.
/// Suggested location: %LOCALAPPDATA%\DeployToolkit\iis-targets.json.</summary>
public sealed class IisTargetMappingStore
{
    private readonly string _filePath;
    private readonly object _gate = new();

    public IisTargetMappingStore(string filePath) => _filePath = filePath;

    public bool TryGet(string componentId, out IisTargetMapping mapping)
    {
        lock (_gate)
        {
            mapping = Load().TryGetValue(componentId, out var found) ? found : default!;
            return mapping is not null;
        }
    }

    public void Save(string componentId, IisTargetMapping mapping)
    {
        lock (_gate)
        {
            var mappings = Load();
            mappings[componentId] = mapping;
            var tempPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(mappings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, _filePath, overwrite: true);
        }
    }

    private Dictionary<string, IisTargetMapping> Load()
    {
        if (!File.Exists(_filePath)) return new(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, IisTargetMapping>>(File.ReadAllText(_filePath))
            ?? new(StringComparer.Ordinal);
    }
}

/// <summary>A concrete IIS target the tool can deploy into.</summary>
public sealed record IisResolvedTarget(string SiteName, string AppPath, string PhysicalPath, string? AppPoolName);

/// <summary>Outcome of resolving a component to an IIS target. When
/// <see cref="Resolved"/> is false, <see cref="Candidates"/> carries the
/// enumerated applications for the site/app picker UI (plan §6).</summary>
public sealed record IisTargetResolution(
    bool Resolved,
    IisResolvedTarget? Target,
    IReadOnlyList<IisApplicationInfo> Candidates,
    string? Message)
{
    public static IisTargetResolution Found(IisResolvedTarget target) =>
        new(true, target, Array.Empty<IisApplicationInfo>(), null);

    public static IisTargetResolution Unresolved(string message, IReadOnlyList<IisApplicationInfo> candidates) =>
        new(false, null, candidates, message);
}

/// <summary>
/// Resolves a <see cref="DeploymentComponent"/> to a concrete IIS
/// site/application, in priority order:
///  1. the machine-local mapping store (chosen on a previous deploy),
///  2. the component's registry config (IisSiteName + IisAppPath),
///  3. otherwise: unresolved, with the live IIS application list as
///     picker candidates.
/// The physical path and app-pool name always come from live IIS data so a
/// stale mapping can't point files into the wrong folder.
/// </summary>
public sealed class IisTargetResolver(IIisController controller, IisTargetMappingStore? mappingStore = null)
{
    public IisTargetResolution Resolve(DeploymentComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (mappingStore is not null && mappingStore.TryGet(component.ComponentId, out var mapped))
        {
            var live = FindApplication(mapped.SiteName, mapped.AppPath);
            if (live is not null)
                return IisTargetResolution.Found(new IisResolvedTarget(
                    live.SiteName, live.Path, live.PhysicalPath, live.AppPoolName ?? mapped.AppPoolName));
            // Mapping points at something that no longer exists — fall
            // through to config/candidates, the picker will refresh it.
        }

        if (!string.IsNullOrWhiteSpace(component.IisSiteName))
        {
            var appPath = NormalizeAppPath(component.IisAppPath);
            var live = FindApplication(component.IisSiteName, appPath);
            if (live is not null)
                return IisTargetResolution.Found(new IisResolvedTarget(
                    live.SiteName, live.Path, live.PhysicalPath, live.AppPoolName));

            return IisTargetResolution.Unresolved(
                $"Site '{component.IisSiteName}' found but no application at path '{appPath}'. Pick the right application below.",
                controller.EnumerateApplications());
        }

        return IisTargetResolution.Unresolved(
            "Component has no IIS site configured. Pick the site/application to deploy into.",
            controller.EnumerateApplications());
    }

    /// <summary>Persists a picker choice for next time on this machine.</summary>
    public void SaveMapping(string componentId, IisResolvedTarget target) =>
        mappingStore?.Save(componentId, new IisTargetMapping(target.SiteName, target.AppPath, target.AppPoolName));

    private IisApplicationInfo? FindApplication(string siteName, string appPath)
    {
        var wanted = NormalizeAppPath(appPath);
        return controller.EnumerateApplications(siteName)
            .FirstOrDefault(a => string.Equals(a.SiteName, siteName, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(a.Path, wanted, StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeAppPath(string? appPath)
    {
        if (string.IsNullOrWhiteSpace(appPath)) return "/";
        var trimmed = appPath.Trim().Replace('\\', '/');
        if (!trimmed.StartsWith('/')) trimmed = "/" + trimmed;
        return trimmed.TrimEnd('/') is { } noTrailing && noTrailing.Length > 0 ? noTrailing : "/";
    }
}
