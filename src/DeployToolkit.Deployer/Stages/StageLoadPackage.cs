using System.Text;
using DeployToolkit.AppKit;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Plan §11 step 1: pick a delta.zip, run <see cref="PackageReader.VerifyIntegrity"/>,
/// show the manifest in a TABBED view (Package Info, Assemblies, Assets,
/// Database, Other — user request Q1), and match the zip to a registry
/// package row.
/// </summary>
internal sealed class StageLoadPackage : StagePanel
{
    private readonly TextBox _zipPathBox;
    private readonly Label _messageLabel;
    private readonly CheckBox _recordNewCheckBox;
    private TabControl _tabs = null!;
    private TextBox _infoBox = null!;
    private DataGridView _assembliesGrid = null!;
    private DataGridView _assetsGrid = null!;
    private DataGridView _dbGrid = null!;
    private DataGridView _otherGrid = null!;

    public StageLoadPackage(MainForm shell) : base(shell)
    {
        var layout = MakeVerticalLayout();
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
        var pickFromRegistryButton = new Button { Text = "Pick from registry…" };
        AppTheme.StyleButton(pickFromRegistryButton);
        pickFromRegistryButton.Click += (_, _) => PickFromRegistry();
        zipRow.Controls.Add(_zipPathBox, 0, 0);
        zipRow.Controls.Add(browseButton, 1, 0);
        zipRow.Controls.Add(pickFromRegistryButton, 2, 0);
        layout.Controls.Add(zipRow);

        _recordNewCheckBox = new CheckBox
        {
            Text = "Record as new package in the offline registry (when no matching row exists)",
            AutoSize = true, Checked = true, Margin = new Padding(2, 6, 2, 2),
        };

        _messageLabel = new Label
        {
            Text = string.Empty, AutoSize = false, Height = 30,
            Dock = DockStyle.Fill, ForeColor = Color.DimGray,
        };

        BuildTabs();
        layout.Controls.Add(_recordNewCheckBox);
        layout.Controls.Add(_messageLabel);
        layout.Controls.Add(_tabs);
        Controls.Add(layout);
    }

    private void BuildTabs()
    {
        _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(10, 4) };

        _infoBox = MakeReadOnlySummaryBox(0);
        var infoTab = new TabPage("Package Info");
        infoTab.Controls.Add(_infoBox);
        _tabs.TabPages.Add(infoTab);

        _assembliesGrid = MakeFileGrid();
        _tabs.TabPages.Add(MakeGridTab("Assemblies (bin/)", _assembliesGrid));

        _assetsGrid = MakeFileGrid();
        _tabs.TabPages.Add(MakeGridTab("Assets (wwwroot/)", _assetsGrid));

        _dbGrid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false };
        AppTheme.StyleGrid(_dbGrid, readOnly: true);
        _dbGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type", FillWeight = 20, SortMode = DataGridViewColumnSortMode.NotSortable });
        _dbGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name / Key", FillWeight = 40, SortMode = DataGridViewColumnSortMode.NotSortable });
        _dbGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Detail", HeaderText = "Detail", FillWeight = 40, SortMode = DataGridViewColumnSortMode.NotSortable });
        _tabs.TabPages.Add(MakeGridTab("Database changes", _dbGrid));

        _otherGrid = MakeFileGrid();
        _tabs.TabPages.Add(MakeGridTab("Other files", _otherGrid));
    }

    private static DataGridView MakeFileGrid()
    {
        var g = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false };
        AppTheme.StyleGrid(g, readOnly: true);
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Path", FillWeight = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "Size", FillWeight = 20, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Hash", HeaderText = "Hash (SHA256)", FillWeight = 20, SortMode = DataGridViewColumnSortMode.NotSortable });
        return g;
    }

    private static TabPage MakeGridTab(string title, DataGridView grid)
    {
        var t = new TabPage(title);
        t.Controls.Add(grid);
        return t;
    }

    private void PopulateTabs(ComponentManifest manifest)
    {
        _infoBox.Text = BuildSummary(manifest, Shell.Context?.Package, Shell.Context?.Component);
        _assembliesGrid.Rows.Clear();
        _assetsGrid.Rows.Clear();
        _otherGrid.Rows.Clear();

        foreach (var f in manifest.Files)
        {
            var row = new[] { f.Path, FormatBytes(f.SizeBytes), f.Hash };
            if (f.Path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
                _assembliesGrid.Rows.Add(row);
            else if (f.Path.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
                _assetsGrid.Rows.Add(row);
            else
                _otherGrid.Rows.Add(row);
        }

        _dbGrid.Rows.Clear();
        foreach (var s in manifest.DbScripts)
            _dbGrid.Rows.Add(s.Kind.ToString(), s.File, "SQL script");
        foreach (var kv in manifest.AppSettingsDelta)
            _dbGrid.Rows.Add("AppSettings", kv.Key, kv.Value?.ToString() ?? "(null — removes key)");
    }

    public override string Title => "1. Package";

    public override void OnEnter()
    {
        if (Context is null)
        {
            _infoBox.Text = string.Empty;
            _assembliesGrid.Rows.Clear();
            _assetsGrid.Rows.Clear();
            _dbGrid.Rows.Clear();
            _otherGrid.Rows.Clear();
            _messageLabel.ForeColor = Color.DimGray;
            _messageLabel.Text = Shell.Store is null
                ? "Connect to a registry first (menu: Registry Connection…)."
                : "Pick a package file below — the integrity check runs automatically.";
        }
        _recordNewCheckBox.Visible = Shell.OfflineMode;
    }

    internal void SetZipPath(string path) => _zipPathBox.Text = path;
    internal void StartLoad() => Guard.RunAsync(Shell, "Verifying package…", LoadAsync);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var store = Shell.Store;
        if (store is null) { AppTheme.Error(this, "No registry connection."); return; }

        var zipPath = _zipPathBox.Text.Trim();
        if (zipPath.Length == 0) { _messageLabel.Text = "Pick a package zip first."; return; }
        if (!File.Exists(zipPath)) { _messageLabel.Text = $"File not found: {zipPath}"; return; }

        _messageLabel.ForeColor = Color.DimGray;
        _messageLabel.Text = "Verifying…";

        var integrity = await Task.Run(() => PackageReader.VerifyIntegrity(zipPath), cancellationToken);
        if (!integrity.IsValid)
        {
            _messageLabel.ForeColor = Color.Firebrick;
            _messageLabel.Text = "Integrity check FAILED — corrupt or incomplete.\n" + string.Join("\n", integrity.Problems);
            return;
        }

        var manifest = PackageReader.ReadManifest(zipPath);
        var component = await store.GetComponentAsync(manifest.ComponentId);
        var package = await MatchPackageAsync(store, manifest);

        if (package is null && Shell.OfflineMode && _recordNewCheckBox.Checked)
        {
            package = await store.CreatePackageAsync(manifest.ComponentId, manifest);
            Shell.AppendLog($"No matching row — recorded as new package {package.PackageId}.");
        }
        if (package is null)
        {
            _messageLabel.ForeColor = Color.Firebrick;
            _messageLabel.Text = Shell.OfflineMode
                ? $"No package row for v{manifest.Version} — tick 'Record as new' or reconcile."
                : $"Package not found in registry (v{manifest.Version}).";
            return;
        }

        Shell.SetContext(new DeploymentContext
        {
            ZipPath = zipPath, Manifest = manifest, Package = package,
            Component = component, OfflineMode = Shell.OfflineMode,
            TargetType = component?.TargetType,
        });

        _messageLabel.ForeColor = Color.ForestGreen;
        _messageLabel.Text = "Integrity check passed — package loaded.";
        PopulateTabs(manifest);

        Shell.ClearLog();
        Shell.AppendLog($"Package loaded: {manifest.Component} v{manifest.Version} ({package.PackageId}, {package.Status}).");
        Shell.OnPackageLoaded();
    }

    private void PickZipPath()
    {
        using var picker = new OpenFileDialog { Title = "Load DeployToolkit package", Filter = "DeployToolkit package (*.zip)|*.zip|All files (*.*)|*.*" };
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            _zipPathBox.Text = picker.FileName;
            StartLoad();
        }
    }

    private void PickFromRegistry()
    {
        var store = Shell.Store;
        if (store is null) { AppTheme.Error(this, "Connect to a registry first."); return; }
        using var dialog = new RegistryPackagePickerDialog(store);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ResultPackage is not { } pkg) return;
        if (string.IsNullOrWhiteSpace(pkg.PackageLocation))
        {
            _messageLabel.ForeColor = Color.Firebrick;
            _messageLabel.Text = "PackageLocation is empty — copy the .zip by hand or rebuild with a store.";
            return;
        }
        _zipPathBox.Text = pkg.PackageLocation!;
        _messageLabel.Text = $"Picked '{pkg.Version}' ({pkg.Status}) — verifying…";
        StartLoad();
    }

    private static async Task<PackageRecord?> MatchPackageAsync(IRegistryStore store, ComponentManifest manifest)
    {
        var candidates = (await store.GetPackagesForComponentAsync(manifest.ComponentId))
            .Where(p => string.Equals(p.Version, manifest.Version, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return null;
        var firstHash = manifest.Files.Count > 0 ? manifest.Files[0].Hash : null;
        foreach (var c in candidates)
        {
            try
            {
                var stored = ManifestSerializer.Deserialize(c.ManifestJson);
                var storedHash = stored.Files.Count > 0 ? stored.Files[0].Hash : null;
                if (storedHash is not null && string.Equals(storedHash, firstHash, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            catch { }
        }
        if (candidates.Count == 1) return candidates[0];
        return candidates.OrderBy(c => Math.Abs((c.CreatedUtc - manifest.CreatedUtc).Ticks)).First();
    }

    private static string BuildSummary(ComponentManifest m, PackageRecord? p, DeploymentComponent? c)
    {
        var s = new StringBuilder();
        s.AppendLine($"Package:    {p?.PackageId ?? "?"}  ({p?.Status})");
        s.AppendLine($"Component:  {m.Client} / {m.Component}   (id {m.ComponentId})");
        s.AppendLine($"Version:    {m.Version}    Created: {m.CreatedUtc:yyyy-MM-dd HH:mm:ss zzz}");
        s.AppendLine($"Git commit: {ShortSha(m.GitCommitSha)}");
        s.AppendLine($"Framework:  {m.TargetFramework}{(m.IsSelfContained ? ", self-contained" : ", framework-dependent")}");
        s.AppendLine($"Files:      {m.Files.Count} changed/new ({FormatBytes(m.Files.Sum(f => f.SizeBytes))}), {m.DeletedFiles.Count} deleted");
        s.AppendLine($"Config:     {(m.AppSettingsDelta.Count == 0 ? "no appsettings delta" : $"{m.AppSettingsDelta.Count} key(s)")}");
        s.AppendLine($"DB scripts: {(m.DbScripts.Count == 0 ? "none" : string.Join("; ", m.DbScripts.Select(x => $"{x.File} ({x.Kind})")))}");
        s.AppendLine($"Health URL: {m.HealthCheckUrl ?? "(none)"}");
        s.AppendLine($"Baseline:   {m.BaselineManifest ?? "(none)"}");
        if (c is not null)
            s.AppendLine($"Registry:   TargetType {c.TargetType}");
        else
            s.AppendLine("Registry:   component not found (offline mode)");
        return s.ToString();
    }

    private static string ShortSha(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha.Length <= 12 ? sha : sha[..12];

    private static string FormatBytes(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F1} MB"
        : bytes >= 1 << 10 ? $"{bytes / (double)(1 << 10):F1} KB"
        : $"{bytes} B";
}
