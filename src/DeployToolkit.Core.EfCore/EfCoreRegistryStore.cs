using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DeployToolkit.Core.EfCore;

/// <summary>
/// EF Core / SQL Server implementation of <see cref="IRegistryStore"/> —
/// the "real" central registry from plan §2.2, replacing the dependency-free
/// <see cref="LocalFileRegistryStore"/> stand-in for every online scenario.
/// The local file store remains the offline-mode fallback (plan §2.2 and §9):
/// the Packager reconciles its files back through this store.
///
/// Each operation opens a short-lived context from an
/// <see cref="IDbContextFactory{RegistryDbContext}"/> — no long-running
/// DbContext instance in a desktop app, no stale change-tracker state
/// between builds/deploys, safe to call concurrently.
///
/// Semantics deliberately mirror <see cref="LocalFileRegistryStore"/> so the
/// two stores are interchangeable behind <see cref="IRegistryStore"/>:
///   - client-name lookup is case-insensitive,
///   - "latest deployed baseline" = Status Deployed, newest DeployedUtc,
///   - "stale packages" = Status Created, oldest first.
/// </summary>
public sealed class EfCoreRegistryStore : IRegistryStore
{
    private readonly IDbContextFactory<RegistryDbContext> _factory;

    public EfCoreRegistryStore(IDbContextFactory<RegistryDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Builds the store against SQL Server / Azure SQL using a pooled
    /// context factory. Example connection strings:
    ///   Azure SQL:  "Server=tcp:myserver.database.windows.net,1433;Database=DeployToolkitRegistry;User Id=...;Password=...;Encrypt=True;"
    ///   Local dev:  "Server=(localdb)\MSSQLLocalDB;Database=DeployToolkitRegistry;Trusted_Connection=True;TrustServerCertificate=True;"
    /// </summary>
    public static EfCoreRegistryStore CreateSqlServer(string connectionString)
        => new(new PooledDbContextFactory<RegistryDbContext>(
            new DbContextOptionsBuilder<RegistryDbContext>()
                .UseSqlServer(connectionString)
                .Options));

    /// <summary>
    /// Creates the database schema by applying the EF migrations in this
    /// assembly (idempotent — no-op when already up to date). Call once at
    /// Packager/Deployer startup. On Azure SQL the connecting login needs
    /// dbmanager/DDL rights for the very first run only.
    ///
    /// Migrations are SQL Server-flavored (column types are baked in at
    /// `dotnet ef migrations add` time), so this path is for SQL Server /
    /// Azure SQL only. Test harnesses that run the provider-neutral model
    /// over another provider (e.g. SQLite) should create the schema with
    /// EnsureCreatedAsync below instead of applying migrations.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the schema directly from the (provider-neutral) model, without
    /// migrations. Used by test harnesses and throwaway local stores running
    /// the same model over non-SQL-Server providers. No-op if the schema
    /// already exists.
    /// </summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    // ---------------------------------------------------------------
    // Clients

    public async Task<Client?> FindClientByNameAsync(string name)
    {
        await using var db = await _factory.CreateDbContextAsync();
        // ToLower() translates on both SQL Server (LOWER) and SQLite, giving
        // the same case-insensitive semantics as LocalFileRegistryStore
        // regardless of server collation. Client count is tiny — the lost
        // index sargability is irrelevant.
        return await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower());
    }

    public async Task<Client?> GetClientAsync(string clientId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == clientId);
    }

    public async Task<Client> CreateClientAsync(string name, string? notes = null)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var client = new Client
        {
            ClientId = Guid.NewGuid().ToString("N"),
            Name = name,
            Notes = notes,
        };
        client.NormalizeAndValidate();

        // Friendly duplicate-name message instead of the raw unique-index
        // violation (same semantics as LocalFileRegistryStore).
        var duplicate = await db.Clients.AnyAsync(c => c.Name.ToLower() == client.Name.ToLower());
        if (duplicate)
            throw new InvalidOperationException($"A client named '{client.Name}' already exists.");

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    public async Task<IReadOnlyList<Client>> GetAllClientsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var clients = await db.Clients.AsNoTracking().ToListAsync();
        // Client-side ordering: identical case-insensitive order on every
        // provider regardless of server collation (same rationale as the
        // DateTimeOffset ordering comments above). Client count is tiny.
        return clients.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<Client> UpdateClientAsync(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.NormalizeAndValidate();

        await using var db = await _factory.CreateDbContextAsync();

        var existing = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == client.ClientId)
            ?? throw new InvalidOperationException($"Client {client.ClientId} not found.");

        // The unique index would also catch this, but a pre-check keeps the
        // error message consistent with the file store.
        var duplicate = await db.Clients.AnyAsync(c =>
            c.ClientId != client.ClientId && c.Name.ToLower() == client.Name.ToLower());
        if (duplicate)
            throw new InvalidOperationException($"A client named '{client.Name}' already exists.");

        existing.Name = client.Name;
        existing.Notes = client.Notes;
        existing.ContactPhone = client.ContactPhone;
        existing.ContactEmail = client.ContactEmail;
        existing.GitRepositoryUrl = client.GitRepositoryUrl;
        existing.DeploymentBranch = client.DeploymentBranch;
        existing.PublishConfigurationJson = client.PublishConfigurationJson;
        existing.HasAmc = client.HasAmc;
        existing.AmcExpiryDate = client.AmcExpiryDate;
        existing.InfrastructureManagedBy = client.InfrastructureManagedBy;
        existing.HostingAccountManagedBy = client.HostingAccountManagedBy;

        await db.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteClientAsync(string clientId)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId)
            ?? throw new InvalidOperationException($"Client {clientId} not found.");

        // Mirrors the Restrict FK with a message a human can act on; the
        // registry is an audit trail, so parents with children are never
        // silently cascade-deleted.
        var componentCount = await db.Components.CountAsync(c => c.ClientId == clientId);
        if (componentCount > 0)
            throw new InvalidOperationException(
                $"Client '{client.Name}' still has {componentCount} component(s). Delete its components first — registry rows are an audit trail and are never cascade-deleted.");

        db.Clients.Remove(client);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------
    // Components

    public async Task<DeploymentComponent?> GetComponentAsync(string componentId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Components.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ComponentId == componentId);
    }

    public async Task<IReadOnlyList<DeploymentComponent>> GetComponentsForClientAsync(string clientId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Components.AsNoTracking()
            .Where(c => c.ClientId == clientId)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<DeploymentComponent> CreateComponentAsync(DeploymentComponent component)
    {
        await using var db = await _factory.CreateDbContextAsync();

        db.Components.Add(component);
        await db.SaveChangesAsync();
        return component;
    }

    public async Task<DeploymentComponent> UpdateComponentAsync(DeploymentComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        await using var db = await _factory.CreateDbContextAsync();

        var existing = await db.Components.FirstOrDefaultAsync(c => c.ComponentId == component.ComponentId)
            ?? throw new InvalidOperationException($"Component {component.ComponentId} not found.");

        // DeploymentComponent is init-only: copy the incoming values onto the
        // tracked instance instead of trying to mutate it in place.
        db.Entry(existing).CurrentValues.SetValues(component);
        await db.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteComponentAsync(string componentId)
    {
        if (string.IsNullOrWhiteSpace(componentId))
            throw new ArgumentException("ComponentId is required.", nameof(componentId));

        await using var db = await _factory.CreateDbContextAsync();

        var component = await db.Components.FirstOrDefaultAsync(c => c.ComponentId == componentId)
            ?? throw new InvalidOperationException($"Component {componentId} not found.");

        // Audit-trail rule: never cascade-delete a parent with children. The
        // user must delete the packages first (DeletePackageAsync).
        var packageCount = await db.Packages.CountAsync(p => p.ComponentId == componentId);
        if (packageCount > 0)
            throw new InvalidOperationException(
                $"Component '{component.Name}' still has {packageCount} package(s). Delete its packages first — registry rows are an audit trail and are never cascade-deleted.");

        db.Components.Remove(component);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------
    // Packages

    public async Task<PackageRecord?> GetLatestDeployedPackageAsync(string componentId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        // Filter server-side; order client-side. DateTimeOffset ordering is
        // not portable across providers (SQLite refuses it in ORDER BY), and
        // the per-component row count is tiny, so this keeps identical
        // semantics (newest DeployedUtc wins) on every provider.
        var deployed = await db.Packages.AsNoTracking()
            .Where(p => p.ComponentId == componentId && p.Status == PackageStatus.Deployed)
            .ToListAsync();
        return deployed
            .OrderByDescending(p => p.DeployedUtc)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<PackageRecord>> GetUndeployedPackagesAsync(string componentId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var created = await db.Packages.AsNoTracking()
            .Where(p => p.ComponentId == componentId && p.Status == PackageStatus.Created)
            .ToListAsync();
        return created
            .OrderBy(p => p.CreatedUtc)
            .ToList();
    }

    public async Task<PackageRecord> CreatePackageAsync(string componentId, ComponentManifest manifest)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var record = new PackageRecord
        {
            PackageId = Guid.NewGuid().ToString("N"),
            ComponentId = componentId,
            Version = manifest.Version,
            CreatedUtc = manifest.CreatedUtc,
            // Same serializer the file store and the package writer use, so
            // baseline manifests deserialize identically no matter which
            // store recorded them.
            ManifestJson = ManifestSerializer.Serialize(manifest),
            GitCommitSha = manifest.GitCommitSha,
            Status = PackageStatus.Created,
        };

        db.Packages.Add(record);
        await db.SaveChangesAsync();
        return record;
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
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Packages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PackageId == packageId);
    }

    public async Task<IReadOnlyList<PackageRecord>> GetPackagesForComponentAsync(string componentId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var packages = await db.Packages.AsNoTracking()
            .Where(p => p.ComponentId == componentId)
            .ToListAsync();
        // Client-side ordering again: SQLite refuses DateTimeOffset in ORDER BY.
        return packages.OrderByDescending(p => p.CreatedUtc).ToList();
    }

    public async Task DeletePackageAsync(string packageId, bool deleteRunHistory = false)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var package = await db.Packages.FirstOrDefaultAsync(p => p.PackageId == packageId)
            ?? throw new InvalidOperationException($"Package {packageId} not found.");

        var runCount = await db.DeploymentRuns.CountAsync(r => r.PackageId == packageId);
        if (runCount > 0 && !deleteRunHistory)
            throw new InvalidOperationException(
                $"Package {packageId} has {runCount} recorded deployment run(s). Pass deleteRunHistory:true to remove the package together with its run history — this cannot be undone.");

        if (runCount > 0)
        {
            var runs = db.DeploymentRuns.Where(r => r.PackageId == packageId);
            db.DeploymentRuns.RemoveRange(runs);
            await db.SaveChangesAsync();
        }

        db.Packages.Remove(package);
        await db.SaveChangesAsync();
    }

    private async Task UpdatePackageAsync(string packageId, Action<PackageRecord> mutate)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var package = await db.Packages.FirstOrDefaultAsync(p => p.PackageId == packageId)
            ?? throw new InvalidOperationException($"Package {packageId} not found.");

        mutate(package);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------
    // Deployment runs

    public async Task<DeploymentRunRecord> RecordRunStartAsync(string packageId, DateTimeOffset startedUtc)
    {
        await using var db = await _factory.CreateDbContextAsync();

        // Same FK-violation surface as the file store's "package not found",
        // but expressed at the database.
        var packageExists = await db.Packages.AnyAsync(p => p.PackageId == packageId);
        if (!packageExists)
            throw new InvalidOperationException($"Package {packageId} not found.");

        var run = new DeploymentRunRecord
        {
            RunId = Guid.NewGuid().ToString("N"),
            PackageId = packageId,
            StartedUtc = startedUtc,
        };

        db.DeploymentRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    public async Task RecordRunCompleteAsync(string runId, string result, bool? healthCheckResult, string? logPath)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var run = await db.DeploymentRuns.FirstOrDefaultAsync(r => r.RunId == runId)
            ?? throw new InvalidOperationException($"Run {runId} not found.");

        run.CompletedUtc = DateTimeOffset.UtcNow;
        run.Result = result;
        run.HealthCheckResult = healthCheckResult;
        run.LogPath = logPath;

        await db.SaveChangesAsync();
    }
}
