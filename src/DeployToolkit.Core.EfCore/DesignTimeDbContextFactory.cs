using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DeployToolkit.Core.EfCore;

/// <summary>
/// Used only by the `dotnet ef` tooling (migrations add / script / update) —
/// never by the running apps. Lets migrations be created without a live
/// SQL Server instance. Override the target with the REGISTRY_CONNECTION_STRING
/// environment variable if your dev DB differs from LocalDB.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RegistryDbContext>
{
    public RegistryDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("REGISTRY_CONNECTION_STRING")
            ?? @"Server=(localdb)\MSSQLLocalDB;Database=DeployToolkitRegistry;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<RegistryDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new RegistryDbContext(options);
    }
}
