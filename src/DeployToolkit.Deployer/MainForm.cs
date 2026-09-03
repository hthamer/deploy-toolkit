using DeployToolkit.AppKit;
using DeployToolkit.Core.Backup;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;
using DeployToolkit.Deployer.Stages;

namespace DeployToolkit.Deployer;

/// <summary>
/// The §11 stage state machine: Empty (no package) → Loaded (package
/// verified + matched to a registry record) → Ready (target resolved +
/// pre-flight passed) → Running (deploy run) → Done (run succeeded; a new
/// package resets). A failed/cancelled run returns to Ready so the same
/// package can be re-deployed with one click.
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
/// The §11 deploy flow as a WIZARD (same pattern as the Packager's packaging
/// wizard): a left-hand step list, one <see cref="StagePanel"/> at a time in
/// the content area, and Back / Next navigation at the bottom. The steps are
/// linear — each unlocks the next only when its work is done — and every
/// transition is user-driven, so modal pickers (target type, IIS application)
/// appear inside their own step instead of stacking on top of each other.
/// <code>
///   1. Package     — pick the delta.zip; integrity + registry match run automatically
///   2. Target      — resolve where it deploys (IIS app selection)
///   3. Pre-flight  — run the checks; must pass
///   4. Backup      — advisory pre-backup (the deploy run backs up anyway)
///   5. Deploy      — the guarded run
/// </code>
/// Startup never crashes on a missing/unreachable registry: settings are
/// loaded tolerantly (own file: %APPDATA%\DeployToolkit\deployer-registry.json)
/// and when no store could be opened the flow stays disabled with a hint in
/// the status strip until the user configures a working connection.
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
    private readonly StagePanel[] _steps;

    private int _currentIndex;
    private int _maxReachedIndex;

    private ListBox _stepList = null!;
    private Panel _contentPanel = null!;
    private LogPane _logPane = null!;
    private Button _backButton = null!;
    private Button _nextButton = null!;
    private Button _deployNowButton = null!;
    private Button _closeButton = null!;
    private Label _hintLabel = null!;
    private ToolStripMenuItem _loadPackageItem = null!;
    private ToolStripMenuItem _rollbackItem = null!;
    private ToolStripMenuItem _viewLogItem = null!;
    private ToolStripStatusLabel _statusLabel = null!;

    public MainForm()
    {
        Text = "DeployToolkit Deployer";
        AppTheme.Apply(this, primaryWindow: true);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1020, 700);
        MinimumSize = new Size(940, 620);

        _loadStage = new StageLoadPackage(this);
        _resolveStage = new StageResolveTarget(this);
        _preflightStage = new StagePreflight(this);
        _backupStage = new StageBackup(this);
        _deployStage = new StageDeploy(this);
        _steps = new StagePanel[] { _loadStage, _resolveStage, _preflightStage, _backupStage, _deployStage };

        BuildUi();
        foreach (var step in _steps)
            _stepList.Items.Add(step.Title);
        ShowStep(0);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = ConnectOnStartupAsync();
    }

    // ---------------------------------------------------------------
    // State the step panels read/write

    /// <summary>The open registry store; null until a connection succeeds.</summary>
    internal IRegistryStore? Store => _store;

    /// <summary>The connection settings currently in effect — carries the
    /// central API base URL (persisted) plus any session-only API credentials
    /// entered in the connection dialog.</summary>
    internal RegistryConnectionSettings ConnectionSettings => _settings;

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
        RefreshNav();
    }

    /// <summary>Replaces the context (fresh load or reset).</summary>
    internal void SetContext(DeploymentContext? context)
    {
        _context = context;
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

    // ---------------------------------------------------------------
    // Step transitions — every move is user-driven (Back/Next/step list),
    // so the shell only refreshes navigation, it never auto-advances.

    /// <summary>Called by the load step after verify+match succeeded: unlocks
    /// the Target step (the user clicks Next).</summary>
    internal void OnPackageLoaded()
    {
        SetStage(DeployerStage.Loaded);
        // Unlock step 2 (Target) so the user can click Next. Without this,
        // _maxReachedIndex stays at 0 and GoToStep(1) returns early (1 > 0).
        if (_maxReachedIndex < 1)
            _maxReachedIndex = 1;
        RefreshNav();
    }

    /// <summary>Called by the resolve step once the target is settled: unlocks
    /// the Pre-flight step.</summary>
    internal void OnTargetResolved()
    {
        if (_maxReachedIndex < 2)
            _maxReachedIndex = 2;
        RefreshNav();
    }

    /// <summary>Called by the pre-flight step when every check passes.</summary>
    internal void OnPreflightPassed()
    {
        SetStage(DeployerStage.Ready);
        // Unlock steps 4 (Backup) and 5 (Deploy) so the user can navigate.
        if (_maxReachedIndex < 4)
            _maxReachedIndex = 4;
        RefreshNav();
    }

    /// <summary>Called by the deploy step when a run finished successfully.</summary>
    internal void OnRunSucceeded() => SetStage(DeployerStage.Done);

    /// <summary>Called by the deploy step when a run failed or was cancelled.</summary>
    internal void OnRunFailed() => SetStage(DeployerStage.Ready);

    /// <summary>Jumps straight to the Deploy step (the Backup step's "Skip —
    /// go to Deploy" affordance).</summary>
    internal void ShowDeployStage() => ShowStep(IndexOf(_deployStage));

    /// <summary>Re-runs the given step's OnEnter (used after a fresh
    /// connection so steps re-read the store).</summary>
    internal void RefreshStage(StagePanel stage)
    {
        if (ReferenceEquals(_steps[_currentIndex], stage))
        {
            stage.OnEnter();
            RefreshNav();
        }
    }

    // ---------------------------------------------------------------
    // UI construction

    private void BuildUi()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };
        _loadPackageItem = new ToolStripMenuItem("Load Package…")
        {
            Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold),
        };
        _loadPackageItem.Click += (_, _) => LoadPackageViaDialog();
        var connectionItem = new ToolStripMenuItem("Registry Connection…");
        connectionItem.Click += (_, _) => ChangeConnection();

        var advanced = new ToolStripMenuItem("Advanced");
        _rollbackItem = new ToolStripMenuItem("Standalone Rollback…");
        _rollbackItem.Click += (_, _) => StandaloneRollback();
        _viewLogItem = new ToolStripMenuItem("View Last Run Log") { Enabled = false };
        _viewLogItem.Click += (_, _) => ViewLastRunLog();
        advanced.DropDownItems.Add(_rollbackItem);
        advanced.DropDownItems.Add(_viewLogItem);

        menu.Items.Add(_loadPackageItem);
        menu.Items.Add(connectionItem);
        menu.Items.Add(advanced);
        MainMenuStrip = menu;

        _contentPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };

        _stepList = new ListBox
        {
            Dock = DockStyle.Left,
            Width = 210,
            BorderStyle = BorderStyle.FixedSingle,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 26,
            IntegralHeight = false,
            Font = new Font(AppTheme.FontFamily, 9.75f),
        };
        _stepList.DrawItem += StepsList_DrawItem;
        _stepList.SelectedIndexChanged += StepsList_SelectedIndexChanged;

        _logPane = new LogPane { Dock = DockStyle.Bottom, Height = 118 };

        var nav = new Panel { Dock = DockStyle.Bottom, Height = 54 };
        _hintLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Padding = new Padding(12, 0, 8, 0),
        };
        var navButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            Width = 380,
            Padding = new Padding(4, 9, 12, 8),
            WrapContents = false,
        };
        _backButton = new Button { Text = "< Back" };
        _nextButton = new Button { Text = "Next >" };
        _deployNowButton = new Button { Text = "Deploy", Enabled = false };
        _closeButton = new Button { Text = "Close" };
        AppTheme.StyleButton(_backButton);
        AppTheme.StyleButton(_nextButton);
        AppTheme.StyleButton(_deployNowButton);
        AppTheme.StyleButton(_closeButton);
        _nextButton.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold);
        _deployNowButton.Font = new Font(AppTheme.FontFamily, 9.5f, FontStyle.Bold);
        _deployNowButton.MinimumSize = new Size(110, 0);

        _backButton.Click += (_, _) => GoToStep(_currentIndex - 1);
        _nextButton.Click += (_, _) => GoToStep(_currentIndex + 1);
        _deployNowButton.Click += (_, _) => _deployStage.StartDeploy();
        _closeButton.Click += (_, _) => Close();

        navButtons.Controls.Add(_backButton);
        navButtons.Controls.Add(_nextButton);
        navButtons.Controls.Add(_deployNowButton);
        navButtons.Controls.Add(_closeButton);

        nav.Controls.Add(_hintLabel);
        nav.Controls.Add(navButtons);

        var status = new StatusStrip { Dock = DockStyle.Bottom };
        _statusLabel = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        };
        status.Items.Add(_statusLabel);

        // Docking order matters: Fill first, then edges — each later add docks
        // within the remaining space, so nothing ever overlaps the menu.
        Controls.Add(_contentPanel);
        Controls.Add(_stepList);
        Controls.Add(_logPane);
        Controls.Add(nav);
        Controls.Add(status);
        Controls.Add(menu);
    }

    // ---------------------------------------------------------------
    // Navigation

    private StagePanel CurrentStep => _steps[_currentIndex];

    private int IndexOf(StagePanel step)
    {
        for (var i = 0; i < _steps.Length; i++)
        {
            if (ReferenceEquals(_steps[i], step))
                return i;
        }
        return 0;
    }

    /// <summary>Whether the user may move FORWARD past the given step —
    /// each step's completion condition (the wizard's linearity rule).</summary>
    private bool CanProceed(StagePanel step) => step switch
    {
        StageLoadPackage => _context is not null,
        StageResolveTarget => _context is { } c
            && c.TargetType is not null
            && (c.TargetType != TargetType.IisLocal || c.IisTarget is not null),
        StagePreflight => _stage is DeployerStage.Ready or DeployerStage.Running or DeployerStage.Done,
        _ => true, // Backup (advisory) and Deploy (terminal) steps
    };

    private void GoToStep(int index)
    {
        if (index < 0 || index >= _steps.Length || index == _currentIndex)
            return;
        if (index > _maxReachedIndex)
            return; // not unlocked yet

        ShowStep(index);
    }

    private void ShowStep(int index)
    {
        _currentIndex = index;
        if (index > _maxReachedIndex)
            _maxReachedIndex = index;

        _contentPanel.Controls.Clear();
        var step = _steps[index];
        _contentPanel.Controls.Add(step);
        step.OnEnter();

        if (_stepList.SelectedIndex != index)
            _stepList.SelectedIndex = index;
        RefreshNav();
    }

    private void RefreshNav()
    {
        var running = _stage == DeployerStage.Running;
        var connected = _store is not null;
        var last = _currentIndex == _steps.Length - 1;

        _hintLabel.Text = HintFor(CurrentStep);

        _backButton.Enabled = _currentIndex > 0 && !running;
        _nextButton.Visible = !last;
        _nextButton.Enabled = !last && !running && CanProceed(CurrentStep);
        _deployNowButton.Visible = last;
        _deployNowButton.Enabled = last && !running && connected && _stage == DeployerStage.Ready;
        _closeButton.Enabled = true;
        _loadPackageItem.Enabled = connected && !running;
        _rollbackItem.Enabled = !running;
        _stepList.Invalidate();
    }

    private static string HintFor(StagePanel step) => step switch
    {
        StageLoadPackage => "Pick the package file — verification and registry matching run automatically.",
        StageResolveTarget => "Choose where this package deploys: select the IIS application, then Next.",
        StagePreflight => "Run the checks — they must all pass before Deploy unlocks.",
        StageBackup => "Optional: take the backup now. The deploy run always backs up before touching anything.",
        _ => "Review the run plan and press Deploy when ready.",
    };

    // ---------------------------------------------------------------
    // Step list drawing (mirrors the Packager wizard's look)

    private void StepsList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _steps.Length)
            return;

        var available = e.Index <= _maxReachedIndex;
        var selected = e.Index == _currentIndex;

        var backColor = selected ? Color.FromArgb(204, 228, 247) : Color.White;
        using (var backBrush = new SolidBrush(backColor))
            e.Graphics.FillRectangle(backBrush, e.Bounds);

        var textColor = available ? Color.Black : SystemColors.GrayText;
        var fontStyle = selected ? FontStyle.Bold : FontStyle.Regular;
        using var textBrush = new SolidBrush(textColor);
        using var font = new Font(AppTheme.FontFamily, 9.75f, fontStyle);
        var bounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        var format = new StringFormat { LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(_steps[e.Index].Title, font, textBrush, bounds, format);
    }

    private void StepsList_SelectedIndexChanged(object? sender, EventArgs e)
    {
        // Clicking a not-yet-reachable step snaps back to the current one.
        if (_stepList.SelectedIndex != _currentIndex)
        {
            if (_stepList.SelectedIndex >= 0 && _stepList.SelectedIndex <= _maxReachedIndex)
                GoToStep(_stepList.SelectedIndex);
            else
                _stepList.SelectedIndex = _currentIndex;
        }
    }

    // ---------------------------------------------------------------
    // Reset / menu actions

    private void ResetForNewPackage()
    {
        SetContext(null);
        SetStage(DeployerStage.Empty);
        ClearLog();
        _maxReachedIndex = 0;
        ShowStep(0);
    }

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
        SetStage(DeployerStage.Empty);
        ShowStep(0);
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
            using var dialog = new RegistryApiConnectionDialog(_settings);
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
        using var dialog = new RegistryApiConnectionDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ResultSettings is not { } chosen)
            return; // cancelled — keep the current connection as-is

        _settings = chosen;
        _settings.Save(DeployerPaths.SettingsPath);
        Guard.FireAndForget(this, "Reconnecting…", async () =>
        {
            await TryConnectAsync(_settings);
            UpdateConnectionUi();
            RefreshStage(_steps[_currentIndex]); // steps re-read the store when re-entered
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

        RefreshNav();
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
