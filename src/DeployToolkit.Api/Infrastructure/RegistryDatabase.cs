using DeployToolkit.Api.Auth;
using DeployToolkit.Core.EfCore;
using Microsoft.EntityFrameworkCore;

namespace DeployToolkit.Api.Infrastructure;

/// <summary>
/// Wires <see cref="RegistryDbContext"/> against the SAME registry database
/// the Packager app uses (plan §2.2 — one central registry, many clients).
/// Differences from the desktop apps, both deliberate:
///
///  1. The connection string comes from <c>ConnectionStrings:Registry</c> in
///     appsettings.json (standard ASP.NET Core configuration pipeline:
///     appsettings → environment variables like
///     <c>ConnectionStrings__Registry</c> → secrets), NOT from the WinForms
///     settings file the Packager uses.
///  2. The schema (including the <c>ApiUsers</c> table this API adds) is
///     applied by the EF migrations in DeployToolkit.Core.EfCore at startup
///     — idempotent, exactly what <see cref="EfCoreRegistryStore.InitializeAsync"/>
///     does for the Packager, so a fresh server comes up with one command.
///
/// Provider note: production is SQL Server / Azure SQL (the migrations are
/// SQL Server-flavored). <c>Database:Provider=Sqlite</c> exists for local
/// dev / smoke tests ONLY — the model is provider-neutral, so the schema is
/// then built with EnsureCreated (same documented split the
/// DeployToolkit.EfCore.SelfTest harness uses).
/// </summary>
public static class RegistryDatabase
{
    /// <summary>Registers the registry DB context with the configured
    /// provider. Throws immediately (with an actionable message) when the
    /// configuration is incomplete — a database-less auth API is useless.</summary>
    public static IServiceCollection AddRegistryDatabase(
        this IServiceCollection services, IConfiguration configuration)
    {
        var provider = (configuration["Database:Provider"] ?? "SqlServer").Trim();
        var connectionString = configuration.GetConnectionString("Registry")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Registry is missing from configuration. Add it to " +
                "appsettings.json (or set the ConnectionStrings__Registry environment " +
                "variable) — it must point at the same DeployToolkitRegistry database " +
                "the Packager app uses.");

        services.AddDbContext<RegistryDbContext>(options =>
        {
            switch (provider.ToLowerInvariant())
            {
                case "sqlserver":
                    options.UseSqlServer(connectionString);
                    break;

                case "sqlite":
                    options.UseSqlite(connectionString);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Database:Provider '{provider}' is not supported. " +
                        "Use 'SqlServer' (production) or 'Sqlite' (local development only).");
            }
        });

        return services;
    }

    /// <summary>
    /// Called once before the app starts accepting traffic:
    ///  1. applies the migrations (SQL Server) or builds the schema from the
    ///     model (SQLite dev store) — both no-ops when already up to date;
    ///  2. seeds the initial API user when <c>ApiUsers</c> is empty and
    ///     <c>Auth:SeedAdmin</c> is configured. The seed password is hashed
    ///     with PBKDF2 before it touches the database and never logged.
    /// </summary>
    public static async Task InitializeRegistryDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegistryDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        var provider = (configuration["Database:Provider"] ?? "SqlServer").Trim();
        var applySchema = configuration.GetValue("Database:ApplyMigrationsOnStartup", true);

        if (applySchema)
        {
            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Applying registry database migrations (SQL Server)…");
                await db.Database.MigrateAsync();
                logger.LogInformation("Registry database is up to date.");
            }
            else
            {
                logger.LogInformation(
                    "Ensuring registry schema exists on the {Provider} dev store…", provider);
                await db.Database.EnsureCreatedAsync();
            }
        }

        await SeedInitialApiUserAsync(db, hasher, configuration, logger);
    }

    /// <summary>
    /// Bootstraps the FIRST API user only (when <c>ApiUsers</c> is empty).
    /// Later user management (create/disable/password change) is phase 2+;
    /// until then, additional users are inserted directly into
    /// <c>ApiUsers</c> with hashes produced by the same PBKDF2 format.
    /// </summary>
    private static async Task SeedInitialApiUserAsync(
        RegistryDbContext db,
        IPasswordHasher hasher,
        IConfiguration configuration,
        ILogger<Program> logger)
    {
        if (await db.ApiUsers.AnyAsync())
            return;

        var username = configuration["Auth:SeedAdmin:Username"];
        var password = configuration["Auth:SeedAdmin:Password"];
        var displayName = configuration["Auth:SeedAdmin:DisplayName"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "The registry database has no API users yet, so every call to " +
                "POST /api/auth/authenticate will return 401. Set Auth:SeedAdmin:Username " +
                "and Auth:SeedAdmin:Password (env: Auth__SeedAdmin__Username / " +
                "Auth__SeedAdmin__Password) and restart once to create the initial user.");
            return;
        }

        db.ApiUsers.Add(new ApiUser
        {
            UserId = Guid.NewGuid().ToString("N"),
            Username = username.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            PasswordHash = hasher.Hash(password),
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Seeded initial API user '{Username}' into ApiUsers (PBKDF2-SHA256 hash; " +
            "the plaintext password is not stored).",
            username.Trim());
    }
}
