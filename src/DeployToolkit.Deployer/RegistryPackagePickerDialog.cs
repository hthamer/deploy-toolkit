using DeployToolkit.AppKit;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Deployer;

/// <summary>
/// Modal dialog for the Deployer's "Pick from registry…" flow (Option B:
/// shared folder + registry links the package). Lists a component's
/// packages from the registry, newest-first, and on OK returns the selected
/// <see cref="PackageRecord"/> (whose <see cref="PackageRecord.PackageLocation"/>
/// points at the .zip in the shared store). The caller (StageLoadPackage)
/// then downloads/opens that .zip and feeds it into the existing
/// integrity-check → manifest → deploy path — exactly the same as a manually
/// browsed .zip, just without the manual copy.
/// </summary>
internal sealed class RegistryPackagePickerDialog : Form
{
    private readonly IRegistryStore _registry;
    private readonly ComboBox _clientBox;
    private readonly ComboBox _componentBox;
    private readonly DataGridView _grid;
    private readonly Button _okButton;
    private readonly Label _hintLabel;
    private bool _suppressComponentCombo;

    private List<Client> _clients = new();
    private List<DeploymentComponent> _components = new();
    private List<PackageRecord> _packages = new();

    /// <summary>The selected package when the dialog closes with OK;
    /// otherwise null.</summary>
    public PackageRecord? ResultPackage { get; private set; }

    public RegistryPackagePickerDialog(IRegistryStore registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        Text = "Pick a package from the registry";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(720, 460);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // --- client / component pickers ---
        var pickersRow = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill };
        pickersRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        pickersRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pickersRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        pickersRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pickersRow.Controls.Add(new Label { Text = "Client:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 8, 8, 2) }, 0, 0);
        _clientBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _clientBox.SelectedIndexChanged += (_, _) => _ = LoadComponentsAsync();
        pickersRow.Controls.Add(_clientBox, 1, 0);
        pickersRow.Controls.Add(new Label { Text = "Component:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 8, 2) }, 2, 0);
        _componentBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _componentBox.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressComponentCombo) return;
            _ = LoadPackagesAsync();
        };
        pickersRow.Controls.Add(_componentBox, 3, 0);
        layout.Controls.Add(pickersRow);

        // --- packages grid ---
        _grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
        AppTheme.StyleGrid(_grid, readOnly: true);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Version", FillWeight = 15, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", FillWeight = 12, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Created", HeaderText = "Created (UTC)", FillWeight = 18, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Location", HeaderText = "Shared store location", FillWeight = 40, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reachable", HeaderText = "Reachable?", FillWeight = 15, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.SelectionChanged += (_, _) => UpdateOkEnabled();
        _grid.CellDoubleClick += (_, _) => { if (SelectedRowPackage is not null) Accept(); };
        layout.Controls.Add(_grid);

        _hintLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 36,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_hintLabel);

        // --- buttons ---
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 48,
        };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        _okButton = new Button { Text = "OK", Enabled = false };
        AppTheme.StyleButton(cancelButton);
        AppTheme.StyleButton(_okButton);
        _okButton.Click += (_, _) => Accept();
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_okButton);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        CancelButton = cancelButton;
        AcceptButton = _okButton;

        _ = LoadClientsAsync();
    }

    private PackageRecord? SelectedRowPackage
    {
        get
        {
            if (_grid.CurrentRow is null) return null;
            var version = _grid.CurrentRow.Cells["Version"].Value as string;
            var status = _grid.CurrentRow.Cells["Status"].Value as string;
            return _packages.FirstOrDefault(p => p.Version == version
                && p.Status.ToString() == status);
        }
    }

    private void UpdateOkEnabled()
    {
        _okButton.Enabled = SelectedRowPackage is not null;
    }

    private void Accept()
    {
        if (SelectedRowPackage is { } pkg)
        {
            ResultPackage = pkg;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private async Task LoadClientsAsync()
    {
        try
        {
            _clients = (await _registry.GetAllClientsAsync()).ToList();
            _suppressComponentCombo = true;
            _clientBox.Items.Clear();
            foreach (var c in _clients)
                _clientBox.Items.Add(c);
            _suppressComponentCombo = false;
            if (_clients.Count > 0)
            {
                _clientBox.SelectedIndex = 0;
                await LoadComponentsAsync(); // load components for the first client
            }
            else
            {
                _hintLabel.Text = "No clients in the registry. Build a package first (Packager).";
            }
        }
        catch (Exception ex)
        {
            _hintLabel.ForeColor = Color.Firebrick;
            _hintLabel.Text = $"Could not load clients: {ex.Message}";
        }
    }

    private async Task LoadComponentsAsync()
    {
        if (_suppressComponentCombo) return;
        if (_clientBox.SelectedItem is not Client client) return;

        try
        {
            _components = (await _registry.GetComponentsForClientAsync(client.ClientId)).ToList();
            _componentBox.Items.Clear();
            foreach (var c in _components)
                _componentBox.Items.Add(c);
            if (_components.Count > 0)
            {
                _componentBox.SelectedIndex = 0;
                await LoadPackagesAsync();
            }
            else
            {
                _packages.Clear();
                _grid.Rows.Clear();
                _hintLabel.Text = "No components for this client.";
            }
        }
        catch (Exception ex)
        {
            _hintLabel.ForeColor = Color.Firebrick;
            _hintLabel.Text = $"Could not load components: {ex.Message}";
        }
    }

    private async Task LoadPackagesAsync()
    {
        if (_componentBox.SelectedItem is not DeploymentComponent component) return;

        try
        {
            _packages = (await _registry.GetPackagesForComponentAsync(component.ComponentId)).ToList();
            _grid.Rows.Clear();
            foreach (var p in _packages)
            {
                var reachable = string.IsNullOrEmpty(p.PackageLocation)
                    ? "— (local only)"
                    : (File.Exists(p.PackageLocation) ? "yes" : "no");
                _grid.Rows.Add(
                    p.Version,
                    p.Status.ToString(),
                    p.CreatedUtc.UtcDateTime.ToString("u"),
                    p.PackageLocation ?? "(not uploaded)",
                    reachable);
            }

            _hintLabel.ForeColor = Color.DimGray;
            _hintLabel.Text = _packages.Count == 0
                ? "No packages for this component yet."
                : $"{_packages.Count} package(s). Double-click a row (or pick + OK) to load it. " +
                  "Rows marked 'no' or '—' weren't uploaded to the shared store — copy the .zip by hand.";
        }
        catch (Exception ex)
        {
            _hintLabel.ForeColor = Color.Firebrick;
            _hintLabel.Text = $"Could not load packages: {ex.Message}";
        }
    }
}
