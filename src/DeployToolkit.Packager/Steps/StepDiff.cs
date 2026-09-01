using DeployToolkit.AppKit;
using DeployToolkit.Core.Manifest;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 3 (plan §10 step 4): diff the publish output against the most recent
/// <b>Deployed</b> package for this component and let the user exclude
/// individual paths (the manual exclude option — unchecked rows land in
/// <see cref="PackageDraft.ExcludedPaths"/> and are honored by
/// <c>PackageBuilder.BuildAsync</c>). Preview-only by design: the final
/// delta is recomputed from disk at build time (deterministic).
/// </summary>
internal sealed class StepDiff : WizardStep
{
    private readonly DataGridView _grid;
    private readonly Label _summaryLabel;
    private readonly Label _baselineLabel;

    private IReadOnlyList<ManifestFile>? _baselineFiles;
    private string? _baselinePackageId;
    private bool _loading;

    public StepDiff(PackagerWizardForm wizard, PackageDraft draft)
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

        _baselineLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 40,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_baselineLabel);

        _grid = new DataGridView { Dock = DockStyle.Fill };
        AppTheme.StyleGrid(_grid, readOnly: false);
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Include",
            HeaderText = "Include",
            Width = 64,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Path",
            HeaderText = "Path",
            ReadOnly = true,
            FillWeight = 70,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Change",
            HeaderText = "Change",
            ReadOnly = true,
            FillWeight = 15,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Size",
            HeaderText = "Size",
            ReadOnly = true,
            FillWeight = 15,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.CellValueChanged += Grid_CellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Commit checkbox toggles immediately so the exclusion set is live.
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        layout.Controls.Add(_grid);

        _summaryLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 24,
            Dock = DockStyle.Fill,
            Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold),
        };
        layout.Controls.Add(_summaryLabel);

        var note = new Label
        {
            Text = "Preview only — the final delta is recomputed from disk at build time (deterministic). " +
                   "Uncheck a row to exclude it from the package (also removes matching deletions).",
            AutoSize = false,
            Height = 36,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(note);

        Controls.Add(layout);
    }

    public override string Title => "3. Diff preview";

    public override string Hint =>
        "Changed/new and deleted files vs. the last Deployed package. Uncheck rows you do not want in this release.";

    public override bool CanProceed => true; // exclusions are optional; even an empty delta may be built

    public override async void OnEnter()
    {
        if (!Draft.IsReadyForDiff || Draft.Component is null || Draft.PublishOutputRoot is null)
            return;

        var component = Draft.Component;
        var outputRoot = Draft.PublishOutputRoot;

        _loading = true;
        await Guard.RunAsync(Wizard, "Comparing against the last deployed baseline…", async cancellationToken =>
        {
            var baseline = await Wizard.Registry.GetLatestDeployedPackageAsync(component.ComponentId);
            _baselinePackageId = baseline?.PackageId;
            _baselineFiles = baseline is null ? null : ManifestSerializer.Deserialize(baseline.ManifestJson).Files;
            // HashFolder reads and hashes EVERY file of the publish output
            // (thousands of files / hundreds of MB for self-contained) —
            // running it on the UI thread froze the wizard (user-reported
            // freeze class). Diff is in-memory and cheap, but it rides along.
            var (currentFiles, diff) = await Task.Run(() =>
            {
                var files = ManifestHasher.HashFolder(outputRoot);
                return (files, ManifestDiffEngine.Diff(files, _baselineFiles));
            }, cancellationToken);
            Draft.CurrentFiles = currentFiles;
            Draft.DiffPreview = diff;
        });
        _loading = false;

        if (IsDisposed || Wizard.IsDisposed)
            return; // the wizard closed mid-compare — nothing to render

        RebuildGrid();
        Wizard.OnDraftChanged();
    }

    private void RebuildGrid()
    {
        _grid.Rows.Clear();

        if (Draft.DiffPreview is not { } diff)
        {
            _baselineLabel.Text = "Nothing to preview yet — run a successful publish first.";
            _summaryLabel.Text = string.Empty;
            return;
        }

        // A fresh preview starts with everything included.
        Draft.ExcludedPaths.Clear();

        _baselineLabel.Text = _baselinePackageId is null
            ? "First package for this component — there is no deployed baseline yet, so everything is new."
            : $"Baseline: last deployed package {_baselinePackageId}";

        var baselinePaths = _baselineFiles is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(_baselineFiles.Select(f => f.Path), StringComparer.Ordinal);

        foreach (var file in diff.ChangedOrNewFiles)
        {
            var change = baselinePaths.Contains(file.Path) ? "Modified" : "Added";
            _grid.Rows.Add(true, file.Path, change, FormatBytes(file.SizeBytes));
        }

        foreach (var deleted in diff.DeletedFiles)
        {
            _grid.Rows.Add(true, deleted, "Deleted", "—");
        }

        UpdateSummaryLabel();
    }

    private void Grid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0 || e.ColumnIndex != 0)
            return;

        var row = _grid.Rows[e.RowIndex];
        if (row.Cells[1].Value is not string path || path.Length == 0)
            return;

        var included = row.Cells[0].Value as bool? ?? true;
        if (included)
            Draft.ExcludedPaths.Remove(path);
        else
            Draft.ExcludedPaths.Add(path);

        UpdateSummaryLabel();
    }

    private void UpdateSummaryLabel()
    {
        if (Draft.DiffPreview is not { } diff)
            return;

        var includedNew = diff.ChangedOrNewFiles.Count(f => !Draft.ExcludedPaths.Contains(f.Path));
        var includedDeleted = diff.DeletedFiles.Count(p => !Draft.ExcludedPaths.Contains(p));
        var excluded = diff.ChangedOrNewFiles.Count - includedNew + (diff.DeletedFiles.Count - includedDeleted);

        _summaryLabel.Text =
            $"{includedNew} changed/new, {includedDeleted} deleted, total {includedNew + includedDeleted}" +
            (excluded > 0 ? $"   ({excluded} excluded manually)" : string.Empty);
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        };
}
