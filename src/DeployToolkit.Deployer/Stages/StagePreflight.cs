using DeployToolkit.AppKit;
using DeployToolkit.Core.Targets;
using DeployToolkit.Core.Targets.Plesk;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Plan §11 step 3: the pre-flight panel. Hosts the target-specific inputs
/// (IIS site root + appsettings path; Azure Kudu publish credentials and the
/// optional ARM settings application; Plesk SFTP connection and deploy
/// options), then runs the display-only check list: disk space on the target
/// root drive, site root exists-or-creatable, appsettings path usable, and
/// the target's own settings validated. When every check passes the shell
/// moves to Ready and the Deploy button unlocks.
/// </summary>
internal sealed class StagePreflight : StagePanel
{
    // IIS inputs (assigned inside the Build*Panel helpers called from the
    // constructor — plain fields with null! initializers so the nullable
    // analysis stays quiet about the helper indirection)
    private readonly TableLayoutPanel _iisPanel;
    private TextBox _siteRootBox = null!;
    private TextBox _appSettingsBox = null!;

    // Azure inputs
    private readonly TableLayoutPanel _azurePanel;
    private TextBox _kuduSiteBox = null!;
    private TextBox _kuduUserBox = null!;
    private TextBox _kuduPassBox = null!;
    private CheckBox _armCheckBox = null!;
    private TextBox _armSubscriptionBox = null!;
    private TextBox _armResourceGroupBox = null!;
    private TextBox _armSiteBox = null!;
    private TextBox _armTokenBox = null!;

    // Plesk inputs
    private readonly TableLayoutPanel _pleskPanel;
    private TextBox _pleskHostBox = null!;
    private NumericUpDown _pleskPortBox = null!;
    private TextBox _pleskUserBox = null!;
    private TextBox _pleskPassBox = null!;
    private TextBox _pleskKeyPathBox = null!;
    private TextBox _pleskRootBox = null!;
    private ComboBox _pleskRestartBox = null!;
    private TextBox _pleskXmlApiUrlBox = null!;
    private TextBox _pleskXmlApiLoginBox = null!;
    private TextBox _pleskXmlApiPassBox = null!;
    private TextBox _pleskSiteIdBox = null!;

    // Results
    private readonly TextBox _checklistBox;
    private readonly Label _resultLabel;

    public StagePreflight(MainForm shell)
        : base(shell)
    {
        var layout = MakeVerticalLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // IIS
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Azure
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Plesk
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // check button
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // checklist
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // result

        _iisPanel = BuildIisPanel();
        _azurePanel = BuildAzurePanel();
        _pleskPanel = BuildPleskPanel();
        layout.Controls.Add(_iisPanel);
        // Azure/Plesk panels are hidden (user: "for now make it only IIS").
        // They're still constructed so the fields exist for when they're
        // re-enabled later, but never added to the visible layout.
        _azurePanel.Visible = false;
        _pleskPanel.Visible = false;

        var checkButton = new Button { Text = "Run pre-flight checks" };
        AppTheme.StyleButton(checkButton);
        checkButton.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold);
        checkButton.Click += (_, _) => Guard.RunAsync(Shell, "Running pre-flight checks…", RunChecksAsync);
        layout.Controls.Add(checkButton);

        _checklistBox = MakeReadOnlySummaryBox(180);
        layout.Controls.Add(_checklistBox);

        _resultLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 30,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_resultLabel);

        Controls.Add(layout);
    }

    public override string Title => "3. Pre-flight";

    public override void OnEnter()
    {
        if (Context is not { } context)
        {
            _resultLabel.Text = "Load a package first.";
            _resultLabel.ForeColor = Color.DimGray;
            return;
        }

        var type = context.TargetType;
        _iisPanel.Visible = type == TargetType.IisLocal;
        _azurePanel.Visible = false; // hidden (IIS only for now)
        _pleskPanel.Visible = false; // hidden (IIS only for now)

        if (type == TargetType.IisLocal)
        {
            if (_siteRootBox.Text.Trim().Length == 0)
            {
                _siteRootBox.Text = context.IisTarget?.PhysicalPath ?? string.Empty;
                _appSettingsBox.Text = string.IsNullOrWhiteSpace(_siteRootBox.Text)
                    ? string.Empty
                    : Path.Combine(_siteRootBox.Text.Trim(), "appsettings.json");
            }
        }

        _resultLabel.ForeColor = Color.DimGray;
        _resultLabel.Text = "Running pre-flight checks automatically…";

        // Q2: auto-run pre-flight on entering the step (after the IIS app is
        // selected in step 2 and the user clicks Next). No manual button
        // needed — the checks run automatically.
        Guard.FireAndForget(Shell, "Running pre-flight checks…", RunChecksAsync);
    }

    // ---------------------------------------------------------------
    // Panels

    private TableLayoutPanel BuildIisPanel()
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        panel.Controls.Add(AppTheme.MakeSectionLabel("IIS site root (files deploy here)"));

        var siteRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        siteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        siteRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _siteRootBox = new TextBox { Dock = DockStyle.Fill };
        var browse = new Button { Text = "Browse…" };
        AppTheme.StyleButton(browse);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "The site root the package's files are copied into.",
                ShowNewFolderButton = true,
            };
            if (Directory.Exists(_siteRootBox.Text))
                picker.SelectedPath = _siteRootBox.Text;
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                _siteRootBox.Text = picker.SelectedPath;
                _appSettingsBox.Text = Path.Combine(picker.SelectedPath, "appsettings.json");
            }
        };
        siteRow.Controls.Add(_siteRootBox, 0, 0);
        siteRow.Controls.Add(browse, 1, 0);
        panel.Controls.Add(siteRow);

        panel.Controls.Add(AppTheme.MakeSectionLabel("appsettings.json (the manifest's delta is merged into it)"));
        _appSettingsBox = new TextBox { Dock = DockStyle.Fill };
        panel.Controls.Add(_appSettingsBox);

        return panel;
    }

    private TableLayoutPanel BuildAzurePanel()
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        panel.Controls.Add(AppTheme.MakeSectionLabel("Azure App Service — Kudu publish credentials"));

        panel.Controls.Add(new Label
        {
            Text = "The Azure path does no stop/start and no local backup — Kudu zip deploy is atomic server-side. " +
                   "Credentials come from the app's publish profile (username starts with '$').",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 0, 2, 4),
        });

        var fields = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;

        _kuduSiteBox = new TextBox { Dock = DockStyle.Fill };
        AddField(fields, ref row, "Site name", _kuduSiteBox);
        _kuduUserBox = new TextBox { Dock = DockStyle.Fill };
        AddField(fields, ref row, "Publish username", _kuduUserBox);
        _kuduPassBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        AddField(fields, ref row, "Publish password", _kuduPassBox);

        var loadButton = new Button { Text = "Load from publish settings…" };
        AppTheme.StyleButton(loadButton);
        loadButton.Click += (_, _) => LoadPublishSettings();
        AddField(fields, ref row, "Publish settings file", loadButton);
        panel.Controls.Add(fields);

        _armCheckBox = new CheckBox
        {
            Text = "Apply the manifest's appsettings delta via the ARM Configuration API (optional — zip deploy alone is valid)",
            AutoSize = true,
            Margin = new Padding(2, 6, 2, 2),
        };
        panel.Controls.Add(_armCheckBox);

        var armFields = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        armFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        armFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var armRow = 0;
        _armSubscriptionBox = new TextBox { Dock = DockStyle.Fill };
        AddField(armFields, ref armRow, "Subscription id", _armSubscriptionBox);
        _armResourceGroupBox = new TextBox { Dock = DockStyle.Fill };
        AddField(armFields, ref armRow, "Resource group", _armResourceGroupBox);
        _armSiteBox = new TextBox { Dock = DockStyle.Fill };
        AddField(armFields, ref armRow, "Site name", _armSiteBox);
        _armTokenBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        AddField(armFields, ref armRow, "ARM bearer token", _armTokenBox);
        _armCheckBox.CheckedChanged += (_, _) => armFields.Visible = _armCheckBox.Checked;
        armFields.Visible = false;
        panel.Controls.Add(armFields);

        return panel;
    }

    private TableLayoutPanel BuildPleskPanel()
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        panel.Controls.Add(AppTheme.MakeSectionLabel("Plesk shared hosting — SFTP connection"));

        panel.Controls.Add(new Label
        {
            Text = "The Plesk executor uploads the delta files over SFTP only — the appsettings delta and DB " +
                   "scripts are NOT applied on this target (apply them via the Plesk panel / manually).",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 0, 2, 4),
        });

        var fields = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;

        _pleskHostBox = new TextBox { Dock = DockStyle.Fill };
        AddField(fields, ref row, "Host", _pleskHostBox);
        _pleskPortBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 22, Dock = DockStyle.Fill };
        AddField(fields, ref row, "SFTP port", _pleskPortBox);
        _pleskUserBox = new TextBox { Dock = DockStyle.Fill };
        AddField(fields, ref row, "Username", _pleskUserBox);
        _pleskPassBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        AddField(fields, ref row, "Password", _pleskPassBox);
        _pleskKeyPathBox = new TextBox { Dock = DockStyle.Fill };
        AddField(fields, ref row, "Private key path (optional)", _pleskKeyPathBox);

        _pleskRootBox = new TextBox { Dock = DockStyle.Fill, Text = "/httpdocs" };
        AddField(fields, ref row, "Remote root path", _pleskRootBox);
        _pleskRestartBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Items = { "None", "AppOffline", "XmlApi" },
        };
        _pleskRestartBox.SelectedIndex = 0;
        AddField(fields, ref row, "Restart mode", _pleskRestartBox);
        _pleskXmlApiUrlBox = new TextBox { Dock = DockStyle.Fill };
        AddField(fields, ref row, "XmlApi base URL", _pleskXmlApiUrlBox);
        _pleskXmlApiLoginBox = new TextBox { Dock = DockStyle.Fill };
        AddField(fields, ref row, "XmlApi login", _pleskXmlApiLoginBox);
        _pleskXmlApiPassBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        AddField(fields, ref row, "XmlApi password", _pleskXmlApiPassBox);
        _pleskSiteIdBox = new TextBox { Dock = DockStyle.Fill };
        AddField(fields, ref row, "Plesk site id", _pleskSiteIdBox);

        panel.Controls.Add(fields);
        return panel;
    }

    // ---------------------------------------------------------------
    // Checks

    private async Task RunChecksAsync()
    {
        if (Context is not { } context)
        {
            AppTheme.Error(this, "Load a package first.");
            return;
        }

        if (Shell.Store is null)
        {
            AppTheme.Error(this, "No registry connection — pre-flight results are recorded against it.");
            return;
        }

        var type = context.TargetType;
        if (type is null)
        {
            _resultLabel.ForeColor = Color.Firebrick;
            _resultLabel.Text = "Target type not resolved — run 'Resolve Target' first.";
            return;
        }

        CommitInputs(context, type.Value);

        var checks = new List<(bool Passed, string Text)>();

        if (type == TargetType.IisLocal)
        {
            checks.Add(context.IisTarget is not null
                ? (true, $"IIS target resolved: site '{context.IisTarget.SiteName}', app '{context.IisTarget.AppPath}', pool '{context.IisTarget.AppPoolName ?? "(none)"}'.")
                : (false, "IIS target NOT resolved — run 'Resolve Target' first."));

            var siteRoot = context.SiteRoot?.Trim() ?? string.Empty;
            if (siteRoot.Length == 0)
            {
                checks.Add((false, "Site root is empty."));
            }
            else
            {
                try
                {
                    if (Directory.Exists(siteRoot))
                        checks.Add((true, $"Site root exists: {siteRoot}"));
                    else
                    {
                        Directory.CreateDirectory(siteRoot); // prove it is creatable
                        checks.Add((true, $"Site root did not exist and was created: {siteRoot}"));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    checks.Add((false, $"Site root cannot be created: {siteRoot} ({ex.Message})"));
                }
            }

            var appSettings = context.AppSettingsPath?.Trim() ?? string.Empty;
            if (appSettings.Length == 0)
            {
                checks.Add((false, "appsettings.json path is empty."));
            }
            else
            {
                try
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(appSettings));
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);
                    checks.Add((true, $"appsettings path usable: {appSettings}" + (File.Exists(appSettings) ? " (existing file — delta merges into it)" : " (will be created on merge)")));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    checks.Add((false, $"appsettings path is not usable: {appSettings} ({ex.Message})"));
                }
            }
        }
        else if (type == TargetType.AzureAppService)
        {
            checks.Add(string.IsNullOrWhiteSpace(context.KuduSiteName)
                ? (false, "Kudu site name is empty.")
                : (true, $"Kudu site: {context.KuduSiteName}"));
            checks.Add(string.IsNullOrWhiteSpace(context.KuduUsername)
                ? (false, "Publish username is empty (from the publish profile, starts with '$').")
                : (true, "Publish username set."));
            checks.Add(string.IsNullOrEmpty(context.KuduPassword)
                ? (false, "Publish password is empty.")
                : (true, "Publish password set."));

            if (context.ApplyAzureSettings)
            {
                checks.Add(string.IsNullOrWhiteSpace(context.ArmSubscriptionId) || string.IsNullOrWhiteSpace(context.ArmResourceGroup) || string.IsNullOrWhiteSpace(context.ArmSiteName)
                    ? (false, "ARM settings application enabled but subscription/resource group/site is missing.")
                    : (true, $"ARM settings application enabled for {context.ArmSubscriptionId}/{context.ArmResourceGroup}/{context.ArmSiteName} (token may be entered at deploy time)."));
            }
            else if (context.Manifest.AppSettingsDelta.Count > 0)
            {
                checks.Add((true, $"ARM settings application is OFF — {context.Manifest.AppSettingsDelta.Count} delta key(s) will NOT be applied (the executor reports this)."));
            }
        }
        else // Plesk
        {
            if (context.PleskConnection is { } connection)
            {
                checks.Add(string.IsNullOrWhiteSpace(connection.Host)
                    ? (false, "Plesk host is empty.")
                    : (true, $"Plesk host: {connection.Host}:{connection.Port}"));
                checks.Add(string.IsNullOrWhiteSpace(connection.Username)
                    ? (false, "Plesk username is empty.")
                    : (true, $"Plesk user: {connection.Username} ({(connection.PrivateKeyPath is null ? "password auth" : "private key auth")})"));
            }
            else
            {
                checks.Add((false, "Plesk connection options missing."));
            }

            if (context.PleskDeploy is { } deploy)
            {
                try
                {
                    deploy.Validate();
                    checks.Add((true, $"Plesk deploy options valid: root {deploy.RemoteRootPath}, restart {deploy.RestartMode}."));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    checks.Add((false, $"Plesk deploy options invalid: {ex.Message}"));
                }
            }
            else
            {
                checks.Add((false, $"Plesk deploy options could not be built: {_pleskCommitError ?? "RemoteRootPath is required."}"));
            }
        }

        // Disk space: the deploy writes roughly twice the package's file
        // bytes (backup + new files) plus change; for executor targets the
        // server-side space cannot be verified from here, so the local temp
        // extraction drive is checked instead.
        var rootToCheck = type == TargetType.IisLocal
            ? context.SiteRoot
            : DeployerPaths.TempExtractRoot(context.Package?.PackageId ?? "preflight");
        checks.Add(CheckDiskSpace(rootToCheck, context.Manifest));

        var lines = checks.Select(c => (c.Passed ? "[ OK ] " : "[FAIL] ") + c.Text).ToList();
        _checklistBox.Lines = lines.ToArray();

        var passed = checks.All(c => c.Passed);
        _resultLabel.ForeColor = passed ? Color.ForestGreen : Color.Firebrick;
        _resultLabel.Text = passed
            ? "Pre-flight passed — 'Deploy' is unlocked. (Backup is advisory; the run also backs up automatically.)"
            : "Pre-flight FAILED — fix the [FAIL] rows above and re-run.";

        if (passed)
            Shell.OnPreflightPassed();

        await Task.CompletedTask; // async for symmetry with the guarded work signature
    }

    private static (bool Passed, string Text) CheckDiskSpace(string? targetRoot, Core.Manifest.ComponentManifest manifest)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetRoot))
                return (false, "No root path to check for disk space.");

            var fullRoot = Path.GetFullPath(targetRoot);
            var driveRoot = Path.GetPathRoot(fullRoot);
            if (string.IsNullOrEmpty(driveRoot))
                return (false, $"Cannot determine the drive for '{fullRoot}'.");

            var drive = DriveInfo.GetDrives()
                .FirstOrDefault(d => string.Equals(d.Name, driveRoot, StringComparison.OrdinalIgnoreCase));
            if (drive is null || !drive.IsReady)
                return (false, $"Drive '{driveRoot}' is not ready.");

            var neededBytes = manifest.Files.Sum(f => f.SizeBytes) * 2 + 100L * 1024 * 1024;
            var available = drive.AvailableFreeSpace;
            return available > neededBytes
                ? (true, $"Disk space on {drive.Name}: {FormatBytes(available)} available, {FormatBytes(neededBytes)} needed (backup + new files + margin).")
                : (false, $"Disk space on {drive.Name}: {FormatBytes(available)} available but {FormatBytes(neededBytes)} needed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, $"Disk space check failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------
    // Input commit

    /// <summary>Set when the Plesk options record refuses a value at
    /// construction (e.g. a remote root without a leading '/').</summary>
    private string? _pleskCommitError;

    private void CommitInputs(DeploymentContext context, TargetType type)
    {
        if (type == TargetType.IisLocal)
        {
            context.SiteRoot = NullIfEmpty(_siteRootBox.Text);
            context.AppSettingsPath = NullIfEmpty(_appSettingsBox.Text)
                                      ?? (string.IsNullOrWhiteSpace(_siteRootBox.Text)
                                          ? null
                                          : Path.Combine(_siteRootBox.Text.Trim(), "appsettings.json"));
        }
        else if (type == TargetType.AzureAppService)
        {
            context.KuduSiteName = NullIfEmpty(_kuduSiteBox.Text);
            context.KuduUsername = NullIfEmpty(_kuduUserBox.Text);
            context.KuduPassword = string.IsNullOrEmpty(_kuduPassBox.Text) ? null : _kuduPassBox.Text;
            context.ApplyAzureSettings = _armCheckBox.Checked;
            context.ArmSubscriptionId = NullIfEmpty(_armSubscriptionBox.Text);
            context.ArmResourceGroup = NullIfEmpty(_armResourceGroupBox.Text);
            context.ArmSiteName = NullIfEmpty(_armSiteBox.Text);
            context.ArmToken = NullIfEmpty(_armTokenBox.Text);
        }
        else if (type == TargetType.Plesk)
        {
            _pleskCommitError = null;
            try
            {
                context.PleskConnection = new PleskConnectionOptions(
                    Host: _pleskHostBox.Text.Trim(),
                    Port: (int)_pleskPortBox.Value,
                    Username: _pleskUserBox.Text.Trim(),
                    Password: NullIfEmpty(_pleskPassBox.Text),
                    PrivateKeyPath: NullIfEmpty(_pleskKeyPathBox.Text));
                context.PleskDeploy = new PleskDeployOptions(
                    RemoteRootPath: _pleskRootBox.Text.Trim(),
                    RestartMode: _pleskRestartBox.SelectedIndex switch
                    {
                        1 => PleskRestartMode.AppOffline,
                        2 => PleskRestartMode.XmlApi,
                        _ => PleskRestartMode.None,
                    },
                    XmlApiBaseUrl: NullIfEmpty(_pleskXmlApiUrlBox.Text),
                    XmlApiLogin: NullIfEmpty(_pleskXmlApiLoginBox.Text),
                    XmlApiPassword: NullIfEmpty(_pleskXmlApiPassBox.Text),
                    SiteId: NullIfEmpty(_pleskSiteIdBox.Text));
            }
            catch (ArgumentException ex)
            {
                // Surface the options record's own guard as a failed check
                // row instead of crashing the panel; both options stay null
                // and the Plesk checks below report the failure.
                _pleskCommitError = ex.Message;
                context.PleskConnection = null;
                context.PleskDeploy = null;
            }
        }
    }

    private void LoadPublishSettings()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Load Azure publish settings",
            Filter = "Publish settings (*.publishsettings)|*.publishsettings|All files (*.*)|*.*",
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        var credentials = PublishSettingsFile.TryLoad(picker.FileName, out var error);
        if (credentials is null)
        {
            AppTheme.Error(this, error ?? "The publish settings file could not be parsed.");
            return;
        }

        _kuduSiteBox.Text = credentials.SiteName;
        _kuduUserBox.Text = credentials.Username;
        _kuduPassBox.Text = credentials.Password;
        if (_armSiteBox.Text.Trim().Length == 0)
            _armSiteBox.Text = credentials.SiteName;
    }

    // ---------------------------------------------------------------
    // Shared layout helper

    private static void AddField(TableLayoutPanel layout, ref int row, string label, Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 6, 8, 2),
        }, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(2, 2, 12, 6);
        layout.Controls.Add(control, 1, row);
        row++;
    }

    private static string? NullIfEmpty(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F1} MB"
        : bytes >= 1 << 10 ? $"{bytes / (double)(1 << 10):F1} KB"
        : $"{bytes} B";
}
