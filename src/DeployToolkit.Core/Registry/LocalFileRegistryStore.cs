using System.Text.Json;
using DeployToolkit.Core.Manifest;

namespace DeployToolkit.Core.Registry;

/// <summary>
/// A file-backed implementation of <see cref="IRegistryStore"/>. Two jobs:
///
///  1. The offline-mode fallback described in the plan — when a Deployer run
///     can't reach the central Azure SQL registry from inside a client's
///     network, it writes results here instead, and the Packager reconciles
///     them back into the real registry once it's reachable again.
///  2. A dependency-free stand-in for the real EF Core store, so
///     PackageBuilder/DeploymentOrchestrator (and their tests) don't need a
///     live SQL Server to exercise the baseline/stale-package logic.
///
/// Not thread-safe across processes (file locking would be needed for
/// that) — fine for its two intended uses, which are both single-process.
/// </summary>
public sealed class LocalFileRegistryStore : IRegistryStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LocalFileRegistryStore(string rootFolder)
    {
        _root = rootFolder;
        Directory.CreateDirectory(_root);
    }

    private string ClientsFile => Path.Combine(_root, "clients.json");
    private string ComponentsFile => Path.Combine(_root, "components.json");
    private string PackagesFile(string componentId) => Path.Combine(_root, "packages", $"{componentId}.json");
    private string RunsFile(string componentId) => Path.Combine(_root, "runs", $"{componentId}.json");

    // ---------------------------------------------------------------
    // Clients

    public async Task<Client?> FindClientByNameAsync(string name)
    {
        var clients = await LoadAsync<List<Client>>(ClientsFile) ?? new();
        return clients.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Client?> GetClientAsync(string clientId)
    {
        var clients = await LoadAsync<List<Client>>(ClientsFile) ?? new();
        return clients.FirstOrDefault(c => c.ClientId == clientId);
    }

    public async Task<Client> CreateClientAsync(string name, string? notes = null)
    {
        var client = new Client { ClientId = Guid.NewGuid().ToString("N"), Name = name, Notes = notes };
        client.NormalizeAndValidate();

        await _lock.WaitAsync();
        try
        {
            var clients = await LoadAsync<List<Client>>(ClientsFile) ?? new();
            if (clients.Any(c => string.Equals(c.Name, client.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A client named '{client.Name}' already exists.");

            clients.Add(client);
            await SaveAsync(ClientsFile, clients);
            return client;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<Client>> GetAllClientsAsync()
    {
        var clients = await LoadAsync<List<Client>>(ClientsFile) ?? new();
        return clients.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<Client> UpdateClientAsync(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.NormalizeAndValidate();

        await _lock.WaitAsync();
        try
        {
            var clients = await LoadAsync<List<Client>>(ClientsFile) ?? new();
            var index = clients.FindIndex(c => c.ClientId == client.ClientId);
            if (index < 0)
                throw new InvalidOperationException($"Client {client.ClientId} not found.");

            if (clients.Any(c => c.ClientId != client.ClientId &&
                                 string.Equals(c.Name, client.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A client named '{client.Name}' already exists.");

            clients[index] = client;
            await SaveAsync(ClientsFile, clients);
            return client;
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteClientAsync(string clientId)
    {
        await _lock.WaitAsync();
        try
        {
            var clients = await LoadAsync<List<Client>>(ClientsFile) ?? new();
            var client = clients.FirstOrDefault(c => c.ClientId == clientId)
                ?? throw new InvalidOperationException($"Client {clientId} not found.");

            var components = await LoadAsync<List<DeploymentComponent>>(ComponentsFile) ?? new();
            var componentCount = components.Count(c => c.ClientId == clientId);
            if (componentCount > 0)
                throw new InvalidOperationException(
                    $"Client '{client.Name}' still has {componentCount} component(s). Delete its components first — registry rows are an audit trail and are never cascade-deleted.");

            clients.Remove(client);
            await SaveAsync(ClientsFile, clients);
        }
        finally { _lock.Release(); }
    }

    // ---------------------------------------------------------------
    // Components

    public async Task<DeploymentComponent?> GetComponentAsync(string componentId)
    {
        var components = await LoadAsync<List<DeploymentComponent>>(ComponentsFile) ?? new();
        return components.FirstOrDefault(c => c.ComponentId == componentId);
    }

    public async Task<IReadOnlyList<DeploymentComponent>> GetComponentsForClientAsync(string clientId)
    {
        var components = await LoadAsync<List<DeploymentComponent>>(ComponentsFile) ?? new();
        return components.Where(c => c.ClientId == clientId).ToList();
    }

    public async Task<DeploymentComponent> CreateComponentAsync(DeploymentComponent component)
    {
        await _lock.WaitAsync();
        try
        {
            var components = await LoadAsync<List<DeploymentComponent>>(ComponentsFile) ?? new();
            components.Add(component);
            await SaveAsync(ComponentsFile, components);
            return component;
        }
        finally { _lock.Release(); }
    }

    public async Task<DeploymentComponent> UpdateComponentAsync(DeploymentComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        await _lock.WaitAsync();
        try
        {
            var components = await LoadAsync<List<DeploymentComponent>>(ComponentsFile) ?? new();
            var index = components.FindIndex(c => c.ComponentId == component.ComponentId);
            if (index < 0)
                throw new InvalidOperationException($"Component {component.ComponentId} not found.");

            components[index] = component;
            await SaveAsync(ComponentsFile, components);
            return component;
        }
        finally { _lock.Release(); }
    }

    // ---------------------------------------------------------------
    // Packages

    public async Task<PackageRecord?> GetLatestDeployedPackageAsync(string componentId)
    {
        var packages = await LoadAsync<List<PackageRecord>>(PackagesFile(componentId)) ?? new();
        return packages
            .Where(p => p.Status == PackageStatus.Deployed)
            .OrderByDescending(p => p.DeployedUtc)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<PackageRecord>> GetUndeployedPackagesAsync(string componentId)
    {
        var packages = await LoadAsync<List<PackageRecord>>(PackagesFile(componentId)) ?? new();
        return packages.Where(p => p.Status == PackageStatus.Created).OrderBy(p => p.CreatedUtc).ToList();
    }

    public async Task<PackageRecord> CreatePackageAsync(string componentId, ComponentManifest manifest)
    {
        await _lock.WaitAsync();
        try
        {
            var file = PackagesFile(componentId);
            var packages = await LoadAsync<List<PackageRecord>>(file) ?? new();

            var record = new PackageRecord
            {
                PackageId = Guid.NewGuid().ToString("N"),
                ComponentId = componentId,
                Version = manifest.Version,
                CreatedUtc = manifest.CreatedUtc,
                ManifestJson = ManifestSerializer.Serialize(manifest),
                GitCommitSha = manifest.GitCommitSha,
                Status = PackageStatus.Created,
            };

            packages.Add(record);
            await SaveAsync(file, packages);
            return record;
        }
        finally { _lock.Release(); }
    }

    public async Task MarkDeployedAsync(string packageId, string deployedBy, DateTimeOffset deployedUtc)
        => await UpdatePackageAsync(packageId, p =>
        {
            p.Status = PackageStatus.Deployed;
            p.DeployedBy = deployedBy;
            p.DeployedUtc = deployedUtc;
        });

    public async Task MarkStatusAsync(string packageId, PackageStatus status)
        => await UpdatePackageAsync(packageId, p => p.Status = status);

    public async Task<PackageRecord?> GetPackageAsync(string packageId)
    {
        var packagesDir = Path.Combine(_root, "packages");
        if (!Directory.Exists(packagesDir))
            return null;

        foreach (var file in Directory.EnumerateFiles(packagesDir, "*.json"))
        {
            var packages = await LoadAsync<List<PackageRecord>>(file) ?? new();
            var match = packages.FirstOrDefault(p => p.PackageId == packageId);
            if (match is not null)
                return match;
        }

        return null;
    }

    public async Task<IReadOnlyList<PackageRecord>> GetPackagesForComponentAsync(string componentId)
    {
        var packages = await LoadAsync<List<PackageRecord>>(PackagesFile(componentId)) ?? new();
        return packages.OrderByDescending(p => p.CreatedUtc).ToList();
    }

    public async Task DeletePackageAsync(string packageId, bool deleteRunHistory = false)
    {
        await _lock.WaitAsync();
        try
        {
            // Same partition scan UpdatePackageAsync uses — packages live in
            // per-component files and the id is all we have.
            var packagesDir = Path.Combine(_root, "packages");
            if (!Directory.Exists(packagesDir))
                throw new InvalidOperationException($"Package {packageId} not found — no packages recorded yet.");

            foreach (var file in Directory.EnumerateFiles(packagesDir, "*.json"))
            {
                var packages = await LoadAsync<List<PackageRecord>>(file) ?? new();
                var match = packages.FirstOrDefault(p => p.PackageId == packageId);
                if (match is null)
                    continue;

                var componentId = Path.GetFileNameWithoutExtension(file);
                var runs = await LoadAsync<List<DeploymentRunRecord>>(RunsFile(componentId)) ?? new();
                var runCount = runs.Count(r => r.PackageId == packageId);
                if (runCount > 0 && !deleteRunHistory)
                    throw new InvalidOperationException(
                        $"Package {packageId} has {runCount} recorded deployment run(s). Pass deleteRunHistory:true to remove the package together with its run history — this cannot be undone.");

                if (runCount > 0)
                {
                    runs.RemoveAll(r => r.PackageId == packageId);
                    await SaveAsync(RunsFile(componentId), runs);
                }

                packages.Remove(match);
                await SaveAsync(file, packages);
                return;
            }

            throw new InvalidOperationException($"Package {packageId} not found.");
        }
        finally { _lock.Release(); }
    }

    private async Task UpdatePackageAsync(string packageId, Action<PackageRecord> mutate)
    {
        await _lock.WaitAsync();
        try
        {
            // packages are partitioned by component file — find which one holds this id
            var packagesDir = Path.Combine(_root, "packages");
            if (!Directory.Exists(packagesDir))
                throw new InvalidOperationException($"Package {packageId} not found — no packages recorded yet.");

            foreach (var file in Directory.EnumerateFiles(packagesDir, "*.json"))
            {
                var packages = await LoadAsync<List<PackageRecord>>(file) ?? new();
                var match = packages.FirstOrDefault(p => p.PackageId == packageId);
                if (match is null)
                    continue;

                mutate(match);
                await SaveAsync(file, packages);
                return;
            }

            throw new InvalidOperationException($"Package {packageId} not found.");
        }
        finally { _lock.Release(); }
    }

    // ---------------------------------------------------------------
    // Deployment runs

    public async Task<DeploymentRunRecord> RecordRunStartAsync(string packageId, DateTimeOffset startedUtc)
    {
        await _lock.WaitAsync();
        try
        {
            var componentId = await FindComponentIdForPackageAsync(packageId)
                ?? throw new InvalidOperationException($"Package {packageId} not found.");

            var file = RunsFile(componentId);
            var runs = await LoadAsync<List<DeploymentRunRecord>>(file) ?? new();

            var run = new DeploymentRunRecord
            {
                RunId = Guid.NewGuid().ToString("N"),
                PackageId = packageId,
                StartedUtc = startedUtc,
            };

            runs.Add(run);
            await SaveAsync(file, runs);
            return run;
        }
        finally { _lock.Release(); }
    }

    public async Task RecordRunCompleteAsync(string runId, string result, bool? healthCheckResult, string? logPath)
    {
        await _lock.WaitAsync();
        try
        {
            var runsDir = Path.Combine(_root, "runs");
            if (!Directory.Exists(runsDir))
                throw new InvalidOperationException($"Run {runId} not found.");

            foreach (var file in Directory.EnumerateFiles(runsDir, "*.json"))
            {
                var runs = await LoadAsync<List<DeploymentRunRecord>>(file) ?? new();
                var match = runs.FirstOrDefault(r => r.RunId == runId);
                if (match is null)
                    continue;

                match.CompletedUtc = DateTimeOffset.UtcNow;
                match.Result = result;
                match.HealthCheckResult = healthCheckResult;
                match.LogPath = logPath;
                await SaveAsync(file, runs);
                return;
            }

            throw new InvalidOperationException($"Run {runId} not found.");
        }
        finally { _lock.Release(); }
    }

    private async Task<string?> FindComponentIdForPackageAsync(string packageId)
    {
        var packagesDir = Path.Combine(_root, "packages");
        if (!Directory.Exists(packagesDir))
            return null;

        foreach (var file in Directory.EnumerateFiles(packagesDir, "*.json"))
        {
            var packages = await LoadAsync<List<PackageRecord>>(file) ?? new();
            if (packages.Any(p => p.PackageId == packageId))
                return Path.GetFileNameWithoutExtension(file);
        }

        return null;
    }

    // ---------------------------------------------------------------

    private static async Task<T?> LoadAsync<T>(string file) where T : class
    {
        if (!File.Exists(file))
            return null;

        var json = await File.ReadAllTextAsync(file);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<T>(json);
    }

    private static async Task SaveAsync<T>(string file, T value)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
