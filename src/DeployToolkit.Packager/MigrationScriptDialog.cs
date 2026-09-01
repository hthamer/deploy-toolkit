using DeployToolkit.AppKit;
using DeployToolkit.Core.Database;

namespace DeployToolkit.Packager;

/// <summary>
/// Modal dialog for the "Generate from EF migrations…" flow (user request #2):
/// the user picks a sibling database project (a different folder than the web
/// project), the dialog discovers its <c>Migrations</c> folder via
/// <see cref="MigrationScriptGenerator.DiscoverMigrations"/>, lets the user
/// pick the <c>from</c> and <c>to</c> migrations (defaulting to "from the last
/// migration" → "to the latest"), runs <c>dotnet ef migrations script</c> via
/// <see cref="MigrationScriptGenerator.GenerateScriptAsync"/>, and returns
/// the generated SQL text + a suggested file name. The caller (StepScripts)
/// then attaches it to the package as an editable .sql script (the user can
/// modify, add, or delete — same grid as manually-added scripts).
///
/// Built in plain C# (no resx/designer), like every other dialog in the app.
/// </summary>
internal sealed class MigrationScriptDialog : Form
{
    private readonly TextBox _projectFolderBox;
    private readonly Button _browseButton;
    private readonly ComboBox _fromMigrationBox;
    private readonly ComboBox _toMigrationBox;
    private readonly Button _generateButton;
    private readonly Label _hintLabel;
    private IReadOnlyList<EfMigration> _migrations = Array.Empty<EfMigration>();

    /// <summary>The generated SQL script text when the dialog closes with OK;
    /// otherwise null.</summary>
    public string? ResultScriptText { get; private set; }

    /// <summary>The suggested file name for the generated script (e.g.
    /// <c>migrations_InitialCreate-to-AddUsers.sql</c>); null on cancel.</summary>
    public string? ResultFileName { get; private set; }

    public MigrationScriptDialog(string? initialProjectFolder)
    {
        Text = "Generate SQL from EF migrations";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(620, 320);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(AppTheme.MakeSectionLabel("Database project (contains the Migrations folder)"));

        var folderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _projectFolderBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = initialProjectFolder ?? string.Empty };
        _browseButton = new Button { Text = "Browse…" };
        AppTheme.StyleButton(_browseButton);
        _browseButton.Click += (_, _) => BrowseForProject();
        folderRow.Controls.Add(_projectFolderBox, 0, 0);
        folderRow.Controls.Add(_browseButton, 1, 0);
        layout.Controls.Add(folderRow);

        // From / To migration pickers — populated after the project is chosen.
        var fromRow = MakeMigrationRow("From migration (optional — last deployed):", out _fromMigrationBox);
        var toRow = MakeMigrationRow("To migration (optional — latest by default):", out _toMigrationBox);
        layout.Controls.Add(fromRow);
        layout.Controls.Add(toRow);

        _hintLabel = new Label
        {
            Text = "Leave 'From' empty to script the full schema; set it to the last deployed migration to script only the delta.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Dock = DockStyle.Fill,
        };
        layout.Controls.Add(_hintLabel);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 48,
        };
        var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        _generateButton = new Button { Text = "Generate", Enabled = false };
        AppTheme.StyleButton(cancelBtn);
        AppTheme.StyleButton(_generateButton);
        _generateButton.Click += (_, _) => _ = GenerateAsync();
        buttons.Controls.Add(cancelBtn);
        buttons.Controls.Add(_generateButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = cancelBtn;
        AcceptButton = _generateButton;

        // Seed the migration list if an initial folder was supplied.
        if (!string.IsNullOrWhiteSpace(initialProjectFolder))
            RefreshMigrations(initialProjectFolder);
    }

    private static Control MakeMigrationRow(string label, out ComboBox box)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 8, 2) }, 0, 0);
        box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        row.Controls.Add(box, 1, 0);
        return row;
    }

    private void BrowseForProject()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Select the EF Core database project folder (contains the Migrations folder)",
            ShowNewFolderButton = false,
        };
        if (Directory.Exists(_projectFolderBox.Text))
            picker.SelectedPath = _projectFolderBox.Text;

        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            _projectFolderBox.Text = picker.SelectedPath;
            RefreshMigrations(picker.SelectedPath);
        }
    }

    /// <summary>Re-discover the Migrations folder and repopulate the From/To
    /// combos. Defaults: From = second-newest (so the script covers the
    /// newest migration only — the common "new migration since last release"
    /// case), To = newest. When there's only one migration, From is left empty
    /// (full schema script).</summary>
    private void RefreshMigrations(string projectFolder)
    {
        _migrations = MigrationScriptGenerator.DiscoverMigrations(projectFolder);

        _fromMigrationBox.Items.Clear();
        _toMigrationBox.Items.Clear();

        // Add an explicit "(start — full schema)" option as the first From entry.
        _fromMigrationBox.Items.Add(string.Empty);
        foreach (var m in _migrations)
        {
            _fromMigrationBox.Items.Add(m.Name);
            _toMigrationBox.Items.Add(m.Name);
        }

        if (_migrations.Count == 0)
        {
            _hintLabel.ForeColor = Color.Firebrick;
            _hintLabel.Text = $"No EF migrations found under '{Path.Combine(projectFolder, "Migrations")}'. " +
                              "Pick the project that contains the Migrations folder (the EF Core database project, not the web project).";
            _generateButton.Enabled = false;
            return;
        }

        _hintLabel.ForeColor = Color.DimGray;
        _hintLabel.Text = _migrations.Count == 1
            ? $"Found 1 migration: {_migrations[0].DisplayName}. 'From' is empty → full schema script."
            : $"Found {_migrations.Count} migrations. Defaulting 'From' to the second-newest so the script covers only the newest migration.";

        // Default: From = second-newest (so the delta = newest migration only),
        // To = newest. When there's only one, From stays empty (full schema).
        _fromMigrationBox.SelectedIndex = _migrations.Count > 1 ? 2 : 0; // index 0 is the empty option
        _toMigrationBox.SelectedIndex = 0; // newest
        _generateButton.Enabled = true;
    }

    private async Task GenerateAsync()
    {
        var projectFolder = _projectFolderBox.Text.Trim();
        if (projectFolder.Length == 0 || !Directory.Exists(projectFolder))
        {
            AppTheme.Error(this, "Pick the database project folder first.");
            return;
        }

        if (_migrations.Count == 0)
        {
            AppTheme.Error(this, "No migrations found — pick a project that contains the Migrations folder.");
            return;
        }

        var from = _fromMigrationBox.SelectedItem as string;
        var to = _toMigrationBox.SelectedItem as string;
        // Empty-string From means "full schema from the first migration".
        if (string.IsNullOrEmpty(from))
            from = null;

        _generateButton.Enabled = false;
        _hintLabel.ForeColor = Color.DimGray;
        _hintLabel.Text = "Generating SQL via 'dotnet ef migrations script'…";

        MigrationScriptResult? result = null;
        try
        {
            // Guard.RunAsync returns Task (not Task<T>) — it swallows the
            // lambda's return value and any exception. Capture the result in a
            // local; the guard reports failures via AppTheme.Error, so read the
            // local after the await to decide OK vs. stay-on-dialog.
            await Guard.RunAsync(this, "Generating EF migration script…", async () =>
            {
                result = await MigrationScriptGenerator.GenerateScriptAsync(projectFolder, from, to, timeoutMinutes: 5);
            });

            if (IsDisposed)
                return;

            if (result is null || !result.Success)
            {
                _hintLabel.ForeColor = Color.Firebrick;
                var msg = result?.ErrorSummary ?? "unknown error";
                _hintLabel.Text = $"Generation failed (exit {result?.ExitCode ?? -1}): {msg}. " +
                                  "Make sure 'dotnet-ef' is installed (dotnet tool install --global dotnet-ef) and the project builds.";
                return;
            }

            ResultScriptText = result.ScriptText;
            // Suggested name: migrations_<from-or-Initial>-to-<to>.sql
            var fromLabel = string.IsNullOrEmpty(from) ? "Initial" : from;
            var toLabel = to ?? "Latest";
            // Strip the timestamp prefix from each for a cleaner file name.
            var fromShort = StripTimestamp(fromLabel);
            var toShort = StripTimestamp(toLabel);
            ResultFileName = $"migrations_{fromShort}-to-{toShort}.sql";

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _hintLabel.ForeColor = Color.Firebrick;
            _hintLabel.Text = DescribeException(ex);
        }
        finally
        {
            if (!IsDisposed)
                _generateButton.Enabled = true;
        }
    }

    private static string StripTimestamp(string migrationName)
    {
        if (string.IsNullOrEmpty(migrationName))
            return migrationName;
        var underscore = migrationName.IndexOf('_');
        return underscore >= 0 ? migrationName[(underscore + 1)..] : migrationName;
    }

    private static string DescribeException(Exception ex) =>
        ex is ArgumentException or InvalidOperationException
            ? ex.Message
            : $"{ex.GetType().Name}: {ex.Message}";
}
