using DeployToolkit.AppKit;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Packager;

/// <summary>
/// The Packager shell (plan §10): an MDI container whose children are the
/// main screens — the packaging wizard, the Clients screen, offline-result
/// reconciliation and the registry connection form (Office-style: the
/// screens open INSIDE the main window instead of stacked dialogs).
///
/// Screen management follows the single-front-screen policy in
/// <see cref="ShellScreenPolicy"/> (fixes the reported MDI pile-up, where
/// every "New Package…"/"Clients…"/… opened ANOTHER child and closing the
/// app meant closing them one by one):
///  - opening a screen makes it the ONE front screen: stateless screens
///    (Clients / Reconcile / Connection) are replaced, never stacked;
///  - the package wizard is pinned while in progress (the §10 flow itself
///    sends the user to the Clients screen mid-wizard) and prompts exactly
///    once when it is finally closed or replaced — one wizard at a time;
///  - a Window menu (MDI list + "Close All Screens") keeps every open child
///    visible and reachable — nothing hides behind a maximized sibling;
///  - the shell menu disables while any child is busy or under a modal
///    dialog, so screens can never be switched or closed underneath a
///    running operation;
///  - closing the shell cascades to the children; at most ONE prompt (the
///    wizard's draft guard) can appear — never one per accumulated form.
///
/// Startup never crashes on a missing/unreachable registry: settings are
/// loaded tolerantly, the first connect attempt runs under a friendly
/// try/connect, and when no store could be opened the wizard/Clients/
/// reconcile actions stay disabled with a hint in the status strip until
/// the user configures a working connection.
/// </summary>
public sealed class MainForm : Form
{
    private RegistryConnectionSettings _settings = new();
    private IRegistryStore? _store;
    private PackageBuilder? _builder;
    private ILocalProjectMappingStore _mappingStore;
    private string? _connectionError;
    // Option B: the shared package store (a network folder) the builder uploads
    // delta.zips to so a Deployer on another machine can fetch them. Null when
    // the user hasn't configured a PackageStoreRootPath (local-only behavior).
    private DeployToolkit.Core.Packaging.IPackageStore? _packageStore;

    private ToolStripMenuItem _newPackageItem = null!;
    private ToolStripMenuItem _clientsItem = null!;
    private ToolStripMenuItem _reconcileItem = null!;
    private ToolStripMenuItem _connectionItem = null!;
    private ToolStripMenuItem _closeAllItem = null!;
    private ToolStripMenuItem _windowMenu = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripStatusLabel _windowsLabel = null!;

    // Single-instance MDI children — the shell keeps at most one of each
    // (see ShellScreenPolicy); the fields are cleared by the FormClosed hook.
    private PackagerWizardForm? _wizardScreen;
    private ClientsScreen? _clientsScreen;
    private ReconcileDialog? _reconcileScreen;
    private ConnectionDialog? _connectionScreen;

    public MainForm()
    {
        Text = "DeployToolkit Packager";
        AppTheme.Apply(this, primaryWindow: true);
        StartPosition = FormStartPosition.CenterScreen;
        // 1040x760 fits the largest child (ClientsScreen, 1000x640 minimum)
        // even maximized INSIDE the MDI client area at the shell's own
        // minimum — a maximized child larger than the client area gets its
        // right/bottom edges clipped off with no way to scroll to them.
        Size = new Size(1160, 780);
        MinimumSize = new Size(1040, 760);
        IsMdiContainer = true; // the screens open as in-app child windows

        _mappingStore = CreateDefaultMappingStore();

        BuildMenu();
        BuildStatusStrip();
        MdiChildActivate += OnMdiChildActivate;

        // Guarded operations no longer disable an MDI child FORM (only its
        // content — see BusyDialog), so no EnabledChanged fires for a busy
        // child: refresh the busy gating from Guard's own notification.
        Guard.BusyStateChanged += OnGuardBusyStateChanged;
        FormClosed += (_, _) => Guard.BusyStateChanged -= OnGuardBusyStateChanged;
    }

    private void OnGuardBusyStateChanged()
    {
        if (!IsDisposed)
            UpdateScreenMenuState();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = ConnectOnStartupAsync();
    }

    // ---------------------------------------------------------------
    // UI construction

    private void BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };

        _newPackageItem = new ToolStripMenuItem("New Package…")
        {
            Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold), // the primary action
            ShortcutKeys = Keys.Control | Keys.N,
            Enabled = false,
        };
        _newPackageItem.Click += (_, _) => TrySwitchToScreen(ShellScreen.Wizard);

        _clientsItem = new ToolStripMenuItem("Clients…") { Enabled = false };
        _clientsItem.Click += (_, _) => TrySwitchToScreen(ShellScreen.Clients);

        _reconcileItem = new ToolStripMenuItem("Reconcile Offline Results…") { Enabled = false };
        _reconcileItem.Click += (_, _) => TrySwitchToScreen(ShellScreen.Reconcile);

        _connectionItem = new ToolStripMenuItem("Connection…");
        _connectionItem.Click += (_, _) => TrySwitchToScreen(ShellScreen.Connection);

        // Window menu: every open child is listed (active one checked) so
        // nothing hides behind a maximized sibling — plus one-click "Close All
        // Screens" so closing never means closing the pile one form at a time.
        // (MenuStrip has no built-in MDI list — MenuStrip is not the old
        // MainMenu/MdiList API — so the list is maintained by
        // RefreshWindowMenu on every open/close/activate.)
        _closeAllItem = new ToolStripMenuItem("Close All Screens")
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.W,
            Enabled = false,
        };
        _closeAllItem.Click += (_, _) => CloseAllScreens();
        _windowMenu = new ToolStripMenuItem("&Window")
        {
            Alignment = ToolStripItemAlignment.Right,
        };
        _windowMenu.DropDownOpening += (_, _) => RefreshWindowMenu();

        menu.Items.Add(_newPackageItem);
        menu.Items.Add(_clientsItem);
        menu.Items.Add(_reconcileItem);
        menu.Items.Add(_connectionItem);
        menu.Items.Add(_windowMenu);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void BuildStatusStrip()
    {
        var strip = new StatusStrip { Dock = DockStyle.Bottom };
        _statusLabel = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        };
        // Right-hand cell: how many screens are open + which one is active —
        // makes the otherwise-invisible maximized siblings countable at a
        // glance (paired with the Window menu).
        _windowsLabel = new ToolStripStatusLabel
        {
            Text = "No screen open",
            Alignment = ToolStripItemAlignment.Right,
            BorderSides = ToolStripStatusLabelBorderSides.Left,
        };
        strip.Items.Add(_statusLabel);
        strip.Items.Add(_windowsLabel);
        Controls.Add(strip);
    }

    // ---------------------------------------------------------------
    // Connection lifecycle

    private async Task ConnectOnStartupAsync()
    {
        _settings = RegistryConnectionSettings.Load(RegistryConnectionSettings.DefaultSettingsPath);

        // First run in offline mode: pre-fill a sensible default root instead
        // of making the user pick a folder before anything works.
        if (_settings.Mode == RegistryMode.LocalFile && string.IsNullOrWhiteSpace(_settings.LocalRoot))
            _settings.LocalRoot = DefaultOfflineRegistryRoot();

        var connected = await TryConnectAsync(_settings);
        if (!connected)
        {
            // First-run / unreachable registry: open the in-app connection
            // screen right away; the main window (with its disabled actions)
            // stays usable behind it — startup must never fail or block.
            TrySwitchToScreen(ShellScreen.Connection);
        }

        UpdateConnectionUi();
    }

    /// <summary>Applies connection settings chosen in the connection screen:
    /// persists them, reconnects (busy-guarded — NO dialogs inside the
    /// guard), and closes the open children after a successful reconnect —
    /// they hold the previous store and would otherwise read/write a dead
    /// connection. Unsaved work in those children is consented to FIRST;
    /// declining keeps the connection screen open with the typed settings
    /// intact (returns false).</summary>
    private bool ApplyConnectionSettings(RegistryConnectionSettings chosen)
    {
        // Consent before ANYTHING happens: a successful reconnect force-closes
        // every child, guarded ones included.
        var guardedForms = GuardedForms();
        if (guardedForms.Count > 0)
        {
            var what = string.Join("; ", guardedForms.Select(g => g.UnsavedWorkDescription));
            if (AppTheme.Confirm(this,
                    "Applying the new connection closes every open screen after reconnecting:\n\n" +
                    $"{what} — that work would be lost.\n\nApply the new connection anyway?",
                    "Change registry connection") != DialogResult.Yes)
                return false;
        }

        _settings = chosen;
        _settings.Save(RegistryConnectionSettings.DefaultSettingsPath);

        Guard.FireAndForget(this, "Reconnecting…", async () =>
        {
            if (await TryConnectAsync(_settings))
                CloseRegistryChildren();
            UpdateConnectionUi();
        });
        return true;
    }

    /// <summary>
    /// Builds and opens the store for <paramref name="settings"/>. Never
    /// throws: returns false and keeps the friendly error for the status
    /// strip. The previous store is disposed when its type is disposable.
    /// </summary>
    private async Task<bool> TryConnectAsync(RegistryConnectionSettings settings)
    {
        try
        {
            RegistryConnectionFactory.Validate(settings);
            var store = await RegistryConnectionFactory.CreateOpenAsync(settings);

            _connectionError = null;
            DisposeStore();
            _store = store;
            // Build the package store (Option B) from the configured root.
            // Null when PackageStoreRootPath is empty → local-only behavior
            // (the .zip lives only on this PC; the Deployer needs a manual copy).
            _packageStore = TryBuildPackageStore(settings);
            _builder = new PackageBuilder(_store, _mappingStore, _packageStore);
            return true;
        }
        catch (ArgumentException ex)
        {
            // Incomplete settings (e.g. first run with nothing configured):
            // not an infrastructure failure — no scary error dialog, the
            // connection dialog handles it.
            _connectionError = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _connectionError = DescribeException(ex);
            return false;
        }
    }

    private static string DescribeException(Exception ex) =>
        ex is ArgumentException or InvalidOperationException
            ? ex.Message
            : $"{ex.GetType().Name}: {ex.Message}";

    private void DisposeStore()
    {
        if (_store is IDisposable disposable)
            disposable.Dispose();
        _store = null;
        _builder = null;
        _packageStore = null;
    }

    /// <summary>Builds the <see cref="DeployToolkit.Core.Packaging.IPackageStore"/>
    /// from the configured <see cref="RegistryConnectionSettings.PackageStoreRootPath"/>.
    /// Returns null when the root is empty (local-only behavior). The store
    /// is created eagerly — a bad/empty path throws here so the connection
    /// flow surfaces it (the user typed a non-empty path that can't be a
    /// store root), but a valid path that's unreachable at upload time is
    /// reported then, not now (the share may be down momentarily).</summary>
    private static DeployToolkit.Core.Packaging.IPackageStore? TryBuildPackageStore(RegistryConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PackageStoreRootPath))
            return null;
        return new DeployToolkit.Core.Packaging.FileSystemPackageStore(settings.PackageStoreRootPath!);
    }

    private void UpdateConnectionUi()
    {
        var connected = _store is not null;

        if (connected && _settings.Mode == RegistryMode.SqlServer)
        {
            _statusLabel.Text = $"SQL Server: {SqlServerPart(_settings.ConnectionString!)}";
            _statusLabel.ForeColor = System.Drawing.Color.Black;
        }
        else if (connected)
        {
            _statusLabel.Text = $"Offline files: {_settings.LocalRoot}";
            _statusLabel.ForeColor = System.Drawing.Color.Black;
        }
        else
        {
            _statusLabel.Text = "No registry connection — 'New Package…' is disabled. " +
                                "Open Connection… to configure one."
                                + (string.IsNullOrEmpty(_connectionError) ? "" : $" ({_connectionError})");
            _statusLabel.ForeColor = System.Drawing.Color.Firebrick;
        }

        UpdateScreenMenuState();
    }

    /// <summary>Menu state: registry-bound screens need a store; EVERY screen
    /// action is blocked while any child is blocked (busy under
    /// <see cref="Guard"/> — including its hidden pre-dialog window — or under
    /// a modal dialog) so screens can never be switched or closed underneath
    /// a running operation.</summary>
    private void UpdateScreenMenuState()
    {
        var anyBusy = MdiChildren.Any(IsChildBlocked);
        var connected = _store is not null;

        _newPackageItem.Enabled = connected && !anyBusy;
        _clientsItem.Enabled = connected && !anyBusy;
        _reconcileItem.Enabled = connected && !anyBusy;
        _connectionItem.Enabled = !anyBusy;
        _closeAllItem.Enabled = !anyBusy && MdiChildren.Length > 0;
    }

    /// <summary>True when a child must block screen switching: it is
    /// DISABLED (a modal dialog's loop disables its owner form) or it has a
    /// live <see cref="Guard"/> operation. The Guard check matters since the
    /// busy dialog no longer disables MDI-child forms — only their content —
    /// so MDI activation never jumps to another screen while work runs
    /// (the reported "wizard replaces the Clients screen" bug).</summary>
    private static bool IsChildBlocked(Form child) =>
        !child.Enabled || Guard.IsBusy(child);

    // ---------------------------------------------------------------
    // MDI children — single-front-screen management (ShellScreenPolicy)

    /// <summary>Screen switch entry point: asks the policy what must close,
    /// collects consent for guarded screens with ONE combined prompt, closes,
    /// then opens/activates the requested screen.</summary>
    private void TrySwitchToScreen(ShellScreen target)
    {
        if (target != ShellScreen.Connection && _store is null)
            return; // registry-bound screens need a store

        // Belt & braces (the menu is already gated): never switch underneath
        // a busy/modal child.
        if (MdiChildren.Any(IsChildBlocked))
            return;

        var decision = ShellScreenPolicy.PlanScreenOpen(target, OpenScreens(), GuardedScreens());

        if (!decision.Proceed)
        {
            if (decision.ActivateInstead is { } activate)
                ActivateScreen(activate);
            return;
        }

        if (decision.ConfirmationRequired && !ConfirmCloseGuarded(decision.GuardedScreensToClose, target))
        {
            // Declined — put the guarded screen (usually the in-progress
            // wizard) in front instead of switching.
            ActivateScreen(decision.GuardedScreensToClose[0]);
            return;
        }

        CloseScreens(decision.ScreensToClose, decision.GuardedScreensToClose);
        CreateScreen(target);
    }

    /// <summary>"Window ▸ Close All Screens": one combined consent (when
    /// guarded work exists) then every child closes — the fix for closing a
    /// pile of windows one by one.</summary>
    private void CloseAllScreens()
    {
        if (MdiChildren.Length == 0)
            return;
        if (MdiChildren.Any(IsChildBlocked))
            return; // busy — the menu is normally disabled already

        var open = OpenScreens();
        var guarded = GuardedScreens();
        var decision = ShellScreenPolicy.PlanCloseAll(open, guarded);

        if (decision.ConfirmationRequired)
        {
            var what = string.Join("; ", GuardedForms().Select(g => g.UnsavedWorkDescription));
            if (AppTheme.Confirm(this,
                    $"Close every open screen?\n\n{what} — that work would be lost.",
                    "Close all screens") != DialogResult.Yes)
                return;
        }

        CloseScreens(decision.ScreensToClose, decision.GuardedScreensToClose);
    }

    /// <summary>ONE prompt listing everything that would be lost. Returns
    /// true when the user consented (or nothing was guarded).</summary>
    private bool ConfirmCloseGuarded(IReadOnlyList<ShellScreen> guardedScreens, ShellScreen target)
    {
        var descriptions = guardedScreens
            .Select(screen => ScreenForm(screen) as IGuardedCloseScreen)
            .Where(g => g is not null)
            .Select(g => g!.UnsavedWorkDescription)
            .ToList();
        if (descriptions.Count == 0)
            return true; // stale decision — nothing guarded anymore

        var action = target == ShellScreen.Wizard
            ? "start a new package wizard"
            : $"open the {ShellScreenPolicy.DisplayName(target)}";
        var what = string.Join("; ", descriptions);

        return AppTheme.Confirm(this,
            $"You are about to {action}.\n\n{what} — that work would be lost.\n\nContinue?",
            "Close open screens") == DialogResult.Yes;
    }

    private void CloseScreens(IEnumerable<ShellScreen> screens, IReadOnlyList<ShellScreen> guarded)
    {
        foreach (var screen in screens)
        {
            if (ScreenForm(screen) is not { IsDisposed: false } form)
                continue;

            if (guarded.Contains(screen) && form is IGuardedCloseScreen guardedForm)
                guardedForm.CloseWithoutPrompt(); // consent was collected above
            else
                form.Close(); // stateless (or unguarded) — closes without prompting
        }
    }

    /// <summary>Creates and shows the requested screen as the new front MDI
    /// child.</summary>
    private void CreateScreen(ShellScreen target)
    {
        Form screen;
        switch (target)
        {
            case ShellScreen.Wizard:
            {
                if (_store is null || _builder is null)
                    return;
                var wizard = new PackagerWizardForm(_store, _builder);
                _wizardScreen = wizard;
                screen = wizard;
                break;
            }
            case ShellScreen.Clients:
            {
                if (_store is null)
                    return;
                var clients = new ClientsScreen(_store);
                _clientsScreen = clients;
                screen = clients;
                break;
            }
            case ShellScreen.Reconcile:
            {
                if (_store is null)
                    return;
                var reconcile = ReconcileDialog.Create(_store);
                _reconcileScreen = reconcile;
                screen = reconcile;
                break;
            }
            case ShellScreen.Connection:
            {
                var connection = ConnectionDialog.CreateEmbedded(_settings, ApplyConnectionSettings);
                _connectionScreen = connection;
                screen = connection;
                break;
            }
            default:
                return;
        }

        RegisterChild(screen);
        ShowChild(screen);
        UpdateWindowStatus();
        UpdateScreenMenuState();
    }

    /// <summary>Shows a screen as a maximized MDI child — one app window,
    /// one visible screen (Office-style).</summary>
    private static void ShowChild(Form screen)
    {
        screen.WindowState = FormWindowState.Maximized;
        screen.Show();
    }

    /// <summary>Hooks a freshly created child: clears its field when it
    /// closes (any path — X, switch, close-all, connection change), mirrors
    /// its busy/modal state onto the shell menu, and restores it to the
    /// front after a blocking state ends (insurance against the classic MDI
    /// quirk where disabling/re-enabling a child moves MDI activation to a
    /// sibling and never back).</summary>
    private void RegisterChild(Form screen)
    {
        screen.MdiParent = this;
        screen.FormClosed += (_, _) =>
        {
            ForgetScreen(screen);
            UpdateWindowStatus();
            UpdateScreenMenuState();
        };
        var wasActiveWhenBlocked = false;
        screen.EnabledChanged += (_, _) =>
        {
            if (screen.Enabled)
            {
                if (wasActiveWhenBlocked && !screen.IsDisposed
                    && !ReferenceEquals(ActiveMdiChild, screen))
                {
                    screen.Activate(); // bring the front screen back
                }
                wasActiveWhenBlocked = false;
            }
            else
            {
                wasActiveWhenBlocked = ReferenceEquals(ActiveMdiChild, screen);
            }
            UpdateScreenMenuState();
        };
    }

    private void ForgetScreen(Form screen)
    {
        if (ReferenceEquals(_wizardScreen, screen))
            _wizardScreen = null;
        if (ReferenceEquals(_clientsScreen, screen))
            _clientsScreen = null;
        if (ReferenceEquals(_reconcileScreen, screen))
            _reconcileScreen = null;
        if (ReferenceEquals(_connectionScreen, screen))
            _connectionScreen = null;
    }

    private void ActivateScreen(ShellScreen screen)
    {
        if (ScreenForm(screen) is { IsDisposed: false } form)
            form.Activate();
    }

    private Form? ScreenForm(ShellScreen screen) => screen switch
    {
        ShellScreen.Wizard => _wizardScreen,
        ShellScreen.Clients => _clientsScreen,
        ShellScreen.Reconcile => _reconcileScreen,
        ShellScreen.Connection => _connectionScreen,
        _ => null,
    };

    private IReadOnlyList<ShellScreen> OpenScreens()
    {
        var screens = new List<ShellScreen>(4);
        if (_wizardScreen is { IsDisposed: false })
            screens.Add(ShellScreen.Wizard);
        if (_clientsScreen is { IsDisposed: false })
            screens.Add(ShellScreen.Clients);
        if (_reconcileScreen is { IsDisposed: false })
            screens.Add(ShellScreen.Reconcile);
        if (_connectionScreen is { IsDisposed: false })
            screens.Add(ShellScreen.Connection);
        return screens;
    }

    /// <summary>Open screens that currently hold unsaved work (ask the live
    /// forms — they own the truth, e.g. the wizard's draft-progress rule).</summary>
    private IReadOnlyList<ShellScreen> GuardedScreens() =>
        OpenScreens()
            .Where(screen => ScreenForm(screen) is IGuardedCloseScreen { HasUnsavedWork: true })
            .ToList();

    private List<IGuardedCloseScreen> GuardedForms() => MdiChildren
        .OfType<IGuardedCloseScreen>()
        .Where(child => child.HasUnsavedWork)
        .ToList();

    /// <summary>Classic MDI quirk fix: when the active (maximized) child
    /// closes, Windows activates the next child in NORMAL state — re-enforce
    /// the one-front-screen policy, then refresh the window status cell.</summary>
    private void OnMdiChildActivate(object? sender, EventArgs e)
    {
        if (ActiveMdiChild is { } active && active.WindowState != FormWindowState.Maximized)
            active.WindowState = FormWindowState.Maximized;
        UpdateWindowStatus();
    }

    private void UpdateWindowStatus()
    {
        var children = MdiChildren;
        if (children.Length == 0)
        {
            _windowsLabel.Text = "No screen open";
            return;
        }

        var activeTitle = ActiveMdiChild is { } active ? TrimTitle(active.Text) : string.Empty;
        _windowsLabel.Text = children.Length == 1
            ? $"1 screen — {activeTitle}"
            : $"{children.Length} screens — active: {activeTitle}  (Window menu to switch)";
    }

    /// <summary>Rebuilds the Window menu's MDI child list ("1 …", "2 …",
    /// active one checked). MenuStrip has no built-in MDI list, so this runs
    /// right before the dropdown opens — always current, even after closes.
    /// The &amp;digit prefixes give Alt+W, 1..9 keyboard switching.</summary>
    private void RefreshWindowMenu()
    {
        _windowMenu.DropDownItems.Clear();
        _windowMenu.DropDownItems.Add(_closeAllItem);

        var children = MdiChildren;
        if (children.Length == 0)
        {
            _windowMenu.DropDownItems.Add(new ToolStripSeparator());
            _windowMenu.DropDownItems.Add(new ToolStripMenuItem("(no screen open)") { Enabled = false });
            return;
        }

        for (var i = 0; i < children.Length; i++)
        {
            var child = children[i];
            var index = i < 9 ? i + 1 : 0; // Alt+W + 1..9 mnemonics for the first nine
            var text = (index > 0 ? $"&{index} " : "   ") + TrimTitle(child.Text);
            var item = new ToolStripMenuItem(text)
            {
                Checked = ReferenceEquals(child, ActiveMdiChild),
            };
            item.Click += (_, _) =>
            {
                if (child is { IsDisposed: false })
                    child.Activate();
            };
            _windowMenu.DropDownItems.Add(item);
        }
    }

    private static string TrimTitle(string title) =>
        title.Length <= 60 ? title : title[..57] + "…";

    /// <summary>Closes every MDI child (after a connection change — the
    /// children hold the previous registry store). Consent for guarded work
    /// was collected by <see cref="ApplyConnectionSettings"/> BEFORE the
    /// reconnect, so guarded children are closed without prompting.</summary>
    private void CloseRegistryChildren()
    {
        foreach (var child in MdiChildren)
        {
            if (child is IGuardedCloseScreen guarded)
                guarded.CloseWithoutPrompt();
            else
                child.Close();
        }
    }

    // ---------------------------------------------------------------
    // Helpers

    /// <summary>Documents\DeployToolkit\OfflineRegistry (with a current-dir
    /// fallback for hosts without a Documents folder).</summary>
    internal static string DefaultOfflineRegistryRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.CurrentDirectory;
        return Path.Combine(documents, "DeployToolkit", "OfflineRegistry");
    }

    private static ILocalProjectMappingStore CreateDefaultMappingStore()
    {
        // Folder→component mappings are machine-local (plan §5) — keep them
        // next to the connection settings under %APPDATA%\DeployToolkit.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.CurrentDirectory;
        return new JsonFileProjectMappingStore(
            Path.Combine(appData, "DeployToolkit", "packager-folder-mappings.json"));
    }

    /// <summary>Extracts the server (data source) part of a SQL connection
    /// string for the status strip; falls back to a generic hint.</summary>
    private static string SqlServerPart(string connectionString)
    {
        foreach (var part in connectionString.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key.Equals("Server", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Address", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Addr", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Network Address", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return "(connection string)";
    }
}
