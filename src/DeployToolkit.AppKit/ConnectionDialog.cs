namespace DeployToolkit.AppKit;

/// <summary>
/// Dialog to view/edit <see cref="RegistryConnectionSettings"/>: radio
/// choice between SQL Server (Azure SQL) and the local-file offline fallback,
/// a monospaced multiline connection-string box with a helper hint, a folder
/// picker for the local root, a Test button (creates the store, counts
/// clients), and OK/Cancel.
///
/// Two hosting modes:
/// <list type="bullet">
/// <item><b>Modal</b> (default ctor): the dialog only RETURNS the settings
/// (<see cref="ResultSettings"/>) — persisting them via
/// <c>RegistryConnectionSettings.Save(...)</c> is the CALLER's job.</item>
/// <item><b>Embedded</b> (<see cref="CreateEmbedded"/>): shown modeless
/// inside the app (e.g. as an MDI child). OK validates, hands the settings
/// straight to the host's apply-callback and closes the form; Cancel/X just
/// closes. The callback returns <c>false</c> to DECLINE the apply (e.g. the
/// user refused to discard an in-progress package wizard the connection
/// change would close) — the dialog then stays open with the typed settings
/// intact instead of silently dropping them. The host owns persisting +
/// reconnecting.</item>
/// </list>
/// </summary>
public sealed class ConnectionDialog : Form
{
    private readonly RadioButton _sqlServerRadio;
    private readonly RadioButton _localFileRadio;
    private readonly TextBox _connectionStringBox;
    private readonly TextBox _localRootBox;
    private readonly TextBox _packageStoreBox;
    private readonly TextBox _gitTagTemplateBox;
    private readonly Label _statusLabel;
    private readonly Func<RegistryConnectionSettings, bool>? _onApplied;

    /// <summary>The settings built from the dialog state when closed with OK;
    /// null when cancelled.</summary>
    public RegistryConnectionSettings? ResultSettings { get; private set; }

    public ConnectionDialog(RegistryConnectionSettings? current = null)
        : this(current, onApplied: null)
    {
    }

    private ConnectionDialog(RegistryConnectionSettings? current, Func<RegistryConnectionSettings, bool>? onApplied)
    {
        current ??= new RegistryConnectionSettings();
        _onApplied = onApplied;

        Text = "Registry connection";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(680, 680);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(12, 12, 12, 4),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // --- mode radios ---
        var modePanel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        _sqlServerRadio = new RadioButton { Text = "SQL Server / Azure SQL (shared central registry)", AutoSize = true };
        _localFileRadio = new RadioButton
        {
            Text = "Local file (offline fallback — registry stored as JSON files in a folder)",
            AutoSize = true,
        };
        modePanel.Controls.Add(_sqlServerRadio);
        modePanel.Controls.Add(_localFileRadio);
        layout.Controls.Add(AppTheme.MakeSectionLabel("Registry mode"));
        layout.Controls.Add(modePanel);

        // --- SQL Server section ---
        _connectionStringBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Height = 68,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f), // monospace hint: connection strings read better in mono
            AcceptsReturn = false,
        };
        var sqlHelper = new Label
        {
            Text = "Azure SQL: Server=tcp:<server>.database.windows.net;Database=<db>;Authentication=Active Directory Default;Encrypt=True",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 2, 2, 6),
        };
        var sqlPanel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        sqlPanel.Controls.Add(_connectionStringBox);
        sqlPanel.Controls.Add(sqlHelper);
        layout.Controls.Add(AppTheme.MakeSectionLabel("Connection string"));
        layout.Controls.Add(sqlPanel);

        // --- Local file section ---
        _localRootBox = new TextBox { Dock = DockStyle.Fill, Width = 380 };
        var browseButton = new Button { Text = "Browse…" };
        AppTheme.StyleButton(browseButton);
        browseButton.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Folder holding the offline registry files (clients.json, components.json, …)",
                ShowNewFolderButton = true,
            };
            if (Directory.Exists(_localRootBox.Text))
                picker.SelectedPath = _localRootBox.Text;
            if (picker.ShowDialog(this) == DialogResult.OK)
                _localRootBox.Text = picker.SelectedPath;
        };
        var localRootRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        localRootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        localRootRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        localRootRow.Controls.Add(_localRootBox, 0, 0);
        localRootRow.Controls.Add(browseButton, 1, 0);
        layout.Controls.Add(AppTheme.MakeSectionLabel("Local registry root folder"));
        layout.Controls.Add(localRootRow);

        // --- Package store section (Option B: shared folder, applies to BOTH modes) ---
        // Where built delta.zips are published so a Deployer on another machine
        // can fetch them. A UNC path (\\fileserver\DeployToolkit\Packages) or a
        // local path. Empty = no store (the .zip lives only on the builder's PC).
        _packageStoreBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Width = 380,
            PlaceholderText = "\\\\fileserver\\DeployToolkit\\Packages (leave empty for local-only)",
        };
        var storeBrowseButton = new Button { Text = "Browse…" };
        AppTheme.StyleButton(storeBrowseButton);
        storeBrowseButton.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Shared package store folder (UNC path or local). Leave empty for local-only.",
                ShowNewFolderButton = true,
            };
            if (Directory.Exists(_packageStoreBox.Text))
                picker.SelectedPath = _packageStoreBox.Text;
            if (picker.ShowDialog(this) == DialogResult.OK)
                _packageStoreBox.Text = picker.SelectedPath;
        };
        var storeRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        storeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        storeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        storeRow.Controls.Add(_packageStoreBox, 0, 0);
        storeRow.Controls.Add(storeBrowseButton, 1, 0);
        layout.Controls.Add(AppTheme.MakeSectionLabel("Package store (shared folder — Option B)"));
        layout.Controls.Add(storeRow);

        // --- Git tag template (auto-tag on deploy) ---
        _gitTagTemplateBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Width = 380,
            PlaceholderText = "deploy-{version}-{date}",
        };
        var tagHint = new Label
        {
            Text = "Placeholders: {version} {date} (yyyyMMdd) {datetime} (yyyyMMdd-HHmmss) {component}. Leave empty to disable auto-tagging.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 2, 2, 6),
        };
        var tagPanel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        tagPanel.Controls.Add(_gitTagTemplateBox);
        tagPanel.Controls.Add(tagHint);
        layout.Controls.Add(AppTheme.MakeSectionLabel("Git tag template (auto-tag on deploy)"));
        layout.Controls.Add(tagPanel);

        // --- status line (test result) ---
        _statusLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 44,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_statusLabel);

        // --- buttons ---
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 48,
        };
        var okButton = new Button { Text = "OK" };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var testButton = new Button { Text = "Test" };
        AppTheme.StyleButton(okButton);
        AppTheme.StyleButton(cancelButton);
        AppTheme.StyleButton(testButton);
        okButton.Click += (_, _) => OnOk();
        // DialogResult auto-closes only MODAL forms — close explicitly so the
        // embedded (modeless) instance goes away on Cancel too.
        cancelButton.Click += (_, _) => { if (!Modal) Close(); };
        testButton.Click += (_, _) => Guard.FireAndForget(this, "Testing connection…", TestConnectionAsync);
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(testButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = cancelButton;
        AcceptButton = okButton;

        // Load current values.
        _sqlServerRadio.Checked = current.Mode == RegistryMode.SqlServer;
        _localFileRadio.Checked = current.Mode != RegistryMode.SqlServer;
        _connectionStringBox.Text = current.ConnectionString ?? string.Empty;
        _localRootBox.Text = current.LocalRoot ?? string.Empty;
        _packageStoreBox.Text = current.PackageStoreRootPath ?? string.Empty;
        _gitTagTemplateBox.Text = current.GitTagTemplate ?? string.Empty;
    }

    private RegistryMode SelectedMode => _sqlServerRadio.Checked ? RegistryMode.SqlServer : RegistryMode.LocalFile;

    /// <summary>Creates the dialog for embedded (modeless / in-app) use: the
    /// host gets the validated settings through <paramref name="onApplied"/>
    /// when the user presses OK; returning <c>true</c> accepts (the dialog
    /// closes itself), returning <c>false</c> declines (the dialog STAYS OPEN
    /// with the typed settings intact — e.g. because the user refused to
    /// discard unsaved work the connection change would close). Cancel/X
    /// always close. The caller decides where it lives (e.g.
    /// <c>MdiParent</c>) and calls <c>Show()</c>.</summary>
    public static ConnectionDialog CreateEmbedded(
        RegistryConnectionSettings? current,
        Func<RegistryConnectionSettings, bool> onApplied)
        => new(current, onApplied ?? throw new ArgumentNullException(nameof(onApplied)));

    private RegistryConnectionSettings BuildSettings() => new()
    {
        Mode = SelectedMode,
        ConnectionString = NullIfEmpty(_connectionStringBox.Text),
        LocalRoot = NullIfEmpty(_localRootBox.Text),
        PackageStoreRootPath = NullIfEmpty(_packageStoreBox.Text),
        GitTagTemplate = NullIfEmpty(_gitTagTemplateBox.Text),
    };

    private async Task TestConnectionAsync()
    {
        var settings = BuildSettings();
        try
        {
            RegistryConnectionFactory.Validate(settings);
        }
        catch (ArgumentException ex)
        {
            AppTheme.Error(this, ex.Message, "Cannot test connection");
            return;
        }

        _statusLabel.ForeColor = Color.DimGray;
        _statusLabel.Text = "Testing…";

        try
        {
            var store = await RegistryConnectionFactory.CreateOpenAsync(settings);
            try
            {
                var clients = await store.GetAllClientsAsync();
                _statusLabel.ForeColor = Color.ForestGreen;
                _statusLabel.Text = $"Connection OK — registry reachable, {clients.Count} client(s) on record.";
            }
            finally
            {
                (store as IDisposable)?.Dispose(); // store implementations currently hold no unmanaged state
            }
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = $"Connection failed: {ex.Message}";
        }
    }

    private void OnOk()
    {
        var settings = BuildSettings();
        try
        {
            RegistryConnectionFactory.Validate(settings);
        }
        catch (ArgumentException ex)
        {
            AppTheme.Error(this, ex.Message, "Incomplete connection settings");
            return; // keep the dialog open
        }

        if (_onApplied is { } apply)
        {
            // Embedded mode: hand the settings to the host (it persists and
            // reconnects) and close — there is no ShowDialog to return to.
            // A declined apply keeps this screen open: the user just refused
            // to discard unsaved work, losing the typed settings on top of
            // that would be a second slap.
            if (!apply(settings))
                return;
            Close();
            return;
        }

        ResultSettings = settings;
        DialogResult = DialogResult.OK;
    }

    private static string? NullIfEmpty(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
