using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;

namespace DeployToolkit.AppKit;

/// <summary>
/// Modal dialog for ADDING a deployment component to a client (plan §6
/// component model). The dialog only CREATES — <see cref="DeploymentComponent"/>
/// is init-only and the registry store has no component update/delete — so the
/// caller persists the result via <c>IRegistryStore.CreateComponentAsync</c>
/// when the dialog returns OK.
/// </summary>
public sealed class ComponentEditorDialog : Form
{
    private readonly string _clientId;
    private readonly string? _existingComponentId;

    private readonly TextBox _nameBox;
    private readonly ComboBox _targetTypeBox;
    private readonly TextBox _targetFrameworkBox;
    private readonly CheckBox _selfContainedBox;

    // IIS target section
    private readonly GroupBox _iisGroup;
    private readonly TextBox _iisSiteNameBox;
    private readonly TextBox _iisAppPathBox;

    // Azure target section
    private readonly GroupBox _azureGroup;
    private readonly TextBox _azureAppServiceNameBox;
    private readonly TextBox _azureResourceGroupBox;

    // Plesk target section
    private readonly GroupBox _pleskGroup;
    private readonly TextBox _pleskHostBox;
    private readonly TextBox _pleskSiteIdBox;

    private readonly TextBox _healthCheckUrlBox;
    private readonly TextBox _dbConnectionRefBox;

    /// <summary>The built component when the dialog is closed with OK; otherwise null.</summary>
    public DeploymentComponent? ResultComponent { get; private set; }

    /// <summary>
    /// Add-mode constructor: builds a brand-new component (a fresh ComponentId
    /// is generated on OK). The TargetType picker is fully editable.
    /// </summary>
    public ComponentEditorDialog(string clientId) : this(clientId, existing: null) { }

    /// <summary>
    /// Edit-mode constructor: pre-fills every field from <paramref name="existing"/>
    /// and, on OK, returns a rebuilt <see cref="DeploymentComponent"/> that
    /// preserves the existing <c>ComponentId</c> / <c>ClientId</c>. The
    /// TargetType picker is LOCKED (the target kind cannot change after a
    /// component exists — its IIS site / Azure app / Plesk site bindings
    /// depend on it) and shown read-only.
    /// </summary>
    public ComponentEditorDialog(string clientId, DeploymentComponent existing)
        : this(clientId, (DeploymentComponent?)existing) { }

    private ComponentEditorDialog(string clientId, DeploymentComponent? existing)
    {
        _clientId = clientId;
        _existingComponentId = existing?.ComponentId;

        Text = existing is null ? "Add component" : $"Edit component — {existing.Name}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(560, 640);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;

        void AddField(string label, Control control)
        {
            layout.Controls.Add(new Label
            { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 8, 2) }, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(2, 2, 12, 6);
            layout.Controls.Add(control, 1, row);
            row++;
        }

        _nameBox = new TextBox();
        AddField("Component name:", _nameBox);

        _targetTypeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var type in Enum.GetValues<TargetType>())
            _targetTypeBox.Items.Add(type);
        _targetTypeBox.SelectedIndex = 0;
        AddField("Target type:", _targetTypeBox);

        _targetFrameworkBox = new TextBox { PlaceholderText = "net8.0 / net48" };
        AddField("Target framework:", _targetFrameworkBox);

        _selfContainedBox = new CheckBox { Text = "Self-contained deployment" };
        AddField("", _selfContainedBox);

        // --- dynamic target sections (visibility follows TargetType) ---
        _iisSiteNameBox = new TextBox();
        _iisAppPathBox = new TextBox();
        _iisGroup = MakeGroup("IIS target", ("IIS site name:", _iisSiteNameBox), ("Application path:", _iisAppPathBox));
        _azureAppServiceNameBox = new TextBox();
        _azureResourceGroupBox = new TextBox();
        _azureGroup = MakeGroup("Azure App Service target", ("App Service name:", _azureAppServiceNameBox),
            ("Resource group:", _azureResourceGroupBox));
        _pleskHostBox = new TextBox();
        _pleskSiteIdBox = new TextBox();
        _pleskGroup = MakeGroup("Plesk target", ("Plesk host:", _pleskHostBox), ("Site ID:", _pleskSiteIdBox));

        _targetTypeBox.SelectedIndexChanged += (_, _) => UpdateTargetSections();
        _iisGroup.Dock = DockStyle.Fill;
        _azureGroup.Dock = DockStyle.Fill;
        _pleskGroup.Dock = DockStyle.Fill;
        layout.Controls.Add(_iisGroup, 0, row);
        layout.SetColumnSpan(_iisGroup, 2);
        row++;
        layout.Controls.Add(_azureGroup, 0, row);
        layout.SetColumnSpan(_azureGroup, 2);
        row++;
        layout.Controls.Add(_pleskGroup, 0, row);
        layout.SetColumnSpan(_pleskGroup, 2);
        row++;

        _healthCheckUrlBox = new TextBox { PlaceholderText = "https://client.example.com/health" };
        AddField("Health check URL:", _healthCheckUrlBox);

        _dbConnectionRefBox = new TextBox
        { PlaceholderText = "vault://secret-name or a Key Vault URI — never the secret itself" };
        AddField("DB connection ref:", _dbConnectionRefBox);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 48,
        };
        var okButton = new Button { Text = "OK" };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        AppTheme.StyleButton(okButton);
        AppTheme.StyleButton(cancelButton);
        okButton.Click += (_, _) => OnOk();
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = cancelButton;
        AcceptButton = okButton;

        // Edit mode: pre-fill the fields from the existing component and lock
        // the TargetType picker (the target kind can't change after a component
        // exists — its IIS/Azure/Plesk bindings depend on it). Add mode leaves
        // everything empty for the user to fill in.
        if (existing is { } ex)
        {
            _nameBox.Text = ex.Name;
            _targetFrameworkBox.Text = ex.TargetFramework;
            _selfContainedBox.Checked = ex.IsSelfContained;
            _iisSiteNameBox.Text = ex.IisSiteName ?? string.Empty;
            _iisAppPathBox.Text = ex.IisAppPath ?? string.Empty;
            _azureAppServiceNameBox.Text = ex.AzureAppServiceName ?? string.Empty;
            _azureResourceGroupBox.Text = ex.AzureResourceGroup ?? string.Empty;
            _pleskHostBox.Text = ex.PleskHost ?? string.Empty;
            _pleskSiteIdBox.Text = ex.PleskSiteId ?? string.Empty;
            _healthCheckUrlBox.Text = ex.HealthCheckUrl ?? string.Empty;
            _dbConnectionRefBox.Text = ex.DbConnectionRef ?? string.Empty;

            // Select + lock the existing TargetType.
            _targetTypeBox.SelectedItem = ex.TargetType;
            _targetTypeBox.Enabled = false;
        }

        UpdateTargetSections();
    }

    private static GroupBox MakeGroup(string title, params (string Label, TextBox Box)[] fields)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, box) in fields)
        {
            table.Controls.Add(new Label
            { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 8, 2) });
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(2, 2, 12, 6);
            table.Controls.Add(box);
        }
        group.Controls.Add(table);
        return group;
    }

    private void UpdateTargetSections()
    {
        var type = SelectedTargetType;
        _iisGroup.Visible = type == TargetType.IisLocal;
        _azureGroup.Visible = type == TargetType.AzureAppService;
        _pleskGroup.Visible = type == TargetType.Plesk;
    }

    private TargetType SelectedTargetType
        => _targetTypeBox.SelectedItem is TargetType type ? type : TargetType.IisLocal;

    private static string? TextOrNull(TextBox box)
    {
        var text = box.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    private void OnOk()
    {
        var name = _nameBox.Text.Trim();
        if (name.Length == 0)
        {
            AppTheme.Error(this, "Component name is required.");
            return;
        }

        var targetFramework = _targetFrameworkBox.Text.Trim();
        if (targetFramework.Length == 0)
        {
            AppTheme.Error(this, "Target framework is required (e.g. net8.0 or net48).");
            return;
        }

        var type = SelectedTargetType;
        ResultComponent = new DeploymentComponent
        {
            // Edit mode preserves the existing ComponentId; add mode mints a fresh one.
            ComponentId = _existingComponentId ?? Guid.NewGuid().ToString("N"),
            ClientId = _clientId,
            Name = name,
            TargetType = type,
            TargetFramework = targetFramework,
            IsSelfContained = _selfContainedBox.Checked,
            IisSiteName = type == TargetType.IisLocal ? TextOrNull(_iisSiteNameBox) : null,
            IisAppPath = type == TargetType.IisLocal ? TextOrNull(_iisAppPathBox) : null,
            AzureAppServiceName = type == TargetType.AzureAppService ? TextOrNull(_azureAppServiceNameBox) : null,
            AzureResourceGroup = type == TargetType.AzureAppService ? TextOrNull(_azureResourceGroupBox) : null,
            PleskHost = type == TargetType.Plesk ? TextOrNull(_pleskHostBox) : null,
            PleskSiteId = type == TargetType.Plesk ? TextOrNull(_pleskSiteIdBox) : null,
            HealthCheckUrl = TextOrNull(_healthCheckUrlBox),
            DbConnectionRef = TextOrNull(_dbConnectionRefBox),
        };

        DialogResult = DialogResult.OK;
    }
}
