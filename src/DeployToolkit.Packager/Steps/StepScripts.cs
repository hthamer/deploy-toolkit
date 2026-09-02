using DeployToolkit.AppKit;
using DeployToolkit.Core.Database;
using DeployToolkit.Core.Manifest;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 5 (plan §10 step 6): two grids — EF migrations and external .sql
/// files. Both are embedded into the package under db/ and run by the
/// Deployer after an explicit confirm.
///
/// <b>EF migrations grid (user request #3/#4)</b>:
/// <list type="number">
///  <item>A project dropdown lists ALL .csproj files found under the
///   folder picked in step 1 (Folder &amp; component) — NOT filtered to web
///   apps, because the DB project is usually a class library. Similar to the
///   publish step's project dropdown, but unfiltered.</item>
///  <item>After selecting a project, the grid auto-populates with the
///   migrations discovered in its Migrations folder (newest-first). Each row
///   has a checkbox; rows NOT in the previous package's
///   <see cref="ComponentManifest.AppliedMigrations"/> are auto-CHECKED
///   (they're the pending ones); already-applied migrations are auto-UNCHECKED
///   and shown as "applied". This is the tracking the user asked for: "retrieve
///   only the migrations that are not applied (don't rely on first-to-last,
///   as there may be some migrations in the middle that are added later
///   which are not deployed)".</item>
///  <item>At build time (StepBuild), the selected migrations generate a SQL
///   script via <c>dotnet ef migrations script --idempotent</c> and are
///   attached as a Schema DbScript. The new manifest's
///   <see cref="ComponentManifest.AppliedMigrations"/> = previously applied
///   ∪ selected — tracked so the next build knows exactly what's pending.</item>
/// </list>
///
/// <b>External .sql grid</b>: the existing manual attachment flow (Add
/// .sql files…, Kind dropdown, Remove). Unchanged from before.
/// </summary>
internal sealed class StepScripts : WizardStep
{
    private readonly ComboBox _efProjectBox;
    private readonly DataGridView _efMigrationsGrid;
    private readonly Label _efHintLabel;
    private readonly DataGridView _sqlGrid;
    private readonly Label _countLabel;
    private bool _loadingEfGrid;
    private bool _loadingSqlGrid;
    /// <summary>Suppresses the project-box SelectedIndexChanged handler while
    /// the box is being populated programmatically (so setting SelectedIndex
    /// during PopulateEfProjectsAsync doesn't fire the async migration load
    /// re-entrantly before population finished).</summary>
    private bool _suppressProjectEvents;
    private IReadOnlyList<EfMigration> _efMigrations = Array.Empty<EfMigration>();

    public StepScripts(PackagerWizardForm wizard, PackageDraft draft)
        : base(wizard, draft)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // EF section label
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // EF project dropdown
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); // EF migrations grid
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // EF hint
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // External section label + button
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); // External .sql grid
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // count label + note

        // ============== EF migrations section ==============
        layout.Controls.Add(AppTheme.MakeSectionLabel("EF migrations (auto-tracked)"));

        var efProjectRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        efProjectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        efProjectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        efProjectRow.Controls.Add(new Label { Text = "DB project:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 8, 2) }, 0, 0);
        _efProjectBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _efProjectBox.SelectedIndexChanged += (_, _) =>
        {
            // Skip the async load while populating programmatically —
            // PopulateEfProjectsAsync fires the load explicitly after the
            // box is ready, so a re-entrant fire-and-forget here would race
            // (and could call Guard.RunAsync while the wizard is already busy
            // from the OnEnter Guard).
            if (_suppressProjectEvents)
                return;
            _ = OnEfProjectSelectedAsync();
        };
        efProjectRow.Controls.Add(_efProjectBox, 1, 0);
        layout.Controls.Add(efProjectRow);

        _efMigrationsGrid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false };
        AppTheme.StyleGrid(_efMigrationsGrid, readOnly: false);
        _efMigrationsGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Include",
            HeaderText = "Include",
            Width = 60,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _efMigrationsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Migration",
            HeaderText = "Migration",
            ReadOnly = true,
            FillWeight = 60,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _efMigrationsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = "Status",
            ReadOnly = true,
            FillWeight = 40,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _efMigrationsGrid.CellValueChanged += (_, e) => OnEfCheckboxChanged(e);
        _efMigrationsGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_efMigrationsGrid.IsCurrentCellDirty)
                _efMigrationsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        layout.Controls.Add(_efMigrationsGrid);

        _efHintLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 36,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_efHintLabel);

        // ============== External .sql section ==============
        var externalHeaderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        externalHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        externalHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        externalHeaderRow.Controls.Add(AppTheme.MakeSectionLabel("External / manual .sql files"), 0, 0);
        var addButton = new Button { Text = "Add .sql files…" };
        AppTheme.StyleButton(addButton);
        addButton.Click += (_, _) => AddScripts();
        externalHeaderRow.Controls.Add(addButton, 1, 0);
        layout.Controls.Add(externalHeaderRow);

        _sqlGrid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false };
        AppTheme.StyleGrid(_sqlGrid, readOnly: false);
        _sqlGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "File",
            HeaderText = "File",
            ReadOnly = true,
            FillWeight = 60,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _sqlGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Kind",
            HeaderText = "Kind",
            DataSource = new[] { DbScriptKind.Schema, DbScriptKind.Data },
            ValueType = typeof(DbScriptKind),
            FillWeight = 25,
        });
        _sqlGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Remove",
            HeaderText = "Remove",
            Text = "Remove",
            UseColumnTextForButtonValue = true,
            FillWeight = 15,
        });
        _sqlGrid.CellValueChanged += (_, _) => CommitSqlFromGrid();
        _sqlGrid.RowsRemoved += (_, _) => CommitSqlFromGrid();
        _sqlGrid.CellContentClick += SqlGrid_CellContentClick;
        layout.Controls.Add(_sqlGrid);

        _countLabel = new Label
        {
            Text = "No scripts attached.",
            AutoSize = false,
            Height = 36,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_countLabel);

        Controls.Add(layout);
    }

    public override string Title => "5. DB scripts";

    public override string Hint =>
        "Pick the EF database project to auto-track migrations (checked = pending since the last deployed package), " +
        "and/or attach external .sql files. Selected migrations generate a script on build.";

    public override bool CanProceed => true;

    public override async void OnEnter()
    {
        // Populate the EF project dropdown with ALL .csproj under the
        // folder picked in step 1 (unfiltered — the DB project is usually a
        // class library, not a web app). Then fetch the baseline manifest's
        // AppliedMigrations so we can auto-check only the pending migrations.
        await PopulateEfProjectsAsync();
        PopulateSqlGrid();
        UpdateCountLabel();
    }

    public override void OnLeave()
    {
        CommitSqlFromGrid();
        CommitEfSelection();
    }

    // ---------------------------------------------------------------
    // EF migrations

    private async Task PopulateEfProjectsAsync()
    {
        if (Draft.FolderPath is null)
        {
            _efProjectBox.Items.Clear();
            _efHintLabel.Text = "Pick a folder in step 1 first.";
            return;
        }

        var projects = await Task.Run(() => DiscoverAllProjects(Draft.FolderPath));

        // Suppress the SelectedIndexChanged handler while programmatically
        // clearing + re-populating the box (Clear/Items.Add/SelectedIndex all
        // fire it). The load is triggered explicitly below for the final
        // selection so it runs exactly once, after the box is ready.
        _suppressProjectEvents = true;
        try
        {
            _efProjectBox.Items.Clear();
            foreach (var p in projects)
                _efProjectBox.Items.Add(p);

            // Restore the previously selected project if it's still in the list;
            // otherwise auto-select the first project.
            if (Draft.EfMigrationsProjectPath is { } prev && projects.Contains(prev))
                _efProjectBox.SelectedItem = prev;
            else if (projects.Count > 0 && _efProjectBox.SelectedIndex < 0)
                _efProjectBox.SelectedIndex = 0; // auto-select the first project
        }
        finally { _suppressProjectEvents = false; }

        if (projects.Count == 0)
        {
            _efHintLabel.Text = "No .csproj found under the selected folder.";
            return;
        }

        // Trigger the migration load for the selected project (the handler was
        // suppressed above, so it didn't fire). This runs exactly once, after
        // the box is fully populated.
        await OnEfProjectSelectedAsync();
    }

    private async Task OnEfProjectSelectedAsync()
    {
        if (_efProjectBox.SelectedItem is not string project || project.Length == 0)
        {
            _efMigrations = Array.Empty<EfMigration>();
            _efMigrationsGrid.Rows.Clear();
            Draft.SelectedEfMigrations.Clear();
            Draft.EfMigrationsProjectPath = null;
            return;
        }

        Draft.EfMigrationsProjectPath = project;

        // Fetch the baseline manifest's AppliedMigrations so we can auto-check
        // only the pending (not-yet-applied) migrations. Fetched once per
        // project selection — cached in the draft for the build step.
        await FetchPreviouslyAppliedMigrationsAsync();

        // Discover migrations + populate the grid with auto-checked = pending.
        _efMigrations = await Task.Run(() => MigrationScriptGenerator.DiscoverMigrations(project));
        PopulateEfMigrationsGrid();
    }

    /// <summary>Fetches the last DEPLOYED package's manifest and caches its
    /// AppliedMigrations in <see cref="Draft.PreviouslyAppliedMigrations"/>.
    /// Empty on the first package (no baseline). Runs under Guard.</summary>
    private async Task FetchPreviouslyAppliedMigrationsAsync()
    {
        if (Draft.Component is null)
        {
            Draft.PreviouslyAppliedMigrations = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        var componentId = Draft.Component.ComponentId;
        IReadOnlyList<string> applied = Array.Empty<string>();
        await Guard.RunAsync(Wizard, "Loading applied migrations…", async _ =>
        {
            var baseline = await Wizard.Registry.GetLatestDeployedPackageAsync(componentId);
            if (baseline is null)
            {
                applied = Array.Empty<string>();
                return;
            }
            var manifest = ManifestSerializer.Deserialize(baseline.ManifestJson);
            applied = manifest.AppliedMigrations;
        });

        Draft.PreviouslyAppliedMigrations = new HashSet<string>(applied, StringComparer.Ordinal);
    }

    private void PopulateEfMigrationsGrid()
    {
        _loadingEfGrid = true;
        _efMigrationsGrid.Rows.Clear();
        Draft.SelectedEfMigrations.Clear();

        var applied = Draft.PreviouslyAppliedMigrations;
        foreach (var m in _efMigrations)
        {
            var isApplied = applied.Contains(m.Name);
            // Auto-CHECK pending (not yet applied); uncheck + mark "applied"
            // for already-deployed migrations. The user can override.
            var idx = _efMigrationsGrid.Rows.Add(!isApplied, m.Name, isApplied ? "applied" : "pending");
            if (!isApplied)
                Draft.SelectedEfMigrations.Add(m.Name);
            else
                _efMigrationsGrid.Rows[idx].DefaultCellStyle.ForeColor = Color.DimGray; // dim applied rows
        }
        _loadingEfGrid = false;

        var pending = _efMigrations.Count(m => !applied.Contains(m.Name));
        _efHintLabel.Text = _efMigrations.Count == 0
            ? "No EF migrations found in this project's Migrations folder."
            : $"{_efMigrations.Count} migration(s) found, {pending} pending (auto-checked). " +
              "Checked migrations generate a script on build and are recorded as applied in the manifest.";
    }

    private void OnEfCheckboxChanged(DataGridViewCellEventArgs e)
    {
        if (_loadingEfGrid || e.RowIndex < 0 || e.ColumnIndex != 0)
            return;

        var row = _efMigrationsGrid.Rows[e.RowIndex];
        if (row.Cells["Migration"].Value is not string name)
            return;

        var include = row.Cells["Include"].Value as bool? ?? false;
        if (include)
            Draft.SelectedEfMigrations.Add(name);
        else
            Draft.SelectedEfMigrations.Remove(name);
    }

    private void CommitEfSelection()
    {
        // Already maintained live by OnEfCheckboxChanged — nothing to do here,
        // but kept for symmetry with CommitSqlFromGrid and as a hook if the
        // commit model ever needs to change.
    }

    // ---------------------------------------------------------------
    // External .sql files

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

        ReloadSqlFromDraft();
    }

    private void SqlGrid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _sqlGrid.Columns[e.ColumnIndex].Name != "Remove")
            return;
        if (_sqlGrid.Rows[e.RowIndex].Cells["File"].Value is string fileName)
            Draft.DbScriptSourcePaths.Remove(fileName);
        _sqlGrid.Rows.RemoveAt(e.RowIndex);
        CommitSqlFromGrid();
    }

    private void CommitSqlFromGrid()
    {
        if (_loadingSqlGrid)
            return;

        Draft.DbScripts.Clear();
        foreach (DataGridViewRow row in _sqlGrid.Rows)
        {
            if (row.IsNewRow) continue;
            var file = row.Cells["File"].Value as string;
            if (string.IsNullOrWhiteSpace(file) || !Draft.DbScriptSourcePaths.ContainsKey(file))
                continue;
            var kind = row.Cells["Kind"].Value is DbScriptKind parsed ? parsed : DbScriptKind.Schema;
            Draft.DbScripts.Add(new DbScriptRef(file, kind));
        }
        UpdateCountLabel();
    }

    private void PopulateSqlGrid()
    {
        _loadingSqlGrid = true;
        _sqlGrid.Rows.Clear();
        foreach (var script in Draft.DbScripts)
            _sqlGrid.Rows.Add(script.File, script.Kind);
        _loadingSqlGrid = false;
    }

    private void ReloadSqlFromDraft()
    {
        _loadingSqlGrid = true;
        _sqlGrid.Rows.Clear();
        foreach (var name in Draft.DbScriptSourcePaths.Keys)
        {
            var kind = Draft.DbScripts.FirstOrDefault(s => s.File == name)?.Kind ?? DbScriptKind.Schema;
            _sqlGrid.Rows.Add(name, kind);
        }
        _loadingSqlGrid = false;
        CommitSqlFromGrid();
    }

    private void UpdateCountLabel() =>
        _countLabel.Text = Draft.DbScripts.Count == 0
            ? "No external scripts attached. EF migrations (if selected) generate their own script on build."
            : $"{Draft.DbScripts.Count} external script(s) attached — embedded under db/ in the package.";

    // ---------------------------------------------------------------
    // Helpers

    /// <summary>Recursively finds ALL .csproj files under the folder,
    /// skipping bin/obj/.git. Unfiltered (unlike StepPublish's DiscoverProjects
    /// which filters to web apps) — the DB project is usually a class
    /// library, not a web app.</summary>
    private static List<string> DiscoverAllProjects(string rootFolder)
    {
        var results = new List<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git" };

        void Walk(string directory)
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(directory, "*.csproj"));
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    if (skip.Contains(Path.GetFileName(sub)))
                        continue;
                    Walk(sub);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // unreadable subtree — skip it
            }
        }

        Walk(rootFolder);
        return results;
    }
}
