namespace DeployToolkit.AppKit;

/// <summary>
/// One of the Packager shell's MDI screens (plan §10 / §19). The shell hosts
/// these as in-app child windows; this enum lets the switching rules below be
/// expressed WITHOUT any WinForms type, so they compile into the pure
/// net8.0 asset and are headless-testable (see DeployToolkit.AppKit.SelfTest).
/// </summary>
public enum ShellScreen
{
    /// <summary>The plan §10 "New Package…" wizard (stateful: a package draft).</summary>
    Wizard,

    /// <summary>The §19 client &amp; package management screen.</summary>
    Clients,

    /// <summary>The "Reconcile Offline Results…" screen (plan §9/§10).</summary>
    Reconcile,

    /// <summary>The registry connection screen (view/edit settings).</summary>
    Connection,
}

/// <summary>
/// Implemented by MDI child screens that can hold unsaved work, so the shell
/// can ask ONCE — with a single combined prompt — before closing them during a
/// screen switch, a "Close All Screens" or a connection change. Consent is
/// always gathered by the shell; <see cref="CloseWithoutPrompt"/> then closes
/// without re-prompting.
/// </summary>
public interface IGuardedCloseScreen
{
    /// <summary>True when closing this screen now would lose unsaved work.</summary>
    bool HasUnsavedWork { get; }

    /// <summary>Short human description for the combined confirm prompt,
    /// e.g. "the package wizard is in progress (step 4 of 7)".</summary>
    string UnsavedWorkDescription { get; }

    /// <summary>Closes the screen without prompting — the caller has already
    /// obtained consent. Equivalent to a plain close for screens without
    /// unsaved work.</summary>
    void CloseWithoutPrompt();
}

/// <summary>The shell's plan after the user asked to open or close screens.
/// All lists use <see cref="ShellScreen"/> values (one instance per screen —
/// the shell keeps at most one of each).</summary>
/// <param name="Proceed">False when there is nothing to open — the shell
/// should just activate <paramref name="ActivateInstead"/>.</param>
/// <param name="ActivateInstead">Open instance to focus; null when a new
/// screen should be opened.</param>
/// <param name="ScreensToClose">Open screens that must CLOSE so the requested
/// screen can become the single front screen.</param>
/// <param name="GuardedScreensToClose">Subset of
/// <paramref name="ScreensToClose"/> holding unsaved work — the shell must
/// confirm with the user BEFORE closing these (decline → activate that screen
/// instead and abandon the switch).</param>
public sealed record ScreenSwitchDecision(
    bool Proceed,
    ShellScreen? ActivateInstead,
    IReadOnlyList<ShellScreen> ScreensToClose,
    IReadOnlyList<ShellScreen> GuardedScreensToClose)
{
    /// <summary>True when the shell must ask before closing (any guarded
    /// screen is about to be closed).</summary>
    public bool ConfirmationRequired => GuardedScreensToClose.Count > 0;
}

/// <summary>
/// PURE decision logic for the Packager MDI shell's screen-switching policy —
/// the rules that fix the reported pile-up ("opening another form never closed
/// the previous one, so at close time I had to close them one by one"):
///
/// <list type="bullet">
/// <item><b>One front screen.</b> Opening a screen closes the other STATELESS
/// screens (Clients / Reconcile / Connection — they re-query the registry on
/// open, so switching is lossless and never prompts).</item>
/// <item><b>The wizard is pinned, not discarded.</b> Opening another screen
/// while a package wizard is in progress KEEPS the wizard (the §10 flow sends
/// the user to the Clients screen mid-wizard — e.g. to resolve stale packages
/// — and switching must not destroy the session). It stays reachable via the
/// shell's Window menu (MDI list) and prompts exactly once when it is finally
/// closed, switched away by a connection change, or replaced.</item>
/// <item><b>One wizard at a time.</b> "New Package…" replaces an existing
/// wizard (silently when it has no progress, with consent when it does) —
/// wizards never accumulate.</item>
/// <item><b>Re-open = focus.</b> Opening a screen that is already open just
/// activates it; nothing closes, nothing duplicates.</item>
/// </list>
///
/// The shell maps these rules onto live Form instances; this class owns none.
/// </summary>
public static class ShellScreenPolicy
{
    /// <summary>Friendly name used in prompts ("the Clients screen…").</summary>
    public static string DisplayName(ShellScreen screen) => screen switch
    {
        ShellScreen.Wizard => "package wizard",
        ShellScreen.Clients => "Clients screen",
        ShellScreen.Reconcile => "offline-results screen",
        ShellScreen.Connection => "connection screen",
        _ => screen.ToString(),
    };

    /// <summary>Screens that need an open registry store to be usable — the
    /// shell disables their menu items while disconnected.</summary>
    public static bool IsRegistryBound(ShellScreen screen) =>
        screen is ShellScreen.Wizard or ShellScreen.Clients or ShellScreen.Reconcile;

    /// <summary>
    /// Decides what must happen when the user asks to open
    /// <paramref name="opening"/> while <paramref name="open"/> screens are
    /// already open (at most one instance of each) and
    /// <paramref name="guarded"/> names the open screens currently holding
    /// unsaved work.
    /// </summary>
    public static ScreenSwitchDecision PlanScreenOpen(
        ShellScreen opening,
        IReadOnlyList<ShellScreen> open,
        IReadOnlyList<ShellScreen> guarded)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(guarded);

        // Rule 4: the requested screen is already open → focus it, touch nothing.
        if (opening != ShellScreen.Wizard && open.Contains(opening))
            return Decline(activate: opening);

        if (opening == ShellScreen.Wizard)
        {
            // Rule 3: "New Package…" replaces an existing wizard — with
            // consent when it holds a draft (guarded), silently when fresh.
            // Every other open screen also makes way for the new front screen.
            var toClose = open.ToList(); // includes the wizard itself when open
            return new ScreenSwitchDecision(
                Proceed: true,
                ActivateInstead: null,
                ScreensToClose: toClose,
                GuardedScreensToClose: Intersect(toClose, guarded));
        }

        // Rules 1 + 2: a stateless screen becomes the front screen — the other
        // stateless screens close (lossless), the wizard stays put.
        var closable = open.Where(s => s != opening && s != ShellScreen.Wizard).ToList();
        return new ScreenSwitchDecision(
            Proceed: true,
            ActivateInstead: null,
            ScreensToClose: closable,
            GuardedScreensToClose: Intersect(closable, guarded));
    }

    /// <summary>
    /// Decides the "Close All Screens" action: everything open closes;
    /// consent is required for the guarded subset (one combined prompt).
    /// </summary>
    public static ScreenSwitchDecision PlanCloseAll(
        IReadOnlyList<ShellScreen> open,
        IReadOnlyList<ShellScreen> guarded)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(guarded);

        return new ScreenSwitchDecision(
            Proceed: true,
            ActivateInstead: null,
            ScreensToClose: open.ToList(),
            GuardedScreensToClose: Intersect(open, guarded));
    }

    private static ScreenSwitchDecision Decline(ShellScreen activate) =>
        new(false, activate, Array.Empty<ShellScreen>(), Array.Empty<ShellScreen>());

    private static IReadOnlyList<ShellScreen> Intersect(
        IReadOnlyList<ShellScreen> screens, IReadOnlyList<ShellScreen> guarded) =>
        screens.Where(guarded.Contains).ToList();
}
