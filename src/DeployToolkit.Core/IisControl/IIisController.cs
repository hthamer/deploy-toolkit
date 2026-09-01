namespace DeployToolkit.Core.IisControl;

/// <summary>One IIS site (top-level application container).</summary>
public sealed record IisSiteInfo(string Name, string? AppPoolName, string PhysicalPath, bool Started);

/// <summary>One application under a site — the root app has Path "/",
/// nested apps have paths like "/cms" (plan §6 topologies).</summary>
public sealed record IisApplicationInfo(string SiteName, string Path, string? AppPoolName, string PhysicalPath);

/// <summary>One application pool.</summary>
public sealed record IisAppPoolInfo(string Name, string? ManagedRuntimeVersion, bool Started);

/// <summary>
/// Everything the tool needs from IIS, expressed as an interface so the
/// whole deploy flow is testable without a Windows box. The real
/// implementation (Microsoft.Web.Administration) lives in
/// DeployToolkit.Core.Windows — IIS APIs don't exist on other OSes, and
/// that thin wrapper is the ONLY place that touches them (plan §8.3).
/// All members are synchronous because Microsoft.Web.Administration is.
/// </summary>
public interface IIisController
{
    IReadOnlyList<IisSiteInfo> EnumerateSites();

    /// <param name="siteName">null/empty = applications across all sites.</param>
    IReadOnlyList<IisApplicationInfo> EnumerateApplications(string? siteName = null);

    IisAppPoolInfo? GetAppPool(string appPoolName);

    void StopSite(string siteName);

    void StartSite(string siteName);

    void StopAppPool(string appPoolName);

    void StartAppPool(string appPoolName);

    void RecycleAppPool(string appPoolName);
}
