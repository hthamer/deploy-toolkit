using System.Text;
using DeployToolkit.AppKit;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Plan §11 step 1: pick a delta.zip, run <see cref="PackageReader.VerifyIntegrity"/>
/// (hashes must match the manifest before anything is trusted — plan §12
/// "Package integrity"), show the manifest summary, and match the zip to a
/// registry package row (the manifest carries only ComponentId — the
/// orchestrator needs a real package id for RecordRunStart/MarkDeployed).
///
/// Matching is best-effort and documented as such: packages are unique per
/// component+version in practice, so the search filters on Version and then
/// disambiguates by manifest identity (the first file's hash, compared via
/// <see cref="ManifestSerializer.Deserialize"/> of each candidate's stored
/// ManifestJson), falling back to CreatedUtc proximity. In offline mode a
/// missing row can be recorded as a new Created package straight from the
/// manifest; in online mode a miss blocks the run with guidance (the row is
/// created by the Packager, so a miss usually means the wrong registry or an
/// un-reconciled offline result).
/// </summary>
internal sealed class StageLoadPackage : StagePanel
{
    private readonly TextBox _zipPathBox;
    private readonly TextBox _summaryBox;
    private readonly Label _messageLabel;
    private readonly CheckBox _recordNewCheckBox;

    public StageLoadPackage(MainForm shell)
        : base(shell)
    {
        var layout = MakeVerticalLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(AppTheme.MakeSectionLabel("Package file (delta.zip)"));

        var zipRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        zipRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        zipRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        zipRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _zipPathBox = new TextBox { Dock = DockStyle.Fill };
        var browseButton = new Button { Text = "Browse…" };
        AppTheme.StyleButton(browseButton);
        browseButton.Click += (_, _) => PickZipPath();
        // Option B: pick a package straight from the registry — the dialog
        // lists components + their packages, and on OK fills the zip path from
        // the selected row's PackageLocation (the shared-store path). No more
        // "where did the builder put the .zip?" — the registry tracks it.
        var pickFromRegistryButton = new Button { Text = "Pick from registry…" };
        AppTheme.StyleButton(pickFromRegistryButton);
        pickFromRegistryButton.Click += (_, _) => PickFromRegistry();
        zipRow.Controls.Add(_zipPathBox, 0, 0);
        zipRow.Controls.Add(browseButton, 1, 0);
        zipRow.Controls.Add(pickFromRegistryButton, 2, 0);
        layout.Controls.Add(zipRow);

        // Offline-mode convenience: the offline registry has no Packager
        // build record for packages created elsewhere, so offer to record one.
        _recordNewCheckBox = new CheckBox
        {
            Text = "Record as new package in the offline registry (when no matching row exists)",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(2, 6, 2, 2),
        };

        _messageLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 56,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };

        _summaryBox = MakeReadOnlySummaryBox(220);

        layout.Controls.Add(_recordNewCheckBox);
        layout.Controls.Add(_messageLabel);
        layout.Controls.Add(_summaryBox);

        Controls.Add(layout);
    }

    public override string Title => "1. Verify & Load";

    public override void OnEnter()
    {
        if (Context is null)
        {
            _summaryBox.Text = string.Empty;
            _messageLabel.ForeColor = Color.DimGray;
            _messageLabel.Text = Shell.Store is null
                ? "Connect to a registry first (menu: Registry Connection…)."
                : "Pick a package and click 'Verify & Load' below. The integrity check runs before anything is shown.";
        }

        // Offline-mode only affordance — in online mode the Packager owns
        // package records, so a missing row is an error, not an action.
        _recordNewCheckBox.Visible = Shell.OfflineMode;
    }

    /// <summary>Fills the zip path (used by the menu's Load Package… flow).</summary>
    internal void SetZipPath(string path) => _zipPathBox.Text = path;

    /// <summary>Runs verify + manifest read + registry match (guarded by the
    /// caller — invoked from the bottom-bar "Verify & Load" button and the
    /// Load Package… menu item). The token is honoured so the busy dialog's
    /// Cancel frees the UI even mid-verify.</summary>
    internal void StartLoad() => Guard.RunAsync(Shell, "Verifying package…", LoadAsync);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var store = Shell.Store;
        if (store is null)
        {
            AppTheme.Error(this, "No registry connection. Open 'Registry Connection…' first.");
            return;
        }

        var zipPath = _zipPathBox.Text.Trim();
        if (zipPath.Length == 0)
        {
            _messageLabel.ForeColor = Color.Firebrick;
            _messageLabel.Text = "Pick a package zip first.";
            return;
        }

        if (!File.Exists(zipPath))
        {
            _messageLabel.ForeColor = Color.Firebrick;
            _messageLabel.Text = $"File not found: {zipPath}";
            return;
        }

        _messageLabel.ForeColor = Color.DimGray;
        _messageLabel.Text = "Verifying…";

        // --- integrity first: a corrupted/partial copy must fail loudly
        // (plan §12) before a single byte of its manifest is trusted.
        // VerifyIntegrity hashes the whole zip — heavy IO, so it runs off
        // the UI thread (user-reported freeze class).
        var integrity = await Task.Run(() => PackageReader.VerifyIntegrity(zipPath), cancellationToken);
        if (!integrity.IsValid)
        {
            _messageLabel.ForeColor = Color.Firebrick;
            _messageLabel.Text = "Integrity check FAILED — the copy is corrupt or incomplete. Nothing was touched.\n" +
                                 string.Join("\n", integrity.Problems);
            Shell.ClearLog();
            Shell.AppendLog($"Package integrity check failed for '{zipPath}':");
            foreach (var problem in integrity.Problems)
                Shell.AppendLog("  " + problem);
            return;
        }

        var manifest = PackageReader.ReadManifest(zipPath);

        // --- registry resolution: component + package row.
        var component = await store.GetComponentAsync(manifest.ComponentId);
        var package = await MatchPackageAsync(store, manifest);

        if (package is null && Shell.OfflineMode && _recordNewCheckBox.Checked)
        {
            package = await store.CreatePackageAsync(manifest.ComponentId, manifest);
            Shell.AppendLog($"No matching package row in the offline registry — recorded the manifest as new package {package.PackageId} (Created).");
        }

        if (package is null)
        {
            _messageLabel.ForeColor = Color.Firebrick;
            _messageLabel.Text = Shell.OfflineMode
                ? $"No package row for version {manifest.Version} in the offline registry, and 'record as new' is unticked — tick it or reconcile first."
                : $"Package not found in registry (version {manifest.Version} for this component) — reconcile offline results or record it as new, then reload.";
            return;
        }

        Shell.SetContext(new DeploymentContext
        {
            ZipPath = zipPath,
            Manifest = manifest,
            Package = package,
            Component = component,
            OfflineMode = Shell.OfflineMode,
            TargetType = component?.TargetType,
        });

        _summaryBox.Text = BuildSummary(manifest, package, component);
        _messageLabel.ForeColor = Color.ForestGreen;
        _messageLabel.Text = "Integrity check passed — package loaded.";

        Shell.ClearLog();
        Shell.AppendLog($"Package loaded and verified: {manifest.Component} v{manifest.Version} (package {package.PackageId}, {package.Status}).");
        Shell.OnPackageLoaded();
    }

    private void PickZipPath()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Load DeployToolkit package",
            Filter = "DeployToolkit package (*.zip)|*.zip|All files (*.*)|*.*",
        };
        var current = _zipPathBox.Text.Trim();
        if (current.Length > 0)
        {
            picker.FileName = Path.GetFileName(current);
            var directory = Path.GetDirectoryName(current);
            if (directory is not null && Directory.Exists(directory))
                picker.InitialDirectory = directory;
        }

        if (picker.ShowDialog(this) == DialogResult.OK)
            _zipPathBox.Text = picker.FileName;
    }

    /// <summary>Option B: opens the registry package picker. On OK, fills the
    /// zip path from the selected package's <see cref="PackageRecord.PackageLocation"/>
    /// — the shared-store path the Packager uploaded the .zip to. If the .zip
    /// isn't reachable at that path (share down / credentials missing), the
    /// existing "File not found" check in <see cref="LoadAsync"/> surfaces a
    /// clear error; the user can also still use Browse… to pick a manually
    /// copied .zip.</summary>
    private void PickFromRegistry()
    {
        var store = Shell.Store;
        if (store is null)
        {
            AppTheme.Error(this, "Connect to a registry first (menu: Registry Connection…).");
            return;
        }

        using var dialog = new RegistryPackagePickerDialog(store);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ResultPackage is not { } pkg)
            return;

        if (string.IsNullOrWhiteSpace(pkg.PackageLocation))
        {
            _messageLabel.ForeColor = Color.Firebrick;
            _messageLabel.Text =
                $"Package '{pkg.Version}' was not uploaded to the shared store (PackageLocation is empty). " +
                "The builder either didn't configure a package store or the upload failed. " +
                "Copy the .zip by hand and use Browse…, or ask the builder to rebuild with the store configured.";
            return;
        }

        _zipPathBox.Text = pkg.PackageLocation!;
        _messageLabel.ForeColor = Color.DimGray;
        _messageLabel.Text = $"Picked package '{pkg.Version}' ({pkg.Status}) from the registry. Click 'Verify & Load' below.";
    }

    /// <summary>
    /// Best-effort match of the loaded manifest to a registry package row —
    /// the manifest has no package id, so the identity is reconstructed:
    ///  1. restrict to this component's rows with the same Version,
    ///  2. prefer an exact manifest match (first file's hash, compared after
    ///     deserializing each candidate's stored ManifestJson),
    ///  3. a single candidate is accepted as-is,
    ///  4. otherwise the candidate with the nearest CreatedUtc wins.
    /// Rebuilt/repackaged zips of the same version are the case this can get
    /// wrong in principle; in practice the Packager never rebuilds an
    /// existing version.
    /// </summary>
    private static async Task<PackageRecord?> MatchPackageAsync(IRegistryStore store, ComponentManifest manifest)
    {
        var candidates = (await store.GetPackagesForComponentAsync(manifest.ComponentId))
            .Where(p => string.Equals(p.Version, manifest.Version, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return null;

        var manifestFirstHash = manifest.Files.Count > 0 ? manifest.Files[0].Hash : null;
        foreach (var candidate in candidates)
        {
            try
            {
                var stored = ManifestSerializer.Deserialize(candidate.ManifestJson);
                var storedFirstHash = stored.Files.Count > 0 ? stored.Files[0].Hash : null;
                if (storedFirstHash is not null && string.Equals(storedFirstHash, manifestFirstHash, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            {
                // A corrupt stored manifest can't disambiguate — skip it.
            }
        }

        if (candidates.Count == 1)
            return candidates[0];

        return candidates
            .OrderBy(c => Math.Abs((c.CreatedUtc - manifest.CreatedUtc).Ticks))
            .First();
    }

    private static string BuildSummary(ComponentManifest manifest, PackageRecord package, DeploymentComponent? component)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"Package:    {package.PackageId}  ({package.Status})");
        summary.AppendLine($"Component:  {manifest.Client} / {manifest.Component}   (id {manifest.ComponentId})");
        summary.AppendLine($"Version:    {manifest.Version}    Created: {manifest.CreatedUtc:yyyy-MM-dd HH:mm:ss zzz}");
        summary.AppendLine($"Git commit: {ShortSha(manifest.GitCommitSha)}");
        summary.AppendLine($"Framework:  {manifest.TargetFramework}{(manifest.IsSelfContained ? ", self-contained" : ", framework-dependent")}");
        summary.AppendLine($"Files:      {manifest.Files.Count} changed/new ({FormatBytes(manifest.Files.Sum(f => f.SizeBytes))}), {manifest.DeletedFiles.Count} deleted");
        summary.AppendLine($"Config:     {(manifest.AppSettingsDelta.Count == 0 ? "no appsettings delta" : $"{manifest.AppSettingsDelta.Count} key(s): {string.Join(", ", manifest.AppSettingsDelta.Keys)}")}");
        summary.AppendLine($"DB scripts: {(manifest.DbScripts.Count == 0 ? "none" : string.Join("; ", manifest.DbScripts.Select(s => $"{s.File} ({s.Kind})")))}");
        summary.AppendLine($"Health URL: {manifest.HealthCheckUrl ?? "(none)"}");
        summary.AppendLine($"Baseline:   {manifest.BaselineManifest ?? "(none — first package for this component)"}");
        if (component is not null)
            summary.AppendLine($"Registry:   component found — TargetType {component.TargetType}" +
                               (string.IsNullOrWhiteSpace(component.HealthCheckUrl) ? "" : $", health URL {component.HealthCheckUrl}"));
        else
            summary.AppendLine("Registry:   component record not found (offline mode) — the target type will be asked for.");
        return summary.ToString();
    }

    private static string ShortSha(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha.Length <= 12 ? sha : sha[..12];

    private static string FormatBytes(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F1} MB"
        : bytes >= 1 << 10 ? $"{bytes / (double)(1 << 10):F1} KB"
        : $"{bytes} B";
}
