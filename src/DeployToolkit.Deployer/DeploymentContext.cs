using DeployToolkit.Core.IisControl;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;
using DeployToolkit.Core.Targets.Plesk;

namespace DeployToolkit.Deployer;

/// <summary>
/// Everything the Deployer knows about the package currently being deployed.
/// One instance per loaded package; recreated by "Finish"/a fresh load.
/// Filled progressively by the stage panels (plan §11 steps 1–4) and read by
/// the deploy stage (plan §11 steps 5–9). Credentials collected here
/// (DB connection string, Kudu password, ARM token) exist for the duration
/// of the run only — nothing here is ever persisted to disk.
/// </summary>
internal sealed class DeploymentContext
{
    // ---------------------------------------------------------------
    // Step 1 — package (set by StageLoadPackage)

    public required string ZipPath { get; set; }

    public required ComponentManifest Manifest { get; set; }

    /// <summary>
    /// The registry package row this zip matches (or was recorded as, in
    /// offline mode). The manifest itself carries only ComponentId — the
    /// package id the orchestrator needs comes from this record. Null only
    /// while the match/record choice has not been made yet.
    /// </summary>
    public PackageRecord? Package { get; set; }

    /// <summary>The registry component, when known (null in offline mode
    /// when the component record was never synced to this machine).</summary>
    public DeploymentComponent? Component { get; set; }

    /// <summary>True when the registry behind this run is the local-file
    /// offline fallback (controls package-record-if-missing and offline
    /// result writing).</summary>
    public bool OfflineMode { get; set; }

    // ---------------------------------------------------------------
    // Step 2 — target resolution (set by StageResolveTarget)

    /// <summary>The effective target type: the component's registry value
    /// when known, otherwise the user's explicit choice (the manifest has no
    /// target-type field — plan §3). Set by the resolve stage in all flows.</summary>
    public TargetType? TargetType { get; set; }

    /// <summary>The resolved IIS target (site/app/physical path/app pool),
    /// when the run targets IIS. Physical path and app pool always come from
    /// live IIS data via <see cref="DeployToolkit.Core.IisControl.IisTargetResolver"/>.</summary>
    public IisResolvedTarget? IisTarget { get; set; }

    /// <summary>The live IIS controller built during resolution — reused by
    /// the deploy stage so the run talks to the same server state it
    /// resolved against. Null for non-IIS targets.</summary>
    public IIisController? IisController { get; set; }

    // ---------------------------------------------------------------
    // Step 3 — pre-flight inputs (set by StagePreflight)

    /// <summary>IIS only: the site root files are deployed into (defaults to
    /// the resolved IIS physical path). Hidden for Azure/Plesk targets.</summary>
    public string? SiteRoot { get; set; }

    /// <summary>IIS only: the appsettings.json the orchestrator merges the
    /// manifest's delta into.</summary>
    public string? AppSettingsPath { get; set; }

    // Azure App Service inputs.
    public string? KuduSiteName { get; set; }
    public string? KuduUsername { get; set; }
    public string? KuduPassword { get; set; }

    /// <summary>Whether the user enabled applying the manifest's appsettings
    /// delta via the ARM Configuration API (optional; zip deploy alone is
    /// valid — the executor reports skipped settings honestly).</summary>
    public bool ApplyAzureSettings { get; set; }

    public string? ArmSubscriptionId { get; set; }
    public string? ArmResourceGroup { get; set; }
    public string? ArmSiteName { get; set; }

    /// <summary>Bearer token for ARM app-settings calls, pasted by the user
    /// (Azure.Identity is deliberately not wired in v1 — Core carries no
    /// Azure SDK; see AzureAppSettingsClient's token-provider seam).</summary>
    public string? ArmToken { get; set; }

    // Plesk inputs.
    public PleskConnectionOptions? PleskConnection { get; set; }
    public PleskDeployOptions? PleskDeploy { get; set; }

    // ---------------------------------------------------------------
    // Step 5 — deploy run (set by StageDeploy)

    /// <summary>
    /// The target DB connection string resolved for this run (manual entry
    /// or unlocked from the local SecretVault). Never persisted anywhere —
    /// cleared when the context is dropped. Null = DB scripts are skipped
    /// this run (user-confirmed).
    /// </summary>
    public string? DbConnectionString { get; set; }
}
