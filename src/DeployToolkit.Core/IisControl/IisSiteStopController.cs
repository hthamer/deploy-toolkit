namespace DeployToolkit.Core.IisControl;

/// <summary>How to stop/start the app during a deploy (plan §7 step 4 and
/// §11 step 5: "app-pool recycle or app_offline.htm, selectable based on
/// account permissions").</summary>
public enum IisStopStrategy
{
    /// <summary>Try the app pool first; fall back to app_offline.htm when
    /// IIS management rights are missing (the common non-admin RDP case).</summary>
    Auto,

    /// <summary>Stop/start via the application pool (requires IIS management
    /// rights — the account must be able to talk to the IIS config store).</summary>
    AppPool,

    /// <summary>Always use the app_offline.htm drop/remove — pure file I/O,
    /// works for filesystem-only accounts. Requires nothing but write
    /// access to the site root.</summary>
    AppOffline,
}

public sealed record IisStopOutcome(bool UsedAppOffline, string Description);

/// <summary>
/// Stop/start logic for one deploy target, encapsulating the plan's
/// "app pool preferred, app_offline.htm fallback" policy. The Deployer UI
/// calls <see cref="Stop"/> before file changes and <see cref="Start"/>
/// after; <see cref="Stop"/> records what it did so <see cref="Start"/>
/// reverses exactly that.
/// </summary>
public sealed class IisSiteStopController(
    IIisController controller,
    string? appPoolName,
    string siteRoot,
    IisStopStrategy strategy = IisStopStrategy.Auto)
{
    /// <summary>What the last <see cref="Stop"/> actually used — Start
    /// reverses this.</summary>
    public IisStopOutcome? LastStopOutcome { get; private set; }

    public IisStopOutcome Stop()
    {
        if (strategy == IisStopStrategy.AppOffline)
            return LastStopOutcome = UseAppOffline("configured strategy is AppOffline");

        if (string.IsNullOrWhiteSpace(appPoolName))
        {
            if (strategy == IisStopStrategy.AppPool)
                throw new InvalidOperationException(
                    "IisStopStrategy is AppPool but the component has no app pool configured.");
            return LastStopOutcome = UseAppOffline("no app pool configured for this component");
        }

        try
        {
            controller.StopAppPool(appPoolName);
            return LastStopOutcome = new IisStopOutcome(false, $"App pool '{appPoolName}' stopped.");
        }
        catch (Exception ex) when (strategy == IisStopStrategy.Auto)
        {
            // Most common cause: the account can't touch the IIS config
            // store (IIS-manager-only or filesystem-only permission level —
            // plan §16 testing matrix). The file-based fallback always works.
            return LastStopOutcome = UseAppOffline($"app pool stop failed ({ex.Message})");
        }
    }

    public IisStopOutcome Start()
    {
        var usedOffline = LastStopOutcome?.UsedAppOffline ?? false;

        if (usedOffline)
        {
            var removed = AppOfflineManager.Remove(siteRoot);
            return new IisStopOutcome(true,
                removed
                    ? "app_offline.htm removed — application is back online."
                    : "app_offline.htm was already gone (or could not be removed) — verify the site is serving again.");
        }

        if (string.IsNullOrWhiteSpace(appPoolName))
            return new IisStopOutcome(false, "No app pool configured — nothing to start.");

        controller.StartAppPool(appPoolName);
        return new IisStopOutcome(false, $"App pool '{appPoolName}' started.");
    }

    private IisStopOutcome UseAppOffline(string reason)
    {
        AppOfflineManager.Drop(siteRoot);
        return new IisStopOutcome(true, $"app_offline.htm dropped ({reason}).");
    }
}
