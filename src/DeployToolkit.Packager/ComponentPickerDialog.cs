using DeployToolkit.AppKit;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;

namespace DeployToolkit.Packager;

/// <summary>
/// Shown when <see cref="ComponentNotResolvedException"/> fires — the first
/// time a folder is packaged. Three routes (plan §5/§6):
/// 1. pick an existing client + component,
/// 2. create a new component for an existing client (field set via AppKit's
///    <see cref="ComponentEditorDialog"/>),
/// 3. create a new client AND component in one go (inline mini-form).
/// When the dialog closes with OK, <see cref="ResolvedComponent"/> is fully
/// persisted in the registry and the folder→component mapping is registered
/// (path 3 maps through <c>CreateClientAndComponentAsync</c>; paths 1–2
/// through <c>RegisterFolderMappingAsync</c>).
/// </summary>
public sealed class ComponentPickerDialog : Form
{
    private readonly IRegistryStore _registry;
    private readonly PackageBuilder _builder;
    private readonly string _folderPath;

    private readonly RadioButton _useExistingRadio;
    private readonly Panel _existingPanel;
    private ComboBox _existingClientCombo = null!;
    private ComboBox _existingComponentCombo = null!;
    private Label _existingSummary = null!;

    private readonly RadioButton _newComponentRadio;
    private readonly Panel _newComponentPanel;
    private ComboBox _newComponentClientCombo = null!;
    private Button _configureButton = null!;
    private Label _newComponentSummary = null!;

    private readonly RadioButton _newClientRadio;
    private readonly Panel _newClientPanel;
    private TextBox _clientNameBox = null!;
    private TextBox _componentNameBox = null!;
    private ComboBox _targetTypeBox = null!;
    private TextBox _targetFrameworkBox = null!;
    private CheckBox _selfContainedBox = null!;

    private DeploymentComponent? _pendingComponent;

    /// <summary>Suppresses the client combo's change handler during
    /// programmatic fills — otherwise assigning SelectedIndex mid-fill fired
    /// a NESTED guarded load ("Loading components…" inside "Loading
    /// clients…"), whose stacked busy dialogs corrupted the owner's enabled
    /// state (the form was left permanently disabled).</summary>
    private bool _suppressExistingSelection;

    /// <summary>The persisted component after OK; null while open/when cancelled.</summary>
    public DeploymentComponent? ResolvedComponent { get; private set; }

    public ComponentPickerDialog(IRegistryStore registry, PackageBuilder builder, string folderPath)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _folderPath = folderPath ?? throw new ArgumentNullException(nameof(folderPath));

        Text = "Resolve component for folder";
        AppTheme.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(720, 660);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var folderLabel = new Label
        {
            Text = $"Folder: {_folderPath}",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 0, 2, 8),
        };
        layout.Controls.Add(folderLabel);

        // ---- route 1: existing component ----
        _useExistingRadio = new RadioButton
        {
            Text = "Use an existing component",
            AutoSize = true,
        };
        _useExistingRadio.CheckedChanged += (_, _) => UpdateSectionStates();
        layout.Controls.Add(_useExistingRadio);

        var existingPanel = MakeIndentedPanel();
        existingPanel.Controls.Add(BuildExistingSection());
        _existingPanel = existingPanel;
        layout.Controls.Add(existingPanel);

        // ---- route 2: new component for an existing client ----
        _newComponentRadio = new RadioButton
        {
            Text = "Create a new component for an existing client",
            AutoSize = true,
        };
        _newComponentRadio.CheckedChanged += (_, _) => UpdateSectionStates();
        layout.Controls.Add(_newComponentRadio);

        var newComponentPanel = MakeIndentedPanel();
        newComponentPanel.Controls.Add(BuildNewComponentSection());
        _newComponentPanel = newComponentPanel;
        layout.Controls.Add(newComponentPanel);

        // ---- route 3: new client + component ----
        _newClientRadio = new RadioButton
        {
            Text = "Create a new client and component (maps this folder automatically)",
            AutoSize = true,
        };
        _newClientRadio.CheckedChanged += (_, _) => UpdateSectionStates();
        layout.Controls.Add(_newClientRadio);

        var newClientPanel = MakeIndentedPanel();
        newClientPanel.Controls.Add(BuildNewClientSection());
        _newClientPanel = newClientPanel;
        layout.Controls.Add(newClientPanel);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 48,
        };
        var okButton = new Button { Text = "OK" };
        AppTheme.StyleButton(okButton);
        okButton.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold);
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        AppTheme.StyleButton(cancelButton);
        okButton.Click += (_, _) => Guard.FireAndForget(this, "Registering component…", OnOkAsync);
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = cancelButton;

        _useExistingRadio.Checked = true;
        UpdateSectionStates();

        Load += (_, _) => _ = LoadClientsAsync();
    }

    // ---------------------------------------------------------------
    // Section builders

    private Control BuildExistingSection()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;

        _existingClientCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        _existingClientCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressExistingSelection)
                _ = LoadComponentsAsync();
        };
        AddRow(table, ref row, "Client:", _existingClientCombo);

        _existingComponentCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        _existingComponentCombo.SelectedIndexChanged += (_, _) => UpdateExistingSummary();
        AddRow(table, ref row, "Component:", _existingComponentCombo);

        _existingSummary = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 56,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        table.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, row);
        table.Controls.Add(_existingSummary, 1, row);
        row++;

        return table;
    }

    private Control BuildNewComponentSection()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;

        _newComponentClientCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        AddRow(table, ref row, "Client:", _newComponentClientCombo);

        _configureButton = new Button { Text = "Configure component…" };
        AppTheme.StyleButton(_configureButton);
        _configureButton.Click += (_, _) => ConfigureComponent();
        AddRow(table, ref row, string.Empty, _configureButton);

        _newComponentSummary = new Label
        {
            Text = "Not configured yet.",
            AutoSize = false,
            Height = 40,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        table.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, row);
        table.Controls.Add(_newComponentSummary, 1, row);
        row++;

        return table;
    }

    private Control BuildNewClientSection()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;

        _clientNameBox = new TextBox { Width = 320, PlaceholderText = "e.g. ClientA" };
        AddRow(table, ref row, "Client name:", _clientNameBox);

        _componentNameBox = new TextBox { Width = 320, PlaceholderText = "e.g. CMS" };
        AddRow(table, ref row, "Component name:", _componentNameBox);

        _targetTypeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        foreach (var type in Enum.GetValues<TargetType>())
            _targetTypeBox.Items.Add(type);
        _targetTypeBox.SelectedIndex = 0;
        AddRow(table, ref row, "Target type:", _targetTypeBox);

        _targetFrameworkBox = new TextBox { Width = 200, PlaceholderText = "net8.0 / net48" };
        AddRow(table, ref row, "Target framework:", _targetFrameworkBox);

        _selfContainedBox = new CheckBox { Text = "Self-contained deployment", AutoSize = true };
        AddRow(table, ref row, string.Empty, _selfContainedBox);

        return table;
    }

    private static Panel MakeIndentedPanel() => new()
    {
        Height = 150,
        Width = 640,
        Padding = new Padding(0, 2, 0, 8),
        AutoSize = true,
        Margin = new Padding(24, 0, 0, 8), // indent under its radio
    };

    private static void AddRow(TableLayoutPanel table, ref int row, string label, Control control)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 4, 8, 2),
        }, 0, row);
        control.Margin = new Padding(2, 2, 12, 4);
        table.Controls.Add(control, 1, row);
        row++;
    }

    // ---------------------------------------------------------------
    // Data loading

    private async Task LoadClientsAsync()
    {
        // ONE guarded pass for the whole initial load: filling the client
        // combos and loading the first client's components must not nest a
        // second Guard (see _suppressExistingSelection).
        await Guard.RunAsync(this, "Loading clients…", async () =>
        {
            var clients = await _registry.GetAllClientsAsync();

            _suppressExistingSelection = true;
            try
            {
                _existingClientCombo.DisplayMember = nameof(Client.Name);
                _existingClientCombo.Items.Clear();
                foreach (var client in clients)
                    _existingClientCombo.Items.Add(client);
                if (clients.Count > 0)
                    _existingClientCombo.SelectedIndex = 0;

                _newComponentClientCombo.DisplayMember = nameof(Client.Name);
                _newComponentClientCombo.Items.Clear();
                foreach (var client in clients)
                    _newComponentClientCombo.Items.Add(client);
                if (clients.Count > 0)
                    _newComponentClientCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressExistingSelection = false;
            }

            if (_existingClientCombo.SelectedItem is Client first)
            {
                var components = await _registry.GetComponentsForClientAsync(first.ClientId);
                FillComponentCombo(components);
            }
        });
    }

    private async Task LoadComponentsAsync()
    {
        if (_existingClientCombo.SelectedItem is not Client client)
            return;

        await Guard.RunAsync(this, "Loading components…", async () =>
        {
            var components = await _registry.GetComponentsForClientAsync(client.ClientId);
            FillComponentCombo(components);
        });
    }

    private void FillComponentCombo(IReadOnlyList<DeploymentComponent> components)
    {
        _existingComponentCombo.DisplayMember = nameof(DeploymentComponent.Name);
        _existingComponentCombo.Items.Clear();
        foreach (var component in components)
            _existingComponentCombo.Items.Add(component);
        if (components.Count > 0)
            _existingComponentCombo.SelectedIndex = 0; // fires the summary update below
        else
            _existingSummary.Text = "This client has no components yet.";
    }

    private void UpdateExistingSummary()
    {
        if (_existingComponentCombo.SelectedItem is not DeploymentComponent component)
        {
            _existingSummary.Text = string.Empty;
            return;
        }

        _existingSummary.Text = DescribeComponent(component);
    }

    private void ConfigureComponent()
    {
        if (_newComponentClientCombo.SelectedItem is not Client client)
        {
            AppTheme.Error(this, "Pick a client first.");
            return;
        }

        using var editor = new ComponentEditorDialog(client.ClientId);
        if (editor.ShowDialog(this) == DialogResult.OK && editor.ResultComponent is { } component)
        {
            _pendingComponent = component;
            _newComponentSummary.Text = DescribeComponent(component);
        }
    }

    private static string DescribeComponent(DeploymentComponent component) =>
        $"{component.TargetType}    {component.TargetFramework}    self-contained: {(component.IsSelfContained ? "yes" : "no")}\n" +
        $"Health check: {component.HealthCheckUrl ?? "(none)"}";

    private void UpdateSectionStates()
    {
        _existingPanel.Enabled = _useExistingRadio.Checked;
        _newComponentPanel.Enabled = _newComponentRadio.Checked;
        _newClientPanel.Enabled = _newClientRadio.Checked;
    }

    // ---------------------------------------------------------------
    // OK — persist + map the folder

    private async Task OnOkAsync()
    {
        try
        {
            if (_useExistingRadio.Checked)
            {
                if (_existingClientCombo.SelectedItem is not Client client)
                {
                    AppTheme.Error(this, "Pick a client.");
                    return;
                }

                if (_existingComponentCombo.SelectedItem is not DeploymentComponent component)
                {
                    AppTheme.Error(this, "Pick a component for this client.");
                    return;
                }

                await _builder.RegisterFolderMappingAsync(_folderPath, component.ComponentId);
                ResolvedComponent = component;
            }
            else if (_newComponentRadio.Checked)
            {
                if (_newComponentClientCombo.SelectedItem is not Client client)
                {
                    AppTheme.Error(this, "Pick a client.");
                    return;
                }

                if (_pendingComponent is null)
                {
                    AppTheme.Error(this, "Configure the new component first (\"Configure component…\").");
                    return;
                }

                DeploymentComponent created = await _registry.CreateComponentAsync(_pendingComponent);
                await _builder.RegisterFolderMappingAsync(_folderPath, created.ComponentId);
                ResolvedComponent = created;
            }
            else
            {
                var clientName = _clientNameBox.Text.Trim();
                var componentName = _componentNameBox.Text.Trim();
                var targetFramework = _targetFrameworkBox.Text.Trim();

                if (clientName.Length == 0)
                {
                    AppTheme.Error(this, "Client name is required.");
                    return;
                }
                if (componentName.Length == 0)
                {
                    AppTheme.Error(this, "Component name is required.");
                    return;
                }
                if (targetFramework.Length == 0)
                {
                    AppTheme.Error(this, "Target framework is required (e.g. net8.0 or net48).");
                    return;
                }

                ResolvedComponent = await _builder.CreateClientAndComponentAsync(
                    _folderPath,
                    clientName,
                    componentName,
                    _targetTypeBox.SelectedItem is TargetType type ? type : TargetType.IisLocal,
                    targetFramework,
                    _selfContainedBox.Checked);
            }

            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            // deliberate cancellation — not an error
        }
        catch (ArgumentException ex)
        {
            AppTheme.Error(this, ex.Message, "Cannot create component");
        }
        catch (InvalidOperationException ex)
        {
            AppTheme.Error(this, ex.Message, "Cannot create component");
        }
        // Everything else escapes into Guard.FireAndForget's themed error
        // dialog; the dialog stays open so the user can retry.
    }
}
