using DeployToolkit.AppKit;
using DeployToolkit.Core.Manifest;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 5 (plan §10 step 6): attach .sql scripts and tag each as Schema or
/// Data (<see cref="DbScriptKind"/>). Scripts are embedded into the package
/// under db/ and run by the Deployer after an explicit confirm — transaction
/// safety is analyzed there (DeployToolkit.Core.Database).
/// </summary>
internal sealed class StepScripts : WizardStep
{
    private readonly DataGridView _grid;
    private readonly Label _countLabel;
    private bool _loadingRows;

    public StepScripts(PackagerWizardForm wizard, PackageDraft draft)
        : base(wizard, draft)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 0, 6),
            WrapContents = false,
        };
        var addButton = new Button { Text = "Add .sql files…" };
        AppTheme.StyleButton(addButton);
        addButton.Click += (_, _) => AddScripts();
        buttons.Controls.Add(addButton);
        layout.Controls.Add(buttons);

        _grid = new DataGridView { Dock = DockStyle.Fill };
        AppTheme.StyleGrid(_grid, readOnly: false);
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "File",
            HeaderText = "File",
            ReadOnly = true,
            FillWeight = 60,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Kind",
            HeaderText = "Kind",
            DataSource = new[] { DbScriptKind.Schema, DbScriptKind.Data },
            ValueType = typeof(DbScriptKind),
            FillWeight = 25,
        });
        var removeColumn = new DataGridViewButtonColumn
        {
            Name = "Remove",
            HeaderText = "Remove",
            Text = "Remove",
            UseColumnTextForButtonValue = true,
            FillWeight = 15,
        };
        _grid.Columns.Add(removeColumn);
        _grid.CellValueChanged += (_, _) => CommitFromGrid();
        _grid.RowsRemoved += (_, _) => CommitFromGrid();
        _grid.CellContentClick += Grid_CellContentClick;
        layout.Controls.Add(_grid);

        _countLabel = new Label
        {
            Text = "No scripts attached.",
            AutoSize = false,
            Height = 24,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_countLabel);

        var note = new Label
        {
            Text = "Scripts are embedded into the package under db/ and run by the Deployer after an explicit " +
                   "confirm — transaction safety is analyzed there (Schema vs Data only tags the intent).",
            AutoSize = false,
            Height = 36,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(note);

        Controls.Add(layout);
    }

    public override string Title => "5. DB scripts";

    public override string Hint =>
        "Optional. Attach .sql files for this release and tag each as Schema or Data.";

    public override bool CanProceed => true;

    public override void OnEnter()
    {
        _grid.Rows.Clear();
        foreach (var script in Draft.DbScripts)
            _grid.Rows.Add(script.File, script.Kind);

        UpdateCountLabel();
    }

    public override void OnLeave() => CommitFromGrid();

    private void AddScripts()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Attach SQL scripts",
            Filter = "SQL scripts (*.sql)|*.sql|All files (*.*)|*.*",
            Multiselect = true,
        };

        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        foreach (var fullPath in picker.FileNames)
        {
            var fileName = Path.GetFileName(fullPath);

            // Same file re-added → refresh the source path silently; a
            // DIFFERENT file reusing an existing name would silently change
            // which bytes get embedded — refuse that instead.
            if (Draft.DbScriptSourcePaths.TryGetValue(fileName, out var existing) &&
                !string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                AppTheme.Error(this,
                    $"A different file named '{fileName}' is already attached. " +
                    "Rename one of them so the embedded script names stay unique.");
                continue;
            }

            Draft.DbScriptSourcePaths[fileName] = fullPath;
        }

        ReloadFromDraft();
    }

    private void Grid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Remove")
            return;

        if (_grid.Rows[e.RowIndex].Cells["File"].Value is string fileName)
            Draft.DbScriptSourcePaths.Remove(fileName);

        _grid.Rows.RemoveAt(e.RowIndex);
        CommitFromGrid();
    }

    /// <summary>Grid is the source of truth — rebuild the draft's script list
    /// from the visible rows (order preserved).</summary>
    private void CommitFromGrid()
    {
        if (_loadingRows)
            return;

        Draft.DbScripts.Clear();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow)
                continue;

            var file = row.Cells["File"].Value as string;
            if (string.IsNullOrWhiteSpace(file) || !Draft.DbScriptSourcePaths.ContainsKey(file))
                continue;

            var kind = row.Cells["Kind"].Value is DbScriptKind parsed ? parsed : DbScriptKind.Schema;
            Draft.DbScripts.Add(new DbScriptRef(file, kind));
        }

        UpdateCountLabel();
    }

    private void ReloadFromDraft()
    {
        _loadingRows = true;
        _grid.Rows.Clear();
        foreach (var name in Draft.DbScriptSourcePaths.Keys)
        {
            var kind = Draft.DbScripts.FirstOrDefault(s => s.File == name)?.Kind ?? DbScriptKind.Schema;
            _grid.Rows.Add(name, kind);
        }
        _loadingRows = false;

        CommitFromGrid();
    }

    private void UpdateCountLabel() =>
        _countLabel.Text = Draft.DbScripts.Count == 0
            ? "No scripts attached."
            : $"{Draft.DbScripts.Count} script(s) attached — embedded under db/ in the package.";
}
