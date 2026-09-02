using DeployToolkit.AppKit;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 3 (plan §10 step 4): diff the publish output against the most recent
/// <b>Deployed</b> package for this component and let the user exclude
/// individual paths (the manual exclude option — unchecked rows land in
/// <see cref="PackageDraft.ExcludedPaths"/> and are honored by
/// <c>PackageBuilder.BuildAsync</c>). Preview-only by design: the final
/// delta is recomputed from disk at build time (deterministic).
///
/// <b>Sensitive-file policy</b>: <c>appsettings.json</c>, <c>web.config</c>,
/// <c>app.config</c>, per-environment <c>appsettings.*.json</c>,
/// <c>connectionstrings.json</c> and <c>secrets.json</c> are ALWAYS excluded
/// — they are never shipped in a delta package (overwriting them on the
/// target server is dangerous; the build-machine copies may carry local dev
/// secrets, and the delta has no trace of the production values). These
/// rows are rendered with a "Sensitive" change label, an unchecked + locked
/// checkbox the user cannot toggle, and a tooltip explaining the policy.
/// The exclusion is also enforced centrally in
/// <see cref="PackageBuilder.BuildAsync"/> so it cannot be bypassed.
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
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 0: baseline label
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 1: grid (EXPANDS)
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 2: summary label
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 3: note

        _baselineLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 22,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_baselineLabel, 0, 0);

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
        // The grid goes in row 1 (the Percent-100 row) so it EXPANDS to fill
        // the remaining vertical space. Added explicitly via SetRow so the
        // TableLayoutPanel places it in the expand row, not the next AutoSize
        // row in add order.
        layout.Controls.Add(_grid, 0, 1);

        _summaryLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 24,
            Dock = DockStyle.Fill,
            Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold),
        };
        layout.Controls.Add(_summaryLabel, 0, 2);

        var note = new Label
        {
            Text = "Preview only — the final delta is recomputed from disk at build time (deterministic). " +
                   "Uncheck a row to exclude it from the package (also removes matching deletions). " +
                   "Rows marked 'Sensitive' (appsettings.json, web.config, appsettings.*.json, " +
                   "connectionstrings.json, secrets.json) are always excluded and locked — they " +
                   "are never published to avoid overwriting production secrets.",
            AutoSize = false,
            Height = 40,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(note, 0, 3);

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

        // A fresh preview starts with everything included — EXCEPT sensitive
        // files (appsettings.json / web.config / ...), which are auto-excluded
        // by policy and can never be included. The user's manual excludes from
        // a previous visit are wiped here too (the grid is rebuilt on entry).
        Draft.ExcludedPaths.Clear();

        _baselineLabel.Text = _baselinePackageId is null
            ? "First package for this component — there is no deployed baseline yet, so everything is new."
            : $"Baseline: last deployed package {_baselinePackageId}";

        var baselinePaths = _baselineFiles is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(_baselineFiles.Select(f => f.Path), StringComparer.Ordinal);

        foreach (var file in diff.ChangedOrNewFiles)
        {
            // Sensitive-file policy: appsettings.json / web.config / etc. are
            // ALWAYS excluded. Render the row with a "Sensitive" change label,
            // an unchecked + LOCKED checkbox (read-only, can't be toggled), and
            // auto-add to ExcludedPaths so the build drops it. The exclusion is
            // ALSO enforced centrally in PackageBuilder.BuildAsync, so even a
            // bypassed UI cannot ship these files.
            var isSensitive = SensitiveFileFilter.IsSensitiveOrAppSettingsVariant(file.Path);
            if (isSensitive)
            {
                Draft.ExcludedPaths.Add(file.Path);
                var rowIdx = _grid.Rows.Add(false, file.Path, "Sensitive", FormatBytes(file.SizeBytes));
                // Lock the checkbox + the whole row so the user can't toggle it.
                _grid.Rows[rowIdx].Cells[0].ReadOnly = true;
                _grid.Rows[rowIdx].DefaultCellStyle.ForeColor = Color.DimGray;
                continue;
            }

            var change = baselinePaths.Contains(file.Path) ? "Modified" : "Added";
            _grid.Rows.Add(true, file.Path, change, FormatBytes(file.SizeBytes));
        }

        foreach (var deleted in diff.DeletedFiles)
        {
            // Sensitive deletions: the file was in the baseline but is gone now.
            // Don't ship a "delete this" instruction either — leave the
            // production sensitive file untouched. Same lock + auto-exclude.
            var isSensitive = SensitiveFileFilter.IsSensitiveOrAppSettingsVariant(deleted);
            if (isSensitive)
            {
                Draft.ExcludedPaths.Add(deleted);
                var rowIdx = _grid.Rows.Add(false, deleted, "Sensitive", "—");
                _grid.Rows[rowIdx].Cells[0].ReadOnly = true;
                _grid.Rows[rowIdx].DefaultCellStyle.ForeColor = Color.DimGray;
                continue;
            }

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

        // Defense-in-depth: a sensitive file's checkbox is locked read-only,
        // but if it's somehow toggled, force it back off and keep it excluded.
        // The central policy in PackageBuilder.BuildAsync drops these anyway.
        if (SensitiveFileFilter.IsSensitiveOrAppSettingsVariant(path))
        {
            if (row.Cells[0].Value is bool b && b)
            {
                row.Cells[0].Value = false;
                _grid.Rows[e.RowIndex].Cells[0].ReadOnly = true;
            }
            Draft.ExcludedPaths.Add(path);
            UpdateSummaryLabel();
            return;
        }

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

        // Count the sensitive rows separately so the user can tell which
        // exclusions are policy (can't override) vs manual (can toggle).
        // ChangedOrNewFiles is IReadOnlyList<ManifestFile>, so the predicate
        // must take a ManifestFile and read its .Path — the method-group
        // SensitiveFileFilter.IsSensitiveOrAppSettingsVariant is Func<string,bool>
        // and won't bind to Func<ManifestFile,bool> (CS1929). DeletedFiles is
        // IReadOnlyList<string>, so the method group binds directly there.
        var sensitiveNew = diff.ChangedOrNewFiles.Count(f => SensitiveFileFilter.IsSensitiveOrAppSettingsVariant(f.Path));
        var sensitiveDeleted = diff.DeletedFiles.Count(SensitiveFileFilter.IsSensitiveOrAppSettingsVariant);
        var sensitiveTotal = sensitiveNew + sensitiveDeleted;
        var manualExcluded = (diff.ChangedOrNewFiles.Count - includedNew - sensitiveNew)
            + (diff.DeletedFiles.Count - includedDeleted - sensitiveDeleted);

        _summaryLabel.Text =
            $"{includedNew} changed/new, {includedDeleted} deleted, total {includedNew + includedDeleted}" +
            (sensitiveTotal > 0 ? $"   ({sensitiveTotal} sensitive — always excluded)" : string.Empty) +
            (manualExcluded > 0 ? $"   ({manualExcluded} excluded manually)" : string.Empty);
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
