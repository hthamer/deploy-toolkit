using DeployToolkit.Core.Git;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Packager;

/// <summary>
/// Everything one "new package" wizard run accumulates across its seven
/// steps (plan §10). Internal to the wizard — created fresh per wizard,
/// reset selectively by the "build another package" action.
/// </summary>
internal sealed class PackageDraft
{
    /// <summary>Step 1: the git working folder the package is built from.</summary>
    public string? FolderPath { get; set; }

    /// <summary>Step 1: git fetch/pull result (null when the folder is not a repo or sync was skipped).</summary>
    public GitSyncResult? GitSync { get; set; }

    /// <summary>Step 1: the resolved (or newly created) component this package targets.</summary>
    public DeploymentComponent? Component { get; set; }

    /// <summary>Step 2: dotnet publish output folder (fresh temp folder per build).</summary>
    public string? PublishOutputRoot { get; set; }

    /// <summary>Step 2: package version (required, trimmed, no spaces).</summary>
    public string? Version { get; set; }

    /// <summary>Step 2: whether the last publish attempt succeeded.</summary>
    public bool PublishSuccess { get; set; }

    /// <summary>Step 3: freshly hashed files of the publish output (preview source).</summary>
    public IReadOnlyList<ManifestFile>? CurrentFiles { get; set; }

    /// <summary>Step 3: preview diff vs. the last Deployed baseline (recomputed at build time — preview only).</summary>
    public ManifestDiffResult? DiffPreview { get; set; }

    /// <summary>Step 3: paths the user unchecked in the diff grid (manual exclude, plan §10 step 4).</summary>
    public HashSet<string> ExcludedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Step 4: appsettings key/value delta for this release.</summary>
    public IReadOnlyDictionary<string, object?> AppSettingsDelta { get; set; } =
        new Dictionary<string, object?>();

    /// <summary>Step 5: attached DB scripts (embedded under db/ in the package).</summary>
    public List<DbScriptRef> DbScripts { get; } = new();

    /// <summary>Step 5: maps each script file name to its full source path on disk.</summary>
    public Dictionary<string, string> DbScriptSourcePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Step 6: where the finished delta.zip is written.</summary>
    public string? OutputZipPath { get; set; }

    /// <summary>Step 6: the build outcome (Finish is enabled only when this is set).</summary>
    public PackageBuildResult? BuildResult { get; set; }

    /// <summary>True when enough state exists for the diff step to run.</summary>
    public bool IsReadyForDiff => PublishSuccess && PublishOutputRoot is not null && Component is not null;

    /// <summary>
    /// Resets everything AFTER the component/folder choice — used by the
    /// "build another package for this component" action (keeps step-1 state).
    /// </summary>
    public void ResetForRebuild()
    {
        PublishOutputRoot = null;
        Version = null;
        PublishSuccess = false;
        CurrentFiles = null;
        DiffPreview = null;
        ExcludedPaths.Clear();
        AppSettingsDelta = new Dictionary<string, object?>();
        DbScripts.Clear();
        DbScriptSourcePaths.Clear();
        OutputZipPath = null;
        BuildResult = null;
    }
}
