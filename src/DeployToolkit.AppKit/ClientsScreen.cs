using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;

namespace DeployToolkit.AppKit;

/// <summary>
/// THE Clients screen (plan §19), built as two full-page views (user
/// feedback: the old split layout starved the client grid):
/// <list type="bullet">
/// <item><b>List page</b> — the client grid gets the whole window, with
/// in-memory search; double-click / Enter / "Open details…" opens a client.</item>
/// <item><b>Detail page</b> — the full 10-field client-profile editor plus
/// the per-client "Components &amp; packages" area, wired to the package
/// lifecycle contract (mark deployed / abandon / supersede / re-open /
/// two-step delete). Everything is visible and modifiable in one place.</item>
/// </list>
///
/// All registry traffic goes through <see cref="Guard"/> (busy overlay +
/// exception-to-dialog), and every mutation reloads the affected grid. The
/// screen is resilient to an unreachable registry: initial-load failures show
/// a friendly error with a "change connection" hint instead of crashing.
///
/// The caller owns connection management: it passes an open
/// <see cref="IRegistryStore"/> in (create it via
/// <see cref="RegistryConnectionFactory"/> and let the user pick via
/// <see cref="ConnectionDialog"/> when creation fails).
///
/// As an MDI child of the Packager shell it implements
/// <see cref="IGuardedCloseScreen"/>: unsaved profile edits in the detail
/// page are detected (dirty tracking) and the shell asks before switching
/// this screen away; the form's own close path (X / app exit) prompts too.
/// </summary>
public sealed class ClientsScreen : Form, IGuardedCloseScreen
{
    private static readonly string[] InfrastructureChoices = { "(none)", nameof(ManagedBy.Boxon), nameof(ManagedBy.Client) };

    private readonly IRegistryStore _registry;

    // Control fields are created in the Build* methods called from the
    // constructor; `= null!` only satisfies the nullable analysis — every
    // field below is assigned before the form is shown.

    // ----- left panel: client list -----
    private TextBox _searchBox = null!;
    private DataGridView _clientsGrid = null!;
    private Button _refreshButton = null!;
    private List<Client> _clients = new();
    private bool _suppressClientSelection;

    // ----- right panel: client profile editor -----
    private TextBox _nameBox = null!;
    private TextBox _contactPhoneBox = null!;
    private TextBox _contactEmailBox = null!;
    private TextBox _gitRepositoryUrlBox = null!;
    private TextBox _deploymentBranchBox = null!;
    private ComboBox _deploymentTypeBox = null!;
    private TextBox _targetRuntimeBox = null!;
    private TextBox _additionalPublishOptionsBox = null!;
    private CheckBox _hasAmcBox = null!;
    private DateTimePicker _amcExpiryPicker = null!;
    private ComboBox _infrastructureBox = null!;
    private TextBox _hostingAccountBox = null!;
    private TextBox _notesBox = null!;
    private Button _saveButton = null!;
    private Button _deleteButton = null!;
    private Label _statusLabel = null!;
    private readonly System.Windows.Forms.Timer _statusTimer;

    // ----- right panel bottom: components & packages -----
    private DataGridView _componentsGrid = null!;
    private DataGridView _packagesGrid = null!;
    private Button _addComponentButton = null!;
    private Button _editComponentButton = null!;
    private Button _deleteComponentButton = null!;
    private ComboBox _packagesComponentBox = null!;
    private List<DeploymentComponent> _clientComponents = new();
    private bool _suppressPackagesCombo;
    private int _childrenLoadId;
    private int _packagesLoadId;

    /// <summary>Client id whose components/packages are currently displayed —
    /// used to skip redundant reloads (every search keystroke used to fire
    /// one, flashing the busy dialog while typing).</summary>
    private string? _childrenLoadedForClientId;

    private Client? _selected;

    // ----- unsaved-edit tracking (profile editor on the detail page) -----
    // True while the user closes this screen despite pending edits; set
    // only by the consented-close path. Dirty-ness itself is COMPUTED
    // (EditorDiffersFromLoaded) instead of event-tracked.
    private bool _closingWithConsent;

    // ----- two full-page views (list / detail) -----
    private Panel _listPage = null!;
    private Panel _detailPage = null!;
    private Label _detailTitle = null!;
    private Button _backButton = null!;
    private Button _openButton = null!;

    public ClientsScreen(IRegistryStore registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        Text = "Clients — DeployToolkit";
        AppTheme.Apply(this);
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterParent;
        WindowState = FormWindowState.Maximized;

        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _statusLabel.Text = string.Empty; };

        // Two full-page views instead of a cramped split layout (user
        // feedback): the list page gives the grid the whole window; the
        // detail page shows and edits the complete profile plus the
        // components & packages area. Only one page is Visible at a time.
        BuildListPage();
        BuildDetailPage();

        Controls.Add(_detailPage);
        Controls.Add(_listPage);

        Load += async (_, _) => await InitialLoadAsync();
        FormClosing += GuardClose;
        FormClosed += (_, _) => _statusTimer.Dispose();
    }

    /// <inheritdoc />
    /// <remarks>Computed by comparing the editor against the loaded client
    /// (not by tracking change events): a field wired tomorrow can't be
    /// missed, and DateTimePicker's check-toggle (which raises no event) is
    /// covered for free.</remarks>
    public bool HasUnsavedWork => EditorDiffersFromLoaded();

    /// <inheritdoc />
    public string UnsavedWorkDescription =>
        $"the client editor has unsaved changes ({(_selected is { } client ? $"client '{client.Name}'" : "a new client")})";

    /// <inheritdoc />
    public void CloseWithoutPrompt()
    {
        // The shell confirmed with the user — close without re-prompting.
        _closingWithConsent = true;
        Close();
    }

    // ==============================================================
    // Page navigation

    /// <summary>Shows the client-list page.</summary>
    private void ShowListPage()
    {
        _detailPage.Visible = false;
        _listPage.Visible = true;
        _searchBox.Focus();
    }

    /// <summary>Back button: leaving the detail page with unsaved edits would
    /// discard them silently — confirm first.</summary>
    private void TryShowListPage()
    {
        if (HasUnsavedWork
            && AppTheme.Confirm(this,
                "The client editor has unsaved changes.\n\nGo back to the list and discard them?",
                "Unsaved changes") != DialogResult.Yes)
        {
            return; // stay on the detail page
        }

        ShowListPage();
    }

    // ==============================================================
    // Unsaved-edit detection + close guard (IGuardedCloseScreen)

    /// <summary>Compares the editor fields against the loaded client (or the
    /// empty "new client" form): true when Save would persist something
    /// different. <see cref="BuildPublishConfigurationJson"/> normalizes the
    /// publish-configuration fields exactly like the save path, so "equivalent
    /// JSON" never counts as a difference.</summary>
    private bool EditorDiffersFromLoaded()
    {
        var client = _selected;

        if (TextDiffers(_nameBox, client?.Name)) return true;
        if (TextDiffers(_contactPhoneBox, client?.ContactPhone)) return true;
        if (TextDiffers(_contactEmailBox, client?.ContactEmail)) return true;
        if (TextDiffers(_gitRepositoryUrlBox, client?.GitRepositoryUrl)) return true;
        if (TextDiffers(_deploymentBranchBox, client?.DeploymentBranch)) return true;
        if (TextDiffers(_hostingAccountBox, client?.HostingAccountManagedBy)) return true;
        if (TextDiffers(_notesBox, client?.Notes)) return true;

        if (BuildPublishConfigurationJson() != client?.PublishConfigurationJson)
            return true;

        if (_hasAmcBox.Checked != (client?.HasAmc ?? false))
            return true;

        var expiry = _amcExpiryPicker.Checked
            ? DateOnly.FromDateTime(_amcExpiryPicker.Value)
            : (DateOnly?)null;
        if (expiry != client?.AmcExpiryDate)
            return true;

        var infrastructure = SelectedInfrastructure;
        var loadedInfrastructure = client?.InfrastructureManagedBy;
        if (infrastructure != loadedInfrastructure)
            return true;

        return false;
    }

    /// <summary>Compares an editor box against a stored (null-able) value with
    /// the same trim/empty semantics the save path applies.</summary>
    private static bool TextDiffers(TextBox box, string? stored)
    {
        var text = box.Text.Trim();
        var normalized = string.IsNullOrEmpty(stored) ? string.Empty : stored.Trim();
        return !string.Equals(text, normalized, StringComparison.Ordinal);
    }

    /// <summary>Close guard (X button / shell close / app exit): unsaved
    /// profile edits ask before the form goes away. Hard OS closes are never
    /// blocked.</summary>
    private void GuardClose(object? sender, FormClosingEventArgs e)
    {
        if (e.Cancel || _closingWithConsent || !HasUnsavedWork)
            return;

        // Never fight a hard OS/task-manager close.
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
            return;

        if (AppTheme.Confirm(this,
                "The client editor has unsaved changes.\n\nClose the Clients screen and discard them?",
                "Unsaved changes") == DialogResult.Yes)
        {
            _closingWithConsent = true; // consented — don't re-ask if close re-enters
            return;
        }

        e.Cancel = true; // keep the screen (cancels a shell/app close too)
    }

    /// <summary>Shows the full-page client editor for the current selection
    /// (or the "new client" form when <see cref="_selected"/> is null).</summary>
    private void ShowDetailPage()
    {
        _detailTitle.Text = _selected is { } client
            ? $"{client.Name} — client details"
            : "New client";
        _deleteButton.Enabled = _selected is not null;
        _listPage.Visible = false;
        _detailPage.Visible = true;
    }

    private void OpenSelectedClient()
    {
        if (_clientsGrid.CurrentRow?.DataBoundItem is ClientRow row)
        {
            _selected = row.Client;
            FillDetailsForm(row.Client);
            ShowDetailPage();
            RequestChildrenReload(force: true); // opening the detail page always refreshes
        }
        else
        {
            AppTheme.Error(this, "Select a client first, or create a new one.", "Open client");
        }
    }

    /// <summary>Reloads the components/packages for the current selection,
    /// but only when that selection actually changed (or when forced). The
    /// generation guard inside the reload protects against overlapping runs.</summary>
    private void RequestChildrenReload(bool force = false)
    {
        var clientId = _selected?.ClientId;
        if (!force && clientId is not null && clientId == _childrenLoadedForClientId)
            return;

        _childrenLoadedForClientId = clientId;
        Guard.FireAndForget(this, "Loading client components…", ReloadClientChildrenAsync);
    }

    // ==============================================================
    // Layout construction: list page

    private void BuildListPage()
    {
        _searchBox = new TextBox { Width = 280 };
        _searchBox.TextChanged += (_, _) => ApplySearchFilter(preferredClientId: _selected?.ClientId);

        _refreshButton = new Button { Text = "Refresh" };
        AppTheme.StyleButton(_refreshButton);
        _refreshButton.Click += (_, _) => Guard.FireAndForget(this, "Reloading clients…",
            async () => await ReloadClientsKeepingSelectionAsync());

        _openButton = new Button { Text = "Open details…" };
        AppTheme.StyleButton(_openButton);
        _openButton.Click += (_, _) => OpenSelectedClient();

        var newButton = new Button { Text = "New client…" };
        AppTheme.StyleButton(newButton);
        newButton.Click += OnNewClick;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10, 10, 10, 6),
            Height = 52,
            WrapContents = false,
        };
        toolbar.Controls.Add(new Label { Text = "Clients:", AutoSize = true, Margin = new Padding(3, 9, 6, 0) });
        toolbar.Controls.Add(_searchBox);
        toolbar.Controls.Add(_refreshButton);
        toolbar.Controls.Add(_openButton);
        toolbar.Controls.Add(newButton);
        toolbar.Controls.Add(new Label
        {
            Text = "Double-click a client (or press Enter) to view and modify all details.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(10, 9, 3, 0),
        });

        _clientsGrid = new DataGridView { Dock = DockStyle.Fill };
        AppTheme.StyleGrid(_clientsGrid);
        _clientsGrid.Columns.Add(Column("Name", 22));
        _clientsGrid.Columns.Add(Column("Phone", 12));
        _clientsGrid.Columns.Add(Column("Email", 16));
        _clientsGrid.Columns.Add(Column("Has AMC", 7));
        _clientsGrid.Columns.Add(Column("AMC expiry", 10));
        _clientsGrid.Columns.Add(Column("Infra managed by", 10));
        _clientsGrid.Columns.Add(Column("Hosting account by", 12));
        _clientsGrid.Columns.Add(Column("Git repo", 16));
        _clientsGrid.Columns.Add(Column("Branch", 8));
        _clientsGrid.SelectionChanged += OnClientSelectionChanged;
        _clientsGrid.CellDoubleClick += (_, _) => OpenSelectedClient();
        _clientsGrid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _clientsGrid.CurrentRow is not null)
            {
                e.Handled = true;
                OpenSelectedClient();
            }
        };

        _listPage = new Panel { Dock = DockStyle.Fill };
        _listPage.Controls.Add(_clientsGrid);
        _listPage.Controls.Add(toolbar);
    }

    // ==============================================================
    // Layout construction: detail page

    private void BuildDetailPage()
    {
        _backButton = new Button { Text = "← Back to list" };
        AppTheme.StyleButton(_backButton);
        _backButton.Click += (_, _) => TryShowListPage();

        _saveButton = new Button { Text = "Save" };
        _deleteButton = new Button { Text = "Delete…" };
        AppTheme.StyleButton(_saveButton);
        AppTheme.StyleButton(_deleteButton);
        _saveButton.Click += OnSaveClick;
        _deleteButton.Click += OnDeleteClientClick;

        _statusLabel = new Label { Text = string.Empty, AutoSize = true, ForeColor = Color.ForestGreen, Margin = new Padding(12, 9, 0, 0) };
        _detailTitle = new Label
        {
            Text = "Client details",
            AutoSize = true,
            Font = new Font(AppTheme.FontFamily, 10f, FontStyle.Bold),
            Margin = new Padding(8, 8, 8, 0),
        };

        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10, 8, 10, 8),
            Height = 52,
            WrapContents = false,
        };
        topBar.Controls.Add(_backButton);
        topBar.Controls.Add(_detailTitle);
        topBar.Controls.Add(_saveButton);
        topBar.Controls.Add(_deleteButton);
        topBar.Controls.Add(_statusLabel);

        // Details on top, components & packages below — deterministic
        // percentage rows (no SplitContainer), AutoScroll inside the details
        // panel keeps the form usable on small screens.
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        content.Controls.Add(BuildDetailsPanel(), 0, 0);
        content.Controls.Add(BuildComponentsAndPackagesTabs(), 0, 1);

        _detailPage = new Panel { Dock = DockStyle.Fill, Visible = false };
        _detailPage.Controls.Add(content);
        _detailPage.Controls.Add(topBar);
    }

    // ==============================================================
    // Layout construction: profile editor (detail page, top half)

    private Control BuildDetailsPanel()
    {
        _nameBox = new TextBox();
        _contactPhoneBox = new TextBox();
        _contactEmailBox = new TextBox();
        _gitRepositoryUrlBox = new TextBox { PlaceholderText = "https://dev.azure.com/... or https://github.com/..." };
        _deploymentBranchBox = new TextBox { PlaceholderText = "main" };

        _deploymentTypeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var type in Enum.GetValues<PublishDeploymentType>())
            _deploymentTypeBox.Items.Add(type);
        _deploymentTypeBox.SelectedIndex = 0;

        _targetRuntimeBox = new TextBox { PlaceholderText = "win-x64" };
        _additionalPublishOptionsBox = new TextBox { PlaceholderText = "-p:PublishTrimmed=false --nologo" };
        _hasAmcBox = new CheckBox { Text = "Client has an AMC (annual maintenance contract)", AutoSize = true };
        _amcExpiryPicker = new DateTimePicker { ShowCheckBox = true, Format = DateTimePickerFormat.Short, Width = 140 };
        _infrastructureBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var choice in InfrastructureChoices)
            _infrastructureBox.Items.Add(choice);
        _infrastructureBox.SelectedIndex = 0;
        _hostingAccountBox = new TextBox { PlaceholderText = "e.g. \"Boxon\", \"Client — Mr. Saleh\"" };
        _notesBox = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 64, Dock = DockStyle.Fill };

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(8),
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;

        void AddField(string label, Control control)
        {
            details.Controls.Add(new Label
            { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 8, 2) }, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(2, 2, 12, 6);
            details.Controls.Add(control, 1, row);
            row++;
        }

        void AddSpan(Control control)
        {
            control.Margin = new Padding(2, 2, 12, 6);
            details.Controls.Add(control, 0, row);
            details.SetColumnSpan(control, 2);
            row++;
        }

        AddSpan(AppTheme.MakeSectionLabel("Client profile"));
        AddField("Name (required):", _nameBox);
        AddField("Contact phone:", _contactPhoneBox);
        AddField("Contact email:", _contactEmailBox);
        AddField("Git repository URL:", _gitRepositoryUrlBox);
        AddField("Deployment branch:", _deploymentBranchBox);
        AddSpan(AppTheme.MakeSectionLabel("Deployment configuration"));
        AddField("Deployment type:", _deploymentTypeBox);
        AddField("Target runtime (RID):", _targetRuntimeBox);
        AddField("Additional publish options:", _additionalPublishOptionsBox);
        AddSpan(AppTheme.MakeSectionLabel("Maintenance & hosting"));
        AddField("", _hasAmcBox);
        AddField("AMC expiry date:", _amcExpiryPicker);
        AddField("Infrastructure managed by:", _infrastructureBox);
        AddField("Hosting account managed by:", _hostingAccountBox);
        AddSpan(AppTheme.MakeSectionLabel("Notes"));
        AddSpan(_notesBox);

        return details;
    }

    private Control BuildComponentsAndPackagesTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };

        // --- Components tab ---
        _addComponentButton = new Button { Text = "Add component…" };
        AppTheme.StyleButton(_addComponentButton);
        _addComponentButton.Click += OnAddComponentClick;

        _editComponentButton = new Button { Text = "Edit…", Enabled = false };
        AppTheme.StyleButton(_editComponentButton);
        _editComponentButton.Click += OnEditComponentClick;

        _deleteComponentButton = new Button { Text = "Delete…", Enabled = false };
        AppTheme.StyleButton(_deleteComponentButton);
        _deleteComponentButton.Click += OnDeleteComponentClick;

        var componentsToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6, 6, 6, 4),
            Height = 42,
            WrapContents = false,
        };
        componentsToolbar.Controls.Add(_addComponentButton);
        componentsToolbar.Controls.Add(_editComponentButton);
        componentsToolbar.Controls.Add(_deleteComponentButton);
        componentsToolbar.Controls.Add(new Label
        {
            Text = "Components belong to the selected client; packages are built per component.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(8, 8, 3, 0),
        });

        _componentsGrid = new DataGridView { Dock = DockStyle.Fill };
        AppTheme.StyleGrid(_componentsGrid);
        _componentsGrid.Columns.Add(Column("Name", 14));
        _componentsGrid.Columns.Add(Column("Target type", 10));
        _componentsGrid.Columns.Add(Column("Target framework", 9));
        _componentsGrid.Columns.Add(Column("Self-contained", 8));
        _componentsGrid.Columns.Add(Column("Target detail", 18));
        _componentsGrid.Columns.Add(Column("Health check URL", 12));
        _componentsGrid.Columns.Add(Column("DB connection ref", 10));
        _componentsGrid.SelectionChanged += OnComponentSelectionChanged;
        _componentsGrid.CellDoubleClick += (_, _) => OnEditComponentClick();
        _componentsGrid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && SelectedComponent is not null)
            {
                e.Handled = true;
                OnEditComponentClick();
            }
        };

        var componentsTab = new TabPage("Components");
        componentsTab.Controls.Add(_componentsGrid);
        componentsTab.Controls.Add(componentsToolbar);

        // --- Packages tab ---
        _packagesComponentBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 200,
            DisplayMember = nameof(DeploymentComponent.Name),
        };
        _packagesComponentBox.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressPackagesCombo) return;
            Guard.FireAndForget(this, "Loading packages…", ReloadPackagesAsync);
        };

        var markDeployedButton = new Button { Text = "Mark Deployed…" };
        var markAbandonedButton = new Button { Text = "Mark Abandoned" };
        var markSupersededButton = new Button { Text = "Mark Superseded" };
        var markCreatedButton = new Button { Text = "Mark Created (re-open)" };
        var deletePackageButton = new Button { Text = "Delete…" };
        AppTheme.StyleButton(markDeployedButton);
        AppTheme.StyleButton(markAbandonedButton);
        AppTheme.StyleButton(markSupersededButton);
        AppTheme.StyleButton(markCreatedButton);
        AppTheme.StyleButton(deletePackageButton);
        markDeployedButton.Click += OnMarkDeployedClick;
        markAbandonedButton.Click += (_, _) => OnMarkStatusClick(PackageStatus.Abandoned);
        markSupersededButton.Click += (_, _) => OnMarkStatusClick(PackageStatus.Superseded);
        markCreatedButton.Click += (_, _) => OnMarkStatusClick(PackageStatus.Created);
        deletePackageButton.Click += OnDeletePackageClick;

        var packagesToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6, 6, 6, 4),
            Height = 42,
            WrapContents = false,
        };
        packagesToolbar.Controls.Add(new Label { Text = "Component:", AutoSize = true, Margin = new Padding(3, 8, 3, 0) });
        packagesToolbar.Controls.Add(_packagesComponentBox);
        packagesToolbar.Controls.Add(markDeployedButton);
        packagesToolbar.Controls.Add(markAbandonedButton);
        packagesToolbar.Controls.Add(markSupersededButton);
        packagesToolbar.Controls.Add(markCreatedButton);
        packagesToolbar.Controls.Add(deletePackageButton);

        _packagesGrid = new DataGridView { Dock = DockStyle.Fill };
        AppTheme.StyleGrid(_packagesGrid);
        _packagesGrid.Columns.Add(Column("Version", 12));
        _packagesGrid.Columns.Add(Column("Status", 10));
        _packagesGrid.Columns.Add(Column("Created (UTC)", 13));
        _packagesGrid.Columns.Add(Column("Deployed (UTC)", 13));
        _packagesGrid.Columns.Add(Column("Deployed by", 12));
        _packagesGrid.Columns.Add(Column("Git SHA", 9));
        _packagesGrid.Columns.Add(Column("Package", 8));

        var packagesTab = new TabPage("Packages");
        packagesTab.Controls.Add(_packagesGrid);
        packagesTab.Controls.Add(packagesToolbar);

        tabs.TabPages.Add(componentsTab);
        tabs.TabPages.Add(packagesTab);
        return tabs;
    }

    private static DataGridViewTextBoxColumn ColumnFactory(string header, string property, int fillWeight) => new()
    {
        HeaderText = header,
        DataPropertyName = property,
        FillWeight = fillWeight,
        ReadOnly = true,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    /// <summary>Fill-mode grid column bound to a row-DTO property whose name
    /// equals <paramref name="header"/> (the DTOs below spell their display
    /// strings with exactly these property names).</summary>
    private static DataGridViewTextBoxColumn Column(string header, int fillWeight)
        => ColumnFactory(header, header, fillWeight);

    // ==============================================================
    // Client list

    private async Task InitialLoadAsync()
    {
        try
        {
            await ReloadClientsKeepingSelectionAsync();
        }
        catch (Exception ex)
        {
            if (IsDisposed)
                return; // the screen was closed mid-load — nothing to report to

            // Registry unreachable — never crash the form; the shell/caller
            // owns the "Change connection…" flow (ConnectionDialog).
            _selected = null;
            ClearDetailsForm();
            ClearClientChildren();
            AppTheme.Error(this,
                "Could not load clients from the registry:\n\n" + ex.Message +
                "\n\nUse \"Change connection…\" in the shell (or switch to the local-file fallback) and reopen this screen.",
                "Registry unreachable");
        }
    }

    private async Task ReloadClientsKeepingSelectionAsync()
    {
        _clients = (await _registry.GetAllClientsAsync()).ToList();
        if (IsDisposed)
            return; // closed mid-load — the shell may switch screens under a busy screen
        ApplySearchFilter(preferredClientId: _selected?.ClientId);
    }

    private void ApplySearchFilter(string? preferredClientId)
    {
        var query = _searchBox.Text.Trim();
        IEnumerable<Client> filtered = _clients;
        if (query.Length > 0)
        {
            filtered = _clients.Where(c =>
                (c.Name ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (c.ContactPhone ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (c.ContactEmail ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var rows = filtered.Select(ToRow).ToList();

        _suppressClientSelection = true;
        try
        {
            _clientsGrid.DataSource = rows;
            DataGridViewRow? target = null;
            foreach (DataGridViewRow gridRow in _clientsGrid.Rows)
            {
                if (gridRow.DataBoundItem is not ClientRow candidate) continue;
                if (preferredClientId is null) { target ??= gridRow; }
                else if (candidate.Client.ClientId == preferredClientId) { target = gridRow; break; }
            }
            if (target is not null && target.Cells.Count > 0)
                _clientsGrid.CurrentCell = target.Cells[0];
        }
        finally
        {
            _suppressClientSelection = false;
        }

        LoadSelectedRowIntoForm();
        RequestChildrenReload();
    }

    private void OnClientSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressClientSelection) return;

        var hadSelection = _selected is not null;
        LoadSelectedRowIntoForm();
        if (_selected is null && !hadSelection)
        {
            ClearClientChildren();
            return;
        }
        RequestChildrenReload();
    }

    private void LoadSelectedRowIntoForm()
    {
        if (_clientsGrid.CurrentRow?.DataBoundItem is ClientRow row)
        {
            _selected = row.Client;
            FillDetailsForm(row.Client);
        }
        else
        {
            _selected = null;
            ClearDetailsForm();
        }
    }

    // ==============================================================
    // Client profile editor

    private void OnNewClick(object? sender, EventArgs e)
    {
        _selected = null;
        ClearDetailsForm();
        ClearClientChildren();
        ShowDetailPage();
        _nameBox.Focus();
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var name = _nameBox.Text.Trim();
        if (name.Length == 0)
        {
            AppTheme.Error(this, "Client name is required.", "Cannot save client");
            _nameBox.Focus();
            return;
        }

        Guard.FireAndForget(this, "Saving client…", async () =>
        {
            try
            {
                var notes = NullIfEmpty(_notesBox.Text);
                var existing = _selected;
                if (existing is null)
                {
                    // New client: CreateClientAsync takes name/notes only —
                    // create, then persist the full profile through
                    // UpdateClientAsync below (simplest correct path).
                    existing = await _registry.CreateClientAsync(name, notes);
                }

                var client = new Client
                {
                    ClientId = existing.ClientId,
                    Name = name,
                    Notes = notes,
                    ContactPhone = NullIfEmpty(_contactPhoneBox.Text),
                    ContactEmail = NullIfEmpty(_contactEmailBox.Text),
                    GitRepositoryUrl = NullIfEmpty(_gitRepositoryUrlBox.Text),
                    DeploymentBranch = NullIfEmpty(_deploymentBranchBox.Text),
                    PublishConfigurationJson = BuildPublishConfigurationJson(),
                    HasAmc = _hasAmcBox.Checked,
                    AmcExpiryDate = _amcExpiryPicker.Checked
                        ? DateOnly.FromDateTime(_amcExpiryPicker.Value)
                        : null,
                    InfrastructureManagedBy = SelectedInfrastructure,
                    HostingAccountManagedBy = NullIfEmpty(_hostingAccountBox.Text),
                };

                _selected = await _registry.UpdateClientAsync(client);
                ShowTransientStatus("Saved ✓");
                await ReloadClientsKeepingSelectionAsync();
                ShowDetailPage(); // refresh the title with the saved name
            }
            catch (ArgumentException ex)
            {
                // Validation failure — the form stays as-is for fixing.
                AppTheme.Error(this, ex.Message, "Cannot save client");
            }
            catch (InvalidOperationException ex)
            {
                // Unknown id / duplicate name — already actionable.
                AppTheme.Error(this, ex.Message, "Cannot save client");
            }
        });
    }

    private void OnDeleteClientClick(object? sender, EventArgs e)
    {
        var client = _selected;
        if (client is null)
        {
            AppTheme.Error(this, "Select a client to delete first.", "Delete client");
            return;
        }

        if (AppTheme.Confirm(this,
                $"Delete client '{client.Name}'?\n\nNote: a client that still has components is refused by the registry " +
                "(the audit trail is never cascade-deleted) — delete those packages/components first if this client really should go away.",
                "Delete client") != DialogResult.Yes)
        {
            return;
        }

        Guard.FireAndForget(this, "Deleting client…", async () =>
        {
            try
            {
                await _registry.DeleteClientAsync(client.ClientId);
            }
            catch (InvalidOperationException ex)
            {
                // "client still has components" etc. — message is already actionable.
                AppTheme.Error(this, ex.Message, "Cannot delete client");
                return;
            }

            _selected = null;
            ShowTransientStatus("Deleted ✓");
            await ReloadClientsKeepingSelectionAsync();
            ShowListPage();
        });
    }

    private void FillDetailsForm(Client client)
    {
        _nameBox.Text = client.Name ?? string.Empty;
        _contactPhoneBox.Text = client.ContactPhone ?? string.Empty;
        _contactEmailBox.Text = client.ContactEmail ?? string.Empty;
        _gitRepositoryUrlBox.Text = client.GitRepositoryUrl ?? string.Empty;
        _deploymentBranchBox.Text = client.DeploymentBranch ?? string.Empty;

        var configuration = SafeParsePublishConfiguration(client);
        _deploymentTypeBox.SelectedItem = configuration?.DeploymentType ?? PublishDeploymentType.FrameworkDependent;
        _targetRuntimeBox.Text = configuration?.TargetRuntime ?? string.Empty;
        _additionalPublishOptionsBox.Text = configuration?.AdditionalPublishOptions ?? string.Empty;

        _hasAmcBox.Checked = client.HasAmc;
        // The picker's own checkbox controls null-ness of the date — an AMC
        // expiry is possible (and storable) even without the HasAmc flag.
        _amcExpiryPicker.Checked = client.AmcExpiryDate.HasValue;
        if (client.AmcExpiryDate is { } expiry)
            _amcExpiryPicker.Value = expiry.ToDateTime(TimeOnly.MinValue);
        _infrastructureBox.SelectedIndex = client.InfrastructureManagedBy switch
        {
            ManagedBy.Boxon => 1,
            ManagedBy.Client => 2,
            _ => 0,
        };
        _hostingAccountBox.Text = client.HostingAccountManagedBy ?? string.Empty;
        _notesBox.Text = client.Notes ?? string.Empty;
    }

    private void ClearDetailsForm()
    {
        _nameBox.Text = string.Empty;
        _contactPhoneBox.Text = string.Empty;
        _contactEmailBox.Text = string.Empty;
        _gitRepositoryUrlBox.Text = string.Empty;
        _deploymentBranchBox.Text = string.Empty;
        _deploymentTypeBox.SelectedItem = PublishDeploymentType.FrameworkDependent;
        _targetRuntimeBox.Text = string.Empty;
        _additionalPublishOptionsBox.Text = string.Empty;
        _hasAmcBox.Checked = false;
        _amcExpiryPicker.Checked = false;
        _infrastructureBox.SelectedIndex = 0;
        _hostingAccountBox.Text = string.Empty;
        _notesBox.Text = string.Empty;
    }

    private static PublishConfiguration? SafeParsePublishConfiguration(Client client)
    {
        try
        {
            return client.PublishConfiguration;
        }
        catch (InvalidOperationException)
        {
            // Corrupt stored JSON — show the raw text where possible instead
            // of dying; the save flow will rewrite it on next save.
            return null;
        }
    }

    private string? BuildPublishConfigurationJson()
    {
        var deploymentType = _deploymentTypeBox.SelectedItem is PublishDeploymentType type
            ? type
            : PublishDeploymentType.FrameworkDependent;
        var runtime = NullIfEmpty(_targetRuntimeBox.Text);
        var options = NullIfEmpty(_additionalPublishOptionsBox.Text);

        if (deploymentType == PublishDeploymentType.FrameworkDependent && runtime is null && options is null)
            return null; // "empty → null JSON" — no client-level publish configuration

        return PublishConfigurationSerializer.Serialize(new PublishConfiguration
        {
            DeploymentType = deploymentType,
            TargetRuntime = runtime,
            AdditionalPublishOptions = options,
        });
    }

    private ManagedBy? SelectedInfrastructure => _infrastructureBox.SelectedIndex switch
    {
        1 => ManagedBy.Boxon,
        2 => ManagedBy.Client,
        _ => null,
    };

    private void ShowTransientStatus(string text)
    {
        if (IsDisposed)
            return; // status write after a mid-operation close

        _statusLabel.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ==============================================================
    // Components & packages (bottom-right tabs)

    private async Task ReloadClientChildrenAsync()
    {
        var generation = ++_childrenLoadId;
        var client = _selected;
        if (client is null)
        {
            ClearClientChildren();
            return;
        }

        var components = (await _registry.GetComponentsForClientAsync(client.ClientId)).ToList();
        if (generation != _childrenLoadId) return; // user moved on — drop this reload
        if (IsDisposed) return; // screen closed mid-load

        _clientComponents = components;
        _componentsGrid.DataSource = components.Select(ToComponentRow).ToList();

        _suppressPackagesCombo = true;
        try
        {
            _packagesComponentBox.Items.Clear();
            foreach (var component in components)
                _packagesComponentBox.Items.Add(component);
            _packagesComponentBox.SelectedIndex = components.Count > 0 ? 0 : -1;
        }
        finally
        {
            _suppressPackagesCombo = false;
        }

        await ReloadPackagesAsync();
    }

    private void ClearClientChildren()
    {
        _childrenLoadId++;
        _childrenLoadedForClientId = null;
        _clientComponents = new List<DeploymentComponent>();
        _componentsGrid.DataSource = new List<ComponentRow>();
        _suppressPackagesCombo = true;
        try
        {
            _packagesComponentBox.Items.Clear();
        }
        finally
        {
            _suppressPackagesCombo = false;
        }
        _packagesGrid.DataSource = new List<PackageRow>();
    }

    private async Task ReloadPackagesAsync()
    {
        var generation = ++_packagesLoadId;
        if (_packagesComponentBox.SelectedItem is not DeploymentComponent component)
        {
            _packagesGrid.DataSource = new List<PackageRow>();
            return;
        }

        var packages = await _registry.GetPackagesForComponentAsync(component.ComponentId);
        if (generation != _packagesLoadId) return;
        if (IsDisposed) return; // screen closed mid-load

        _packagesGrid.DataSource = packages.Select(ToPackageRow).ToList();
    }

    private void OnAddComponentClick(object? sender, EventArgs e)
    {
        if (_selected is null)
        {
            AppTheme.Error(this, "Select (or save) a client first — components belong to a client.", "Add component");
            return;
        }

        using var dialog = new ComponentEditorDialog(_selected.ClientId);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ResultComponent is null) return;
        var component = dialog.ResultComponent;

        Guard.FireAndForget(this, "Adding component…", async () =>
        {
            try
            {
                await _registry.CreateComponentAsync(component);
            }
            catch (ArgumentException ex)
            {
                AppTheme.Error(this, ex.Message, "Cannot add component");
                return;
            }
            catch (InvalidOperationException ex)
            {
                AppTheme.Error(this, ex.Message, "Cannot add component");
                return;
            }
            await ReloadClientChildrenAsync();
        });
    }

    /// <summary>The currently selected component in the components grid (the
    /// full <see cref="DeploymentComponent"/> from the in-memory list), or
    /// null when nothing is selected.</summary>
    private DeploymentComponent? SelectedComponent
    {
        get
        {
            if (_componentsGrid.CurrentRow is null)
                return null;
            var name = _componentsGrid.CurrentRow.Cells["Name"]?.Value as string;
            return _clientComponents.FirstOrDefault(c => c.Name == name);
        }
    }

    private void OnComponentSelectionChanged(object? sender, EventArgs e)
    {
        var hasSelection = SelectedComponent is not null;
        _editComponentButton.Enabled = hasSelection;
        _deleteComponentButton.Enabled = hasSelection;
    }

    private void OnEditComponentClick()
    {
        if (_selected is null)
        {
            AppTheme.Error(this, "Select a client first — components belong to a client.", "Edit component");
            return;
        }

        if (SelectedComponent is not { } existing)
            return; // no row selected — the toolbar button is disabled, but double-click can still fire on an empty grid

        using var dialog = new ComponentEditorDialog(_selected.ClientId, existing);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ResultComponent is null) return;
        var updated = dialog.ResultComponent;

        Guard.FireAndForget(this, "Saving component…", async () =>
        {
            try
            {
                await _registry.UpdateComponentAsync(updated);
            }
            catch (InvalidOperationException ex)
            {
                AppTheme.Error(this, ex.Message, "Cannot save component");
                return;
            }
            await ReloadClientChildrenAsync();
        });
    }

    private void OnEditComponentClick(object? sender, EventArgs e) => OnEditComponentClick();

    private void OnDeleteComponentClick(object? sender, EventArgs e)
    {
        if (_selected is null)
            return;

        if (SelectedComponent is not { } existing)
        {
            AppTheme.Error(this, "Select a component to delete first.", "Delete component");
            return;
        }

        if (AppTheme.Confirm(this,
                $"Delete component '{existing.Name}'? This cannot be undone.",
                "Delete component") != DialogResult.Yes)
            return;

        Guard.FireAndForget(this, "Deleting component…", async () =>
        {
            try
            {
                await _registry.DeleteComponentAsync(existing.ComponentId);
            }
            catch (InvalidOperationException ex)
            {
                // The audit-trail rule: a component with packages cannot be
                // deleted. Show the clear error so the user knows to delete
                // the packages first.
                AppTheme.Error(this, ex.Message, "Cannot delete component");
                return;
            }
            await ReloadClientChildrenAsync();
        });
    }

    private PackageRow? SelectedPackage => _packagesGrid.CurrentRow?.DataBoundItem as PackageRow;

    private void RequireSelectedPackage(string action)
    {
        if (SelectedPackage is null)
            AppTheme.Error(this, $"Select a package to {action} first.", "Packages");
    }

    private void OnMarkDeployedClick(object? sender, EventArgs e)
    {
        if (SelectedPackage is not { } row)
        {
            RequireSelectedPackage("mark deployed");
            return;
        }

        using var prompt = new DeployedByPrompt(row.Package.DeployedBy ?? Environment.UserName);
        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        var deployedBy = prompt.UserName;

        if (AppTheme.Confirm(this,
                $"Mark package '{row.Version}' ({ShortId(row.Package.PackageId)}) as DEPLOYED by '{deployedBy}', effective now?",
                "Mark deployed") != DialogResult.Yes)
        {
            return;
        }

        Guard.FireAndForget(this, "Marking package deployed…", async () =>
        {
            await _registry.MarkDeployedAsync(row.Package.PackageId, deployedBy, DateTimeOffset.UtcNow);
            ShowTransientStatus("Marked deployed ✓");
            await ReloadPackagesAsync();
        });
    }

    private void OnMarkStatusClick(PackageStatus status)
    {
        if (SelectedPackage is not { } row)
        {
            RequireSelectedPackage("re-tag");
            return;
        }

        var confirmText = status switch
        {
            PackageStatus.Abandoned =>
                $"Mark package '{row.Version}' ({ShortId(row.Package.PackageId)}) as ABANDONED?\n\n" +
                "An abandoned package can never silently become a diff baseline again (plan §9).",
            PackageStatus.Superseded =>
                $"Mark package '{row.Version}' ({ShortId(row.Package.PackageId)}) as SUPERSEDED?",
            PackageStatus.Created =>
                $"Re-open package '{row.Version}' ({ShortId(row.Package.PackageId)}) — set its status back to CREATED?",
            PackageStatus.Deployed =>
                $"Mark package '{row.Version}' ({ShortId(row.Package.PackageId)}) as DEPLOYED (without recording who/when)?\n\n" +
                "Prefer \"Mark Deployed…\" — it records who deployed and when.",
            _ => $"Set package status of '{row.Version}' to {status}?",
        };

        if (AppTheme.Confirm(this, confirmText, "Change package status") != DialogResult.Yes) return;

        Guard.FireAndForget(this, "Updating package status…", async () =>
        {
            await _registry.MarkStatusAsync(row.Package.PackageId, status);
            ShowTransientStatus($"Status → {status} ✓");
            await ReloadPackagesAsync();
        });
    }

    private void OnDeletePackageClick(object? sender, EventArgs e)
    {
        if (SelectedPackage is not { } row)
        {
            RequireSelectedPackage("delete");
            return;
        }

        if (AppTheme.Confirm(this,
                $"Delete package '{row.Version}' ({ShortId(row.Package.PackageId)})?\n\n" +
                "Deletion is refused while deployment-run records exist for it — in that case you will be asked " +
                "whether to delete the run history too (irreversible).",
                "Delete package") != DialogResult.Yes)
        {
            return;
        }

        Guard.FireAndForget(this, "Deleting package…", async () =>
        {
            try
            {
                await _registry.DeletePackageAsync(row.Package.PackageId, deleteRunHistory: false);
            }
            catch (InvalidOperationException)
            {
                // §19 two-step contract: run history exists → explicit opt-in
                // to delete the package AND its history together.
                if (AppTheme.Confirm(this,
                        "Deployment run history exists for this package.\n\n" +
                        "Delete the package AND its run history? This is irreversible — the manifest audit trail goes with it.",
                        "Delete run history") != DialogResult.Yes)
                {
                    return;
                }

                await _registry.DeletePackageAsync(row.Package.PackageId, deleteRunHistory: true);
            }

            ShowTransientStatus("Deleted ✓");
            await ReloadPackagesAsync();
        });
    }

    // ==============================================================
    // Grid row DTOs (string-shaped for display; hold the live object)

    private static ClientRow ToRow(Client client) => new(
        client,
        client.Name ?? string.Empty,
        client.ContactPhone ?? string.Empty,
        client.ContactEmail ?? string.Empty,
        client.HasAmc ? "Yes" : "No",
        client.AmcExpiryDate?.ToString("yyyy-MM-dd") ?? string.Empty,
        client.InfrastructureManagedBy?.ToString() ?? string.Empty,
        client.HostingAccountManagedBy ?? string.Empty,
        client.GitRepositoryUrl ?? string.Empty,
        client.DeploymentBranch ?? string.Empty);

    private static ComponentRow ToComponentRow(DeploymentComponent component) => new(
        component,
        component.Name,
        component.TargetType.ToString(),
        component.TargetFramework,
        component.IsSelfContained ? "Yes" : "No",
        DescribeTarget(component),
        component.HealthCheckUrl ?? string.Empty,
        component.DbConnectionRef ?? string.Empty);

    private static string DescribeTarget(DeploymentComponent component) => component.TargetType switch
    {
        TargetType.IisLocal => $"{component.IisSiteName ?? "?"}{(string.IsNullOrEmpty(component.IisAppPath) ? string.Empty : " — " + component.IisAppPath)}",
        TargetType.AzureAppService => $"{component.AzureAppServiceName ?? "?"}{(string.IsNullOrEmpty(component.AzureResourceGroup) ? string.Empty : " (rg: " + component.AzureResourceGroup + ")")}",
        TargetType.Plesk => $"{component.PleskHost ?? "?"}{(string.IsNullOrEmpty(component.PleskSiteId) ? string.Empty : " — site " + component.PleskSiteId)}",
        _ => string.Empty,
    };

    private static PackageRow ToPackageRow(PackageRecord package) => new(
        package,
        package.Version,
        package.Status.ToString(),
        package.CreatedUtc.ToString("yyyy-MM-dd HH:mm"),
        package.DeployedUtc?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
        package.DeployedBy ?? string.Empty,
        package.GitCommitSha is { Length: > 0 } sha ? (sha.Length <= 12 ? sha : sha[..12]) : string.Empty,
        ShortId(package.PackageId));

    private static string ShortId(string id) => id.Length <= 8 ? id : id[..8];

    private static string? NullIfEmpty(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private sealed record ClientRow(
        Client Client,
        string Name,
        string Phone,
        string Email,
        string HasAmc,
        string AmcExpiry,
        string InfraManagedBy,
        string HostingManagedBy,
        string GitRepo,
        string Branch);

    private sealed record ComponentRow(
        DeploymentComponent Component,
        string Name,
        string TargetType,
        string TargetFramework,
        string SelfContained,
        string TargetDetail,
        string HealthCheckUrl,
        string DbConnectionRef);

    private sealed record PackageRow(
        PackageRecord Package,
        string Version,
        string Status,
        string CreatedUtc,
        string DeployedUtc,
        string DeployedBy,
        string GitSha,
        string PackageId);

    /// <summary>Tiny one-field modal prompt used by "Mark Deployed…"
    /// (pre-filled with the previous DeployedBy or the current OS user).</summary>
    private sealed class DeployedByPrompt : Form
    {
        private readonly TextBox _userBox;

        public string UserName => _userBox.Text.Trim();

        public DeployedByPrompt(string prefill)
        {
            Text = "Who deployed this package?";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AppTheme.Apply(this);
            Size = new Size(420, 170);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
            layout.Controls.Add(new Label
            {
                Text = "Deployed by (recorded in the registry):",
                AutoSize = true,
                Margin = new Padding(2, 2, 2, 6),
            });
            _userBox = new TextBox { Text = prefill, Dock = DockStyle.Top };
            layout.Controls.Add(_userBox);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(12),
                Height = 48,
            };
            var ok = new Button { Text = "OK" };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            AppTheme.StyleButton(ok);
            AppTheme.StyleButton(cancel);
            ok.Click += (_, _) => DialogResult = DialogResult.OK;
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);

            Controls.Add(layout);
            Controls.Add(buttons);
            CancelButton = cancel;
            AcceptButton = ok;
        }
    }
}
