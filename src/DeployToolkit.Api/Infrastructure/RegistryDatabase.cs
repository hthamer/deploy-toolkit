using DeployToolkit.Api.Auth;
using DeployToolkit.Api.Infrastructure;
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
///  2. The schema (including the <c>ApiUsers</c>, <c>ApiSettings</c> and
///     <c>ApiCredentialLogs</c> tables this API adds) is applied by the EF
///     migrations in DeployToolkit.Core.EfCore at startup — idempotent,
///     exactly what <see cref="EfCoreRegistryStore.InitializeAsync"/> does
///     for the Packager, so a fresh server comes up with one command.
///
/// Provider note: production is SQL Server / Azure SQL (the migrations are
/// SQL Server-flavored). <c>Database:Provider=Sqlite</c> exists for local
/// dev / smoke tests ONLY — the model is provider-neutral, so the schema is
/// then built with EnsureCreated (same documented split the
/// DeployToolkit.EfCore.SelfTest harness uses).
///
/// Credential policy (user requirement): the username and password are
/// ALWAYS registered in the database — never in appsettings.json or any
/// other configuration file. On first run against an empty <c>ApiUsers</c>
/// table this bootstrap generates the initial credentials (username
/// <c>admin</c> + a crypto-random password), stores the PBKDF2 hash and
/// registers the credential in <c>ApiCredentialLogs</c>.
/// </summary>
public static class RegistryDatabase
{
    /// <summary>Username of the auto-registered first API account. Chosen in
    /// code (not configuration) so no credential material ever lives outside
    /// the database; rename the row by hand if a different login is wanted.</summary>
    public const string InitialUsername = "admin";

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

        // Typed accessor over the ApiSettings table (rotation settings live
        // in the database, not appsettings.json).
        services.AddScoped<ApiSettingsStore>();

        return services;
    }

    /// <summary>
    /// Called once before the app starts accepting traffic:
    ///  1. applies the migrations (SQL Server) or builds the schema from the
    ///     model (SQLite dev store) — both no-ops when already up to date;
    ///  2. seeds any missing default rotation settings into
    ///     <c>ApiSettings</c> (existing operator edits are never touched);
    ///  3. registers the initial API credential IN THE DATABASE when
    ///     <c>ApiUsers</c> is empty — generated here, never read from
    ///     configuration.
    /// </summary>
    public static async Task InitializeRegistryDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegistryDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var settings = scope.ServiceProvider.GetRequiredService<ApiSettingsStore>();
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

        // Rotation settings: DB rows, not config. Missing keys get the
        // documented defaults; anything the operator already tuned wins.
        await settings.EnsureDefaultsAsync();

        await SeedInitialApiUserAsync(db, hasher, settings, logger);
    }

    /// <summary>
    /// Registers the FIRST API credential in the database (only when
    /// <c>ApiUsers</c> is empty). The username is the fixed
    /// <see cref="InitialUsername"/> and the password is crypto-random —
    /// generated HERE and written to <c>ApiUsers</c> (hash) +
    /// <c>ApiCredentialLogs</c> (plaintext, Reason=InitialSeed). Nothing is
    /// read from appsettings.json; the plaintext never appears in the log.
    /// Later user management (create/disable/password change) is phase 2+;
    /// until then, additional users are inserted directly into
    /// <c>ApiUsers</c> with hashes produced by the same PBKDF2 format.
    /// </summary>
    private static async Task SeedInitialApiUserAsync(
        RegistryDbContext db,
        IPasswordHasher hasher,
        ApiSettingsStore settings,
        ILogger<Program> logger)
    {
        if (await db.ApiUsers.AnyAsync())
            return;

        var rotation = await settings.GetRotationSettingsAsync();
        var interval = TimeSpan.FromMinutes(
            Math.Max(PasswordRotationMinimumInterval, rotation.IntervalMinutes));
        var password = RandomPasswordGenerator.Generate(rotation.PasswordLength);
        var now = DateTimeOffset.UtcNow;

        db.ApiUsers.Add(new ApiUser
        {
            UserId = Guid.NewGuid().ToString("N"),
            Username = InitialUsername,
            DisplayName = "Registry Administrator",
            PasswordHash = hasher.Hash(password),
            IsActive = true,
            CreatedUtc = now,
            PasswordChangedUtc = now,
        });

        // The credential is REGISTERED IN THE DATABASE (user requirement):
        // the latest ApiCredentialLogs row per username is the current
        // working credential — retrievable with a plain SELECT, no config
        // files, no state file.
        db.ApiCredentialLogs.Add(new ApiCredentialLog
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = InitialUsername,
            Password = password,
            Reason = "InitialSeed",
            CreatedUtc = now,
        });

        // The freshly registered password IS current — schedule the first
        // rotation one interval out (overriding the "due now" adoption
        // default, which only makes sense for registries that already had
        // users before this feature existed).
        await settings.SetAsync(ApiSettingKeys.RotationLastRunUtc, now.ToString("O"));
        await settings.SetAsync(ApiSettingKeys.RotationNextRunUtc, now.Add(interval).ToString("O"));

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Registered the initial API credential for user '{Username}' in the registry " +
            "database (PBKDF2 hash in ApiUsers, plaintext in ApiCredentialLogs / " +
            "Reason=InitialSeed — never in a config file or the log). Current credential: " +
            "SELECT TOP 1 Username, Password, CreatedUtc FROM ApiCredentialLogs " +
            "WHERE Username = '{Username}' ORDER BY CreatedUtc DESC. First rotation scheduled " +
            "{Minutes:0.#} minute(s) after startup.",
            InitialUsername, InitialUsername, interval.TotalMinutes);
    }

    /// <summary>Mirrors <see cref="Auth.PasswordRotationService"/>'s interval
    /// floor (0.5 min) — kept as a constant here to avoid a circular dependency.</summary>
    private const double PasswordRotationMinimumInterval = 0.5;
}
