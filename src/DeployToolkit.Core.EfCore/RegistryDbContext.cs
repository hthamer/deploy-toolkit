using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;
using Microsoft.EntityFrameworkCore;

namespace DeployToolkit.Core.EfCore;

/// <summary>
/// EF Core model over the exact domain POCOs the rest of the toolkit already
/// uses (plan §2.2 schema). Deliberately provider-neutral — no SQL Server
/// specific annotations — so the same model (and the same migrations)
/// validate and run against SQL Server / Azure SQL in production AND against
/// SQLite in the offline self-test. All configuration is done here; the
/// domain classes stay plain.
///
/// Design notes:
///  - String keys (Guids stored as "N"-format, matching the existing domain
///    code) with explicit lengths — required for SQL Server index key limits
///    and to avoid nvarchar(max) keys.
///  - Enums stored as readable strings (TargetType, PackageStatus) so the
///    registry DB can be audited by hand with plain SELECTs.
///  - No navigation properties — the domain POCOs have none, and this
///    registry is accessed through <see cref="EfCoreRegistryStore"/>'s
///    purpose-built queries rather than arbitrary graph traversals.
///  - Restrict on Client→Component→Package FKs: registry rows are an audit
///    trail, so deleting a parent that still has children must fail loudly,
///    never cascade-delete history. Runs cascade from their package because
///    a run has no meaning without it.
/// </summary>
public sealed class RegistryDbContext : DbContext
{
    public RegistryDbContext(DbContextOptions<RegistryDbContext> options)
        : base(options)
    {
    }

    public DbSet<Client> Clients => Set<Client>();
    public DbSet<DeploymentComponent> Components => Set<DeploymentComponent>();
    public DbSet<PackageRecord> Packages => Set<PackageRecord>();
    public DbSet<DeploymentRunRecord> DeploymentRuns => Set<DeploymentRunRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>(e =>
        {
            e.ToTable("Clients");
            e.HasKey(c => c.ClientId);
            e.Property(c => c.ClientId).HasMaxLength(32).ValueGeneratedNever();
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.Notes).HasMaxLength(2000);

            // Client profile columns (pre-WinForms client management):
            e.Property(c => c.ContactPhone).HasMaxLength(50);
            e.Property(c => c.ContactEmail).HasMaxLength(255);
            e.Property(c => c.GitRepositoryUrl).HasMaxLength(1024);
            e.Property(c => c.DeploymentBranch).HasMaxLength(100);
            // Canonical JSON from PublishConfigurationSerializer — same
            // audit-in-the-open pattern as Packages.ManifestJson.
            e.Property(c => c.PublishConfigurationJson);
            e.Property(c => c.InfrastructureManagedBy)
                .HasConversion<string>()
                .HasMaxLength(20);
            e.Property(c => c.HostingAccountManagedBy).HasMaxLength(200);

            // Client names are the human-facing lookup key (Packager's
            // "create or reuse client" path) — keep them unique.
            e.HasIndex(c => c.Name).IsUnique();
        });

        modelBuilder.Entity<DeploymentComponent>(e =>
        {
            e.ToTable("DeploymentComponents");
            e.HasKey(c => c.ComponentId);
            e.Property(c => c.ComponentId).HasMaxLength(32).ValueGeneratedNever();
            e.Property(c => c.ClientId).HasMaxLength(32);
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.TargetType)
                .HasConversion<string>()
                .HasMaxLength(20);
            e.Property(c => c.TargetFramework).HasMaxLength(50);
            e.Property(c => c.IisSiteName).HasMaxLength(255);
            e.Property(c => c.IisAppPath).HasMaxLength(255);
            e.Property(c => c.AzureAppServiceName).HasMaxLength(255);
            e.Property(c => c.AzureResourceGroup).HasMaxLength(255);
            e.Property(c => c.PleskHost).HasMaxLength(255);
            e.Property(c => c.PleskSiteId).HasMaxLength(64);
            e.Property(c => c.HealthCheckUrl).HasMaxLength(1024);
            e.Property(c => c.DbConnectionRef).HasMaxLength(256);

            e.HasOne<Client>()
                .WithMany()
                .HasForeignKey(c => c.ClientId)
                .OnDelete(DeleteBehavior.Restrict);

            // One "CMS" per client: component names are unique per client.
            e.HasIndex(c => new { c.ClientId, c.Name }).IsUnique();
        });

        modelBuilder.Entity<PackageRecord>(e =>
        {
            e.ToTable("Packages");
            e.HasKey(p => p.PackageId);
            e.Property(p => p.PackageId).HasMaxLength(32).ValueGeneratedNever();
            e.Property(p => p.ComponentId).HasMaxLength(32);
            e.Property(p => p.Version).HasMaxLength(50);
            // Full manifest JSON is the audit record of exactly what shipped.
            e.Property(p => p.ManifestJson);
            e.Property(p => p.GitCommitSha).HasMaxLength(64);
            e.Property(p => p.Status)
                .HasConversion<string>()
                .HasMaxLength(20);
            e.Property(p => p.DeployedBy).HasMaxLength(100);
            // Where the built .zip physically lives (Option B: shared folder +
            // registry links the package). UNC path / local path / URL; null
            // when no package store is configured (the .zip lives only on the
            // builder's PC and must be copied by hand).
            e.Property(p => p.PackageLocation).HasMaxLength(512);

            e.HasOne<DeploymentComponent>()
                .WithMany()
                .HasForeignKey(p => p.ComponentId)
                .OnDelete(DeleteBehavior.Restrict);

            // Hot queries: latest-deployed baseline lookup and the
            // stale/"Created but never deployed" scan (plan §9).
            e.HasIndex(p => new { p.ComponentId, p.Status });
            e.HasIndex(p => new { p.ComponentId, p.CreatedUtc });
        });

        modelBuilder.Entity<DeploymentRunRecord>(e =>
        {
            e.ToTable("DeploymentRuns");
            e.HasKey(r => r.RunId);
            e.Property(r => r.RunId).HasMaxLength(32).ValueGeneratedNever();
            e.Property(r => r.PackageId).HasMaxLength(32);
            e.Property(r => r.Result).HasMaxLength(20);
            e.Property(r => r.LogPath).HasMaxLength(1024);

            e.HasOne<PackageRecord>()
                .WithMany()
                .HasForeignKey(r => r.PackageId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(r => r.PackageId);
        });
    }
}
