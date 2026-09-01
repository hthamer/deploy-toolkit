using DeployToolkit.AppKit;
using DeployToolkit.Core.Backup;
using DeployToolkit.Core.Registry;
using DeployToolkit.Deployer.Stages;

namespace DeployToolkit.Deployer;

/// <summary>
/// The §11 stage state machine: Empty (no package) → Loaded (package
/// verified + matched to a registry record) → Ready (target resolved +
/// pre-flight passed) → Running (deploy run) → Done (run succeeded; Finish
/// resets). A failed/cancelled run returns to Ready so the same package can
/// be re-deployed with one click.
/// </summary>
internal enum DeployerStage
{
    Empty,
    Loaded,
    Ready,
    Running,
    Done,
}

/// <summary>
/// The §11 deploy flow state machine: <see cref="DeployerStage.Empty"/> (no
/// package) → <see cref="DeployerStage.Loaded"/> (package verified + matched
/// to a registry record) → <see cref="DeployerStage.Ready"/> (target resolved
/// + pre-flight checks passed) → <see cref="DeployerStage.Running"/> (the
/// guarded deploy run) → <see cref="DeployerStage.Done"/> (run completed
/// successfully; "Finish" resets). A failed/cancelled run returns to Ready so
/// the same package can be re-deployed with one click after a fix.
///
/// Startup never crashes on a missing/unreachable registry: settings are
/// loaded tolerantly (own file: %APPDATA%\DeployToolkit\deployer-registry.json,
/// same pattern as the Packager shell) and when no store could be opened the
/// stage buttons stay disabled with a hint in the status strip until the user
/// configures a working connection.
/// </summary>
public sealed class MainForm : Form
{
    private RegistryConnectionSettings _settings = new();
    private IRegistryStore? _store;
    private string? _connectionError;

    private DeploymentContext? _context;
    private DeployerStage _stage = DeployerStage.Empty;
    private string? _lastRunLogPath;

    private readonly StageLoadPackage _loadStage;
    private readonly StageResolveTarget _resolveStage;
    private readonly StagePreflight _preflightStage;
    private readonly StageBackup _backupStage;
    private readonly StageDeploy _deployStage;
    private StagePanel _currentStage = null!;

    private ToolStripMenuItem _loadPackageItem = null!;
    private ToolStripMenuItem _rollbackItem = null!;
    private ToolStripMenuItem _viewLogItem = null!;
    private Label _packageSummary = null!;
    private LogPane _logPane = null!;
    private Button _verifyLoadButton = null!;
    private Button _resolveTargetButton = null!;
    private Button _preflightButton = null!;
    private Button _backupButton = null!;
    private Button _deployButton = null!;
    private Button _finishButton = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private Panel _stageHost = null!;

    public MainForm()
    {
        Text = "DeployToolkit Deployer";
        AppTheme.Apply(this, primaryWindow: true);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 560);
        MinimumSize = new Size(680, 500);

        _loadStage = new StageLoadPackage(this);
        _resolveStage = new StageResolveTarget(this);
        _preflightStage = new StagePreflight(this);
        _backupStage = new StageBackup(this);
        _deployStage = new StageDeploy(this);

        BuildMenu();
        BuildHeader();
        BuildStageArea();
        BuildBottomBar();
        BuildStatusStrip();

        ShowStage(_loadStage);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = ConnectOnStartupAsync();
    }

    // ---------------------------------------------------------------
    // State the stage panels read/write

    /// <summary>The open registry store; null until a connection succeeds.</summary>
    internal IRegistryStore? Store => _store;

    /// <summary>The package being deployed; null until step 1 succeeds.</summary>
    internal DeploymentContext? Context => _context;

    /// <summary>True when the registry behind the run is the local-file
    /// offline fallback (plan §2.2) — controls record-if-missing behavior and
    /// offline result writing.</summary>
    internal bool OfflineMode => _settings.Mode == RegistryMode.LocalFile;

    /// <summary>Current stage-machine state.</summary>
    internal DeployerStage Stage => _stage;

    internal void SetStage(DeployerStage stage)
    {
        _stage = stage;
        UpdateStageButtons();
    }

    /// <summary>Replaces the context (fresh load or reset).</summary>
    internal void SetContext(DeploymentContext? context)
    {
        _context = context;
        UpdateHeader();
    }

    internal void AppendLog(string line) => _logPane.AppendLine(line);

    internal void ClearLog() => _logPane.ClearAll();

    internal void SetLastRunLogPath(string path)
    {
        _lastRunLogPath = path;
        _viewLogItem.Enabled = true;
    }

    /// <summary>Whether a run log is available to view.</summary>
    internal bool HasLastRunLog => _lastRunLogPath is not null;

    internal void ShowStage(StagePanel stage)
    {
        if (ReferenceEquals(_currentStage, stage))
            return;
        ShowStageCore(stage);
    }

    /// <summary>Re-shows a stage the user is already on (used to force
    /// OnEnter re-runs, e.g. after a fresh connection).</summary>
    internal void RefreshStage(StagePanel stage) => ShowStageCore(stage);

    private void ShowStageCore(StagePanel stage)
    {
        _currentStage = stage;
        _stageHost.Controls.Clear();
        _stageHost.Controls.Add(stage);
        stage.OnEnter();
    }

    /// <summary>Called by the load stage after verify+match succeeded:
    /// advances to the resolve-target stage (which routes to pre-flight for
    /// non-IIS targets).</summary>
    internal void OnPackageLoaded()
    {
        SetStage(DeployerStage.Loaded);
        ShowStage(_resolveStage);
        _resolveStage.StartResolve();
    }

    /// <summary>Called by the resolve stage once the target is settled.</summary>
    internal void OnTargetResolved() => ShowStage(_preflightStage);

    /// <summary>Called by the pre-flight stage when every check passes.</summary>
    internal void OnPreflightPassed() => SetStage(DeployerStage.Ready);

    /// <summary>Called by the deploy stage when a run finished successfully.</summary>
    internal void OnRunSucceeded() => SetStage(DeployerStage.Done);

    /// <summary>Called by the deploy stage when a run failed or was cancelled.</summary>
    internal void OnRunFailed() => SetStage(DeployerStage.Ready);

    /// <summary>Shows the deploy stage panel without starting a run (the
    /// Backup stage's "Skip — go to Deploy" affordance).</summary>
    internal void ShowDeployStage() => ShowStage(_deployStage);

    // ---------------------------------------------------------------
    // UI construction

    private void BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };

        _loadPackageItem = new ToolStripMenuItem("Load Package…")
        {
            Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold), // the primary action
        };
        _loadPackageItem.Click += (_, _) => LoadPackageViaDialog();

        var connectionItem = new ToolStripMenuItem("Registry Connection…");
        connectionItem.Click += (_, _) => ChangeConnection();

        _rollbackItem = new ToolStripMenuItem("Standalone Rollback…");
        _rollbackItem.Click += (_, _) => StandaloneRollback();

        _viewLogItem = new ToolStripMenuItem("View Last Run Log") { Enabled = false };
        _viewLogItem.Click += (_, _) => ViewLastRunLog();

        menu.Items.Add(_loadPackageItem);
        menu.Items.Add(connectionItem);
        menu.Items.Add(_rollbackItem);
        menu.Items.Add(_viewLogItem);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(12, 8, 12, 4) };
        _packageSummary = new Label
        {
            Dock = DockStyle.Fill,
            Text = "No package loaded. Use 'Load Package…' to pick a delta.zip (plan §11 step 1).",
            ForeColor = Color.DimGray,
            AutoEllipsis = false,
        };
        header.Controls.Add(_packageSummary);
        Controls.Add(header);
    }

    private void BuildStageArea()
    {
        _stageHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4), AutoScroll = true };
        Controls.Add(_stageHost);
    }

    private void BuildBottomBar()
    {
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 220 };

        _logPane = new LogPane { Dock = DockStyle.Fill };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 52,
            Padding = new Padding(12, 8, 12, 8),
            WrapContents = false,
        };
        _verifyLoadButton = MakeStageButton("Verify && Load");
        _resolveTargetButton = MakeStageButton("Resolve Target");
        _preflightButton = MakeStageButton("Pre-flight");
        _backupButton = MakeStageButton("Backup");
        _deployButton = MakeStageButton("Deploy");
        _finishButton = MakeStageButton("Finish");
        _deployButton.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold);

        _verifyLoadButton.Click += (_, _) =>
        {
            SetContext(null); // a fresh load always starts from an empty context
            ShowStage(_loadStage);
            _loadStage.StartLoad();
        };
        _resolveTargetButton.Click += (_, _) => { ShowStage(_resolveStage); _resolveStage.StartResolve(); };
        _preflightButton.Click += (_, _) => ShowStage(_preflightStage);
        _backupButton.Click += (_, _) => ShowStage(_backupStage);
        _deployButton.Click += (_, _) => { ShowStage(_deployStage); _deployStage.StartDeploy(); };
        _finishButton.Click += (_, _) => ResetForNewPackage();

        buttons.Controls.Add(_verifyLoadButton);
        buttons.Controls.Add(_resolveTargetButton);
        buttons.Controls.Add(_preflightButton);
        buttons.Controls.Add(_backupButton);
        buttons.Controls.Add(_deployButton);
        buttons.Controls.Add(_finishButton);

        bottom.Controls.Add(_logPane);
        bottom.Controls.Add(buttons);
        Controls.Add(bottom);
    }

    private static Button MakeStageButton(string text)
    {
        var button = new Button { Text = text, Enabled = false };
        AppTheme.StyleButton(button);
        return button;
    }

    private void BuildStatusStrip()
    {
        var strip = new StatusStrip { Dock = DockStyle.Bottom };
        _statusLabel = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        };
        strip.Items.Add(_statusLabel);
        Controls.Add(strip);
    }

    // ---------------------------------------------------------------
    // Stage-machine rendering

    private void UpdateStageButtons()
    {
        var connected = _store is not null;
        var running = _stage == DeployerStage.Running;
        var loaded = _stage is DeployerStage.Loaded or DeployerStage.Ready;

        _verifyLoadButton.Enabled = connected && !running;
        _resolveTargetButton.Enabled = connected && loaded;
        _preflightButton.Enabled = connected && loaded;
        _backupButton.Enabled = connected && loaded;
        _deployButton.Enabled = connected && _stage == DeployerStage.Ready;
        _finishButton.Enabled = _stage == DeployerStage.Done;
        _loadPackageItem.Enabled = connected && !running;
        _rollbackItem.Enabled = !running;
    }

    private void UpdateHeader()
    {
        if (_context is not { } context)
        {
            _packageSummary.Text = "No package loaded. Use 'Load Package…' to pick a delta.zip (plan §11 step 1).";
            _packageSummary.ForeColor = Color.DimGray;
            return;
        }

        var manifest = context.Manifest;
        var package = context.Package;
        var target = context.TargetType is { } type ? type.ToString() : "(not resolved yet)";
        _packageSummary.Text =
            $"{manifest.Client} / {manifest.Component} — v{manifest.Version}    (component {manifest.ComponentId})\n" +
            $"Package: {(package is null ? "(not matched yet)" : $"{package.PackageId} — {package.Status}")}    Target: {target}\n" +
            $"Zip: {context.ZipPath}";
        _packageSummary.ForeColor = Color.Black;
    }

    private void ResetForNewPackage()
    {
        SetContext(null);
        SetStage(DeployerStage.Empty);
        ClearLog();
        ShowStageCore(_loadStage); // force re-enter so the panel clears its result state
    }

    // ---------------------------------------------------------------
    // Menu actions

    private void LoadPackageViaDialog()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Load DeployToolkit package",
            Filter = "DeployToolkit package (*.zip)|*.zip|All files (*.*)|*.*",
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        SetContext(null);
        ShowStage(_loadStage);
        _loadStage.SetZipPath(picker.FileName);
        _loadStage.StartLoad();
    }

    private void StandaloneRollback()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Pick the backup folder to roll back (the one containing backup-manifest.json).",
            ShowNewFolderButton = false,
        };
        var defaultRoot = DeployerPaths.DefaultBackupsRoot;
        if (Directory.Exists(defaultRoot))
            picker.SelectedPath = defaultRoot;

        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        var backupFolder = picker.SelectedPath;
        if (AppTheme.Confirm(this,
                $"Restore the backup in\n{backupFolder}\n\nover the site root recorded in its backup-manifest.json?",
                "Standalone rollback") != DialogResult.Yes)
        {
            return;
        }

        Guard.RunAsync(this, "Rolling back…", async cancellationToken =>
        {
            // Rollback restores every file of the backup — real IO, so it
            // runs off the UI thread; the busy dialog's Cancel frees the UI
            // while the (unabortable) restore finishes in the background.
            await Task.Run(() => new BackupManager().Rollback(backupFolder), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return; // abandoned — no completion dialog (the restore still completed)

            MessageBox.Show(this, $"Files restored from backup:\n{backupFolder}",
                AppTheme.Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    /// <summary>Opens the last run's JSON-lines log in the read-only viewer
    /// (plan §8.6 audit trail).</summary>
    internal void ViewLastRunLog()
    {
        if (_lastRunLogPath is null)
            return;
        new RunLogViewerForm(_lastRunLogPath).Show(this);
    }

    // ---------------------------------------------------------------
    // Connection lifecycle (mirrors the Packager shell exactly, with the
    // Deployer's own settings file)

    private async Task ConnectOnStartupAsync()
    {
        _settings = RegistryConnectionSettings.Load(DeployerPaths.SettingsPath);

        // First run in offline mode: pre-fill a sensible default root instead
        // of making the user pick a folder before anything works.
        if (_settings.Mode == RegistryMode.LocalFile && string.IsNullOrWhiteSpace(_settings.LocalRoot))
            _settings.LocalRoot = DefaultOfflineRegistryRoot();

        var connected = await TryConnectAsync(_settings);
        if (!connected)
        {
            // First-run / unreachable registry: offer the connection dialog
            // right away, but the main window still opens (possibly disabled)
            // when the user cancels — startup must never fail.
            using var dialog = new ConnectionDialog(_settings);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.ResultSettings is { } chosen)
            {
                _settings = chosen;
                if (_settings.Mode == RegistryMode.LocalFile && string.IsNullOrWhiteSpace(_settings.LocalRoot))
                    _settings.LocalRoot = DefaultOfflineRegistryRoot();
                connected = await TryConnectAsync(_settings);
            }
        }

        UpdateConnectionUi();
    }

    private void ChangeConnection()
    {
        // The dialog itself runs UNGUARDED — a busy overlay over a modal form
        // it can never outlive used to brick the shell ("Reconnecting…"
        // floating disabled on top of the connection form). Only the actual
        // reconnect below is busy-guarded.
        using var dialog = new ConnectionDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ResultSettings is not { } chosen)
            return; // cancelled — keep the current connection as-is

        _settings = chosen;
        _settings.Save(DeployerPaths.SettingsPath);
        Guard.FireAndForget(this, "Reconnecting…", async () =>
        {
            await TryConnectAsync(_settings);
            UpdateConnectionUi();
            RefreshStage(_currentStage); // stages re-read the store when re-entered
        });
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

    private void DisposeStore()
    {
        if (_store is IDisposable disposable)
            disposable.Dispose();
        _store = null;
    }

    private void UpdateConnectionUi()
    {
        var connected = _store is not null;

        if (connected && _settings.Mode == RegistryMode.SqlServer)
        {
            _statusLabel.Text = $"SQL Server: {SqlServerPart(_settings.ConnectionString!)}";
            _statusLabel.ForeColor = Color.Black;
        }
        else if (connected)
        {
            _statusLabel.Text = $"Offline files: {_settings.LocalRoot}";
            _statusLabel.ForeColor = Color.Black;
        }
        else
        {
            _statusLabel.Text = "No registry connection — loading packages is disabled. " +
                                "Open 'Registry Connection…' to configure one."
                                + (string.IsNullOrEmpty(_connectionError) ? "" : $" ({_connectionError})");
            _statusLabel.ForeColor = Color.Firebrick;
        }

        UpdateStageButtons();
    }

    // ---------------------------------------------------------------
    // Helpers

    private static string DescribeException(Exception ex) =>
        ex is ArgumentException or InvalidOperationException
            ? ex.Message
            : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>Documents\DeployToolkit\OfflineRegistry (with a current-dir
    /// fallback for hosts without a Documents folder) — the same default the
    /// Packager uses, so both tools find the same offline registry files.</summary>
    internal static string DefaultOfflineRegistryRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.CurrentDirectory;
        return Path.Combine(documents, "DeployToolkit", "OfflineRegistry");
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
