using DeployToolkit.AppKit;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Packager;

/// <summary>
/// Plan §9/§10 step 2: shown by the wizard when the chosen component has
/// <b>Created</b>-but-never-deployed packages. An undeployed package must
/// never silently become a later diff baseline, so the user resolves each
/// one here — Mark Deployed (if it actually shipped), Abandon (if it was
/// never sent), or Ignore for now (proceed without resolving; the build
/// result will repeat the warning). Rows disappear as they are resolved;
/// <see cref="AllResolved"/> tells the caller whether anything was left.
/// </summary>
public sealed class StalePackagesDialog : Form
{
    private readonly IRegistryStore _registry;
    private readonly BindingSource _binding = new();
    private readonly DataGridView _grid;
    private readonly Button _markDeployedButton;
    private readonly Button _abandonButton;
    private readonly Label _hintLabel;

    public StalePackagesDialog(IRegistryStore registry, IReadOnlyList<PackageRecord> stalePackages)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _binding.DataSource = stalePackages.ToList();

        Text = "Unresolved packages for this component";
        AppTheme.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(640, 460);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = "These packages were built but never confirmed deployed. " +
                   "Resolve them before packaging again — an unresolved Created package can never become a diff baseline.",
            AutoSize = true,
            Margin = new Padding(2, 0, 2, 8),
        };
        layout.Controls.Add(intro, 0, 0);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Version", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CreatedUtc", HeaderText = "Created (UTC)", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "GitSha", HeaderText = "Git SHA", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", ReadOnly = true });
        AppTheme.StyleGrid(_grid, readOnly: true);
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        layout.Controls.Add(_grid, 0, 1);

        _hintLabel = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 4, 2, 4),
        };
        layout.Controls.Add(_hintLabel, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };
        _markDeployedButton = new Button { Text = "Mark Deployed…" };
        _abandonButton = new Button { Text = "Abandon" };
        var ignoreButton = new Button { Text = "Ignore for now", DialogResult = DialogResult.Cancel };
        foreach (var b in new[] { _markDeployedButton, _abandonButton, ignoreButton })
            AppTheme.StyleButton(b);
        _markDeployedButton.Click += async (_, _) => await MarkDeployedAsync();
        _abandonButton.Click += async (_, _) => await AbandonAsync();
        buttons.Controls.Add(_markDeployedButton);
        buttons.Controls.Add(_abandonButton);
        buttons.Controls.Add(ignoreButton);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);
        CancelButton = ignoreButton;

        _grid.DataSource = _binding;
        _grid.DataBindingComplete += (_, _) => UpdateButtons();
    }

    /// <summary>True when no stale packages remain (or there were none).</summary>
    public bool AllResolved => Rows.Count == 0;

    private IReadOnlyList<PackageRecord> Rows => (IReadOnlyList<PackageRecord>)_binding.List;

    private PackageRecord? Selected
    {
        get
        {
            if (_grid.CurrentRow?.Index is not { } index || index < 0 || index >= Rows.Count)
                return null;
            return Rows[index];
        }
    }

    private void UpdateButtons()
    {
        var any = Selected is not null;
        _markDeployedButton.Enabled = any;
        _abandonButton.Enabled = any;
        _hintLabel.Text = Rows.Count == 0
            ? "All resolved — nothing left to decide."
            : Selected is null
                ? "Select a package, then choose how to resolve it."
                : $"Package {Selected!.Version} — {Selected.Status}";
    }

    private async Task MarkDeployedAsync()
    {
        if (Selected is not { } package)
            return;

        using var prompt = new DeployedByPrompt(package.DeployedBy ?? Environment.UserName);
        if (prompt.ShowDialog(this) != DialogResult.OK)
            return;

        var user = prompt.UserName;
        if (user.Length == 0)
        {
            AppTheme.Error(this, "A user name is required — the registry records who confirmed the deployment.");
            return;
        }

        await Guard.RunAsync(this, "Recording deployed status…", async () =>
        {
            await _registry.MarkDeployedAsync(package.PackageId, user, DateTimeOffset.UtcNow);
            RemoveRow(package);
        });
    }

    private async Task AbandonAsync()
    {
        if (Selected is not { } package)
            return;

        var choice = AppTheme.Confirm(this,
            $"Mark package '{package.Version}' as Abandoned?\n\n" +
            "Abandoned packages are excluded from diff baselines and can be deleted later on the Clients screen.");
        if (choice != DialogResult.Yes)
            return;

        await Guard.RunAsync(this, "Marking as abandoned…", async () =>
        {
            await _registry.MarkStatusAsync(package.PackageId, PackageStatus.Abandoned);
            RemoveRow(package);
        });
    }

    private void RemoveRow(PackageRecord package)
    {
        _binding.Remove(package);
        UpdateButtons();
    }

    /// <summary>Tiny one-field modal prompt (same shape as the Clients screen's).</summary>
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
