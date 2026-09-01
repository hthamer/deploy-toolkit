using System.Runtime.Versioning;
using DeployToolkit.Core.IisControl;
using ObjectState = Microsoft.Web.Administration.ObjectState;

namespace DeployToolkit.Core.Windows;

/// <summary>
/// The real <see cref="IIisController"/> — a thin, defensive wrapper over
/// Microsoft.Web.Administration (plan §8.3: library API, never
/// PowerShell/Restart-WebAppPool). This is the ONLY file in the solution
/// that touches the IIS COM-backed config store.
///
/// Requires: Windows with IIS installed, and the running account must have
/// read (enumerate) or read/write (stop/start/recycle) access to the IIS
/// configuration — plan §16's permission-level matrix. Operators without
/// management rights use the app_offline.htm path
/// (<see cref="IisSiteStopController"/> with AppOffline strategy), which
/// never comes through here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrosoftWebAdministrationController : IIisController
{
    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "IIS control requires Windows with IIS installed (Microsoft.Web.Administration). " +
                "Self-tests and the Packager machine don't need this — only the Deployer on a target server does.");
    }

    public IReadOnlyList<IisSiteInfo> EnumerateSites()
    {
        EnsureWindows();
        using var manager = new Microsoft.Web.Administration.ServerManager();
        var sites = new List<IisSiteInfo>();
        foreach (var site in manager.Sites)
        {
            var rootApp = site.Applications.FirstOrDefault(a => a.Path == "/");
            var appPool = rootApp?.ApplicationPoolName ?? site.Applications.FirstOrDefault()?.ApplicationPoolName;
            var physicalPath = rootApp?.VirtualDirectories.Count > 0
                ? Environment.ExpandEnvironmentVariables(rootApp.VirtualDirectories[0].PhysicalPath)
                : string.Empty;
            sites.Add(new IisSiteInfo(site.Name, appPool, physicalPath, IsStarted(site.State)));
        }
        return sites;
    }

    public IReadOnlyList<IisApplicationInfo> EnumerateApplications(string? siteName = null)
    {
        EnsureWindows();
        using var manager = new Microsoft.Web.Administration.ServerManager();
        var apps = new List<IisApplicationInfo>();
        foreach (var site in manager.Sites)
        {
            if (!string.IsNullOrEmpty(siteName) &&
                !string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var app in site.Applications)
            {
                var rootVdir = app.VirtualDirectories.Count > 0 ? app.VirtualDirectories[0] : null;
                apps.Add(new IisApplicationInfo(
                    site.Name,
                    app.Path,
                    app.ApplicationPoolName,
                    rootVdir is null ? string.Empty : Environment.ExpandEnvironmentVariables(rootVdir.PhysicalPath)));
            }
        }
        return apps;
    }

    public IisAppPoolInfo? GetAppPool(string appPoolName)
    {
        EnsureWindows();
        using var manager = new Microsoft.Web.Administration.ServerManager();
        var pool = manager.ApplicationPools.FirstOrDefault(p =>
            string.Equals(p.Name, appPoolName, StringComparison.OrdinalIgnoreCase));
        if (pool is null) return null;
        return new IisAppPoolInfo(pool.Name, pool.ManagedRuntimeVersion, IsStarted(pool.State));
    }

    public void StopSite(string siteName)
    {
        EnsureWindows();
        using var manager = new Microsoft.Web.Administration.ServerManager();
        var site = RequireSite(manager, siteName);
        if (site.State == ObjectState.Stopped) return;
        var state = site.Stop();
        if (state == ObjectState.Stopping) WaitFor(() => site.State == ObjectState.Stopped);
    }

    public void StartSite(string siteName)
    {
        EnsureWindows();
        using var manager = new Microsoft.Web.Administration.ServerManager();
        var site = RequireSite(manager, siteName);
        if (site.State == ObjectState.Started) return;
        var state = site.Start();
        if (state == ObjectState.Starting) WaitFor(() => site.State == ObjectState.Started);
    }

    public void StopAppPool(string appPoolName)
    {
        EnsureWindows();
        using var manager = new Microsoft.Web.Administration.ServerManager();
        var pool = RequireAppPool(manager, appPoolName);
        if (pool.State == ObjectState.Stopped) return;
        var state = pool.Stop();
        if (state == ObjectState.Stopping) WaitFor(() => pool.State == ObjectState.Stopped);
    }

    public void StartAppPool(string appPoolName)
    {
        EnsureWindows();
        using var manager = new Microsoft.Web.Administration.ServerManager();
        var pool = RequireAppPool(manager, appPoolName);
        if (pool.State == ObjectState.Started) return;
        var state = pool.Start();
        if (state == ObjectState.Starting) WaitFor(() => pool.State == ObjectState.Started);
    }

    public void RecycleAppPool(string appPoolName)
    {
        EnsureWindows();
        using var manager = new Microsoft.Web.Administration.ServerManager();
        var pool = RequireAppPool(manager, appPoolName);
        // Recycle returns immediately with Recycling state; the pool comes
        // back Started on its own — do NOT wait for Stopped here.
        pool.Recycle();
        WaitFor(() => pool.State == ObjectState.Started || pool.State == ObjectState.Starting, 60);
    }

    // ---------------------------------------------------------------

    private static Microsoft.Web.Administration.Site RequireSite(
        Microsoft.Web.Administration.ServerManager manager, string siteName)
    {
        var site = manager.Sites.FirstOrDefault(s => string.Equals(s.Name, siteName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"IIS site '{siteName}' was not found on this server.");
        return site;
    }

    private static Microsoft.Web.Administration.ApplicationPool RequireAppPool(
        Microsoft.Web.Administration.ServerManager manager, string appPoolName)
    {
        var pool = manager.ApplicationPools.FirstOrDefault(p =>
                string.Equals(p.Name, appPoolName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"IIS application pool '{appPoolName}' was not found on this server.");
        return pool;
    }

    private static bool IsStarted(ObjectState state) =>
        state is ObjectState.Started or ObjectState.Starting;

    /// <summary>Microsoft.Web.Administration's state transitions are
    /// asynchronous under the hood (Stop() may return Stopping) — poll with
    /// a bounded wait so callers get deterministic post-conditions.</summary>
    private static void WaitFor(Func<bool> condition, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(250);
        }
        // Time-out is a soft failure: the next operation will surface the
        // real state. Deliberately no exception — partial progress beats a
        // hard failure while IIS finishes a transition.
    }
}
