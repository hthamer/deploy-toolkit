using System.Text.Json;
using System.Text.Json.Serialization;
using DeployToolkit.Core.EfCore;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.AppKit;

/// <summary>
/// Where the central registry (plan §2.2) lives: a SQL Server / Azure SQL
/// database (production, via <see cref="EfCoreRegistryStore"/>), or a local
/// folder of JSON files (offline-mode fallback, via
/// <see cref="LocalFileRegistryStore"/>). The Packager/Deployer shells expose
/// this choice through <see cref="ConnectionDialog"/> and persist the result
/// with <see cref="RegistryConnectionSettings.Save"/>.
/// </summary>
public enum RegistryMode
{
    SqlServer,
    LocalFile
}

/// <summary>
/// The persisted "which registry am I connected to" settings. Pure data —
/// deliberately contains NO WinForms types so it can be exercised by the
/// headless self-test suite (tools/DeployToolkit.AppKit.SelfTest).
///
/// Persistence contract:
///  - <see cref="Load"/> tolerates a missing or corrupt file (returns
///    defaults, never throws at startup — a broken settings file must not
///    stop the app from launching) and tolerates unknown JSON fields
///    (forward compatibility with future versions).
///  - <see cref="Save"/> is atomic-ish: written to a temp file first, then
///    moved over the target, so a crash mid-save cannot truncate the file.
/// </summary>
public sealed class RegistryConnectionSettings
{
    public RegistryMode Mode { get; set; } = RegistryMode.LocalFile;

    /// <summary>SQL Server / Azure SQL connection string (SQL Server mode only).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Root folder holding the offline registry files (LocalFile mode only).</summary>
    public string? LocalRoot { get; set; }

    /// <summary>
    /// The shared folder where built delta.zips are published so the Deployer
    /// (on another machine) can fetch them without the builder copying the
    /// file by hand (Option B: shared folder + registry links the package).
    /// A UNC path (<c>\\fileserver\DeployToolkit\Packages</c>) or a local
    /// path. Null/empty = no store configured — the .zip lives only on the
    /// builder's PC and must be copied to the deployer manually (the
    /// pre-Option-B behavior). Applies to BOTH modes (SqlServer and
    /// LocalFile) — the store is independent of where the registry lives.
    /// Credentials for a password-protected share are resolved automatically
    /// from Windows Credential Manager; no auth is configured here.
    /// </summary>
    public string? PackageStoreRootPath { get; set; }

    /// <summary>
    /// Where the shells persist these settings by default:
    /// %APPDATA%\DeployToolkit\packager-registry.json (falls back to the
    /// current directory when %APPDATA% is unavailable, e.g. on some CI hosts).
    /// </summary>
    public static string DefaultSettingsPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                appData = Environment.CurrentDirectory;
            return Path.Combine(appData, "DeployToolkit", "packager-registry.json");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }, // "SqlServer"/"LocalFile" stay human-readable in the file
    };

    /// <summary>Loads settings from <paramref name="filePath"/>. A missing,
    /// unreadable or corrupt file yields the defaults (LocalFile mode, no
    /// roots set) instead of throwing — startup must never die on a bad
    /// settings file.</summary>
    public static RegistryConnectionSettings Load(string filePath)
    {
        var defaults = new RegistryConnectionSettings();
        try
        {
            if (!File.Exists(filePath))
                return defaults;

            var loaded = JsonSerializer.Deserialize<RegistryConnectionSettings>(
                File.ReadAllText(filePath), JsonOptions);
            return loaded ?? defaults;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return defaults;
        }
    }

    /// <summary>Writes these settings to <paramref name="filePath"/> atomically
    /// (temp file + move), creating the containing directory when needed.</summary>
    public void Save(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tempPath, filePath, overwrite: true);
    }
}

/// <summary>
/// Builds an open <see cref="IRegistryStore"/> from
/// <see cref="RegistryConnectionSettings"/> — the one place that knows which
/// store implementation backs each mode.
/// </summary>
public static class RegistryConnectionFactory
{
    /// <summary>
    /// Checks that the settings are complete enough to attempt a connection.
    /// Throws <see cref="ArgumentException"/> with an actionable message
    /// naming the offending field.
    /// </summary>
    public static void Validate(RegistryConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        switch (settings.Mode)
        {
            case RegistryMode.SqlServer:
                if (string.IsNullOrWhiteSpace(settings.ConnectionString))
                    throw new ArgumentException(
                        "ConnectionString is required for SQL Server mode. " +
                        "Example: \"Server=tcp:<server>.database.windows.net,1433;Database=DeployToolkitRegistry;Authentication=Active Directory Default;Encrypt=True\".");
                break;

            case RegistryMode.LocalFile:
                if (string.IsNullOrWhiteSpace(settings.LocalRoot))
                    throw new ArgumentException(
                        "LocalRoot is required for local-file mode. " +
                        "Pick the folder that holds (or will hold) the offline registry files (clients.json, components.json, …).");
                break;

            default:
                throw new ArgumentException($"Unknown registry mode '{settings.Mode}'.", nameof(settings));
        }
    }

    /// <summary>
    /// Creates and "opens" the store described by <paramref name="settings"/>.
    ///
    ///  - SQL Server mode: builds <see cref="EfCoreRegistryStore.CreateSqlServer"/>
    ///    and runs <see cref="EfCoreRegistryStore.InitializeAsync"/> (apply EF
    ///    migrations — production-correct bootstrap). If the server is
    ///    unreachable this throws the underlying connection exception — that is
    ///    deliberate: the caller (ConnectionDialog / shell startup) turns it
    ///    into a friendly "change connection" prompt instead of silently
    ///    handing the UI a dead store.
    ///  - LocalFile mode: creates the root folder if needed and returns a
    ///    <see cref="LocalFileRegistryStore"/> over it.
    /// </summary>
    public static async Task<IRegistryStore> CreateOpenAsync(RegistryConnectionSettings settings)
    {
        Validate(settings);

        switch (settings.Mode)
        {
            case RegistryMode.SqlServer:
            {
                var store = EfCoreRegistryStore.CreateSqlServer(settings.ConnectionString!);
                await store.InitializeAsync().ConfigureAwait(false);
                return store;
            }

            default: // RegistryMode.LocalFile (Validate rejects anything else)
                return new LocalFileRegistryStore(settings.LocalRoot!);
        }
    }
}
