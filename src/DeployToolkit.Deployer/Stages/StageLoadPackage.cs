using System.Text;
using DeployToolkit.AppKit;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Plan §11 step 1: pick a delta.zip, run <see cref="PackageReader.VerifyIntegrity"/>,
/// show the manifest in a TABBED view (Package Info, App files, Assets,
/// Database, Other — user request), and match the zip to a registry
/// package row.
/// </summary>
internal sealed class StageLoadPackage : StagePanel
{
    private readonly TextBox _zipPathBox;
    private readonly Label _messageLabel;
    private TabControl _tabs = null!;
    private TextBox _infoBox = null!;
    private DataGridView _appFilesGrid = null!;
    private DataGridView _assetsGrid = null!;
    private DataGridView _dbGrid = null!;
    private DataGridView _otherGrid = null!;

    public StageLoadPackage(MainForm shell) : base(shell)
    {
        var layout = MakeVerticalLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 0: section label
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 1: zip row
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 2: message
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 3: tabs (FILL)

        layout.Controls.Add(AppTheme.MakeSectionLabel("Package file (delta.zip)"), 0, 0);

        // Only Browse — no "Pick from registry" (user: no need for it).
        var zipRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        zipRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        zipRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _zipPathBox = new TextBox { Dock = DockStyle.Fill };
        var browseButton = new Button { Text = "Browse…" };
        AppTheme.StyleButton(browseButton);
        browseButton.Click += (_, _) => PickZipPath();
        zipRow.Controls.Add(_zipPathBox, 0, 0);
        zipRow.Controls.Add(browseButton, 1, 0);
        layout.Controls.Add(zipRow, 0, 1);

        _messageLabel = new Label
        {
            Text = string.Empty, AutoSize = false, Height = 24,
            Dock = DockStyle.Fill, ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_messageLabel, 0, 2);

        BuildTabs();
        layout.Controls.Add(_tabs, 0, 3);
        Controls.Add(layout);
    }

    private void BuildTabs()
    {
        _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(10, 4) };

        _infoBox = MakeReadOnlySummaryBox(0);
        var infoTab = new TabPage("Package Info");
        infoTab.Controls.Add(_infoBox);
        _tabs.TabPages.Add(infoTab);

        // "App files" — ALL files directly in the files/ folder (not just bin/).
        // The user said: "load all the files directly from files folder not
        // bin folder". So this tab shows every file from the manifest's Files
        // list (they're all under files/ in the package — bin/ is just one
        // subpath). Rename from "Assemblies" to "App files".
        _appFilesGrid = MakeFileGrid();
        _tabs.TabPages.Add(MakeGridTab("App files", _appFilesGrid));

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
        // Package Info — no sensitive info (no git SHA, no component ID,
        // no registry details — user: "don't print any sensitive information
        // like git info, or component info").
        _infoBox.Text = BuildSummary(manifest);

        // App files — ALL files from the manifest (they're all in the files/
        // folder of the package). The user said: "load all the files directly
        // from files folder not bin folder" — so every file shows here.
        _appFilesGrid.Rows.Clear();
        _assetsGrid.Rows.Clear();
        _otherGrid.Rows.Clear();

        foreach (var f in manifest.Files)
        {
            var row = new[] { f.Path, FormatBytes(f.SizeBytes), f.Hash };
            if (f.Path.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
                _assetsGrid.Rows.Add(row);
            else
                _appFilesGrid.Rows.Add(row);
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
            _appFilesGrid.Rows.Clear();
            _assetsGrid.Rows.Clear();
            _dbGrid.Rows.Clear();
            _otherGrid.Rows.Clear();
            _messageLabel.ForeColor = Color.DimGray;
            _messageLabel.Text = "Pick a package file below — the integrity check runs automatically.";
        }
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
        var (package, matchedBy) = await MatchPackageAsync(store, manifest);

        // No offline registry / "Record as new" — user said no need for it.
        if (package is null)
        {
            // In offline mode (local file store), auto-create the record —
            // reusing the manifest's PackageId when the zip carries one, so
            // the deploy report flags THE row the Packager created in the
            // central registry (the local store just can't see it).
            if (Shell.OfflineMode)
            {
                package = await store.CreatePackageAsync(
                    manifest.ComponentId, manifest, packageId: manifest.PackageId);
                Shell.AppendLog($"No matching row — recorded as new package {package.PackageId}.");
                matchedBy = manifest.PackageId is null ? "new record (no id in manifest)" : "manifest PackageId (new local record)";
            }
            else if (manifest.PackageId is not null)
            {
                // The zip carries an explicit PackageId that is NOT in the
                // connected registry. Guessing by version+hash could flag the
                // WRONG row as Deployed — refuse with an actionable message
                // instead (wrong registry connection, or a package built by an
                // older Packager against a different registry).
                _messageLabel.ForeColor = Color.Firebrick;
                _messageLabel.Text =
                    $"Package '{manifest.PackageId}' (from the package manifest) was not found in the " +
                    $"connected registry (component '{manifest.ComponentId}', v{manifest.Version}).\n" +
                    "The deploy report would flag the wrong row — check the registry connection " +
                    "(the package was probably built against a different registry).";
                return;
            }
            else
            {
                _messageLabel.ForeColor = Color.Firebrick;
                _messageLabel.Text = $"Package not found in registry (v{manifest.Version}).";
                return;
            }
        }
        else if (manifest.PackageId is not null &&
                 !string.Equals(package.PackageId, manifest.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            // Defensive: the registry returned a row whose id differs from the
            // manifest's. Report the MANIFEST's id (that is the id the Packager
            // registered for this exact zip) — never the heuristic match.
            Shell.AppendLog(
                $"WARNING: registry matched package {package.PackageId} but the manifest carries {manifest.PackageId} — " +
                "using the manifest's PackageId for the deploy report.");
            package = package.WithPackageId(manifest.PackageId);
            matchedBy = "manifest PackageId (override)";
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
        Shell.AppendLog($"Package loaded: {manifest.Component} v{manifest.Version} ({package.PackageId}, {package.Status}; matched by {matchedBy}).");
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

    /// <summary>
    /// Finds the registry row for the loaded zip. When the manifest carries an
    /// explicit PackageId (all packages built by the current Packager), that id
    /// is looked up EXACTLY — no guessing. The version+first-file-hash
    /// heuristic remains only for legacy manifests without an id (matchedBy
    /// reports which path won, so the log always shows how the row was chosen).
    /// </summary>
    private static async Task<(PackageRecord? Package, string MatchedBy)> MatchPackageAsync(IRegistryStore store, ComponentManifest manifest)
    {
        if (manifest.PackageId is not null)
        {
            var byId = await store.GetPackageAsync(manifest.PackageId);
            if (byId is not null)
                return (byId, "manifest PackageId (exact)");
            return (null, "manifest PackageId (not found)");
        }

        var candidates = (await store.GetPackagesForComponentAsync(manifest.ComponentId))
            .Where(p => string.Equals(p.Version, manifest.Version, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return (null, "version+hash (no candidates)");
        var firstHash = manifest.Files.Count > 0 ? manifest.Files[0].Hash : null;
        foreach (var c in candidates)
        {
            try
            {
                var stored = ManifestSerializer.Deserialize(c.ManifestJson);
                var storedHash = stored.Files.Count > 0 ? stored.Files[0].Hash : null;
                if (storedHash is not null && string.Equals(storedHash, firstHash, StringComparison.OrdinalIgnoreCase))
                    return (c, "version+first-file-hash");
            }
            catch { }
        }
        if (candidates.Count == 1) return (candidates[0], "version (single candidate)");
        return (candidates.OrderBy(c => Math.Abs((c.CreatedUtc - manifest.CreatedUtc).Ticks)).First(), "version+created-date (closest)");
    }

    /// <summary>Package Info summary — NO sensitive info (no git SHA, no
    /// registry details, no health URL). User: "don't print any sensitive
    /// information like git info, or component info." The registry PackageId
    /// IS shown — operators validate the deploy report against the database
    /// with it (user request: "make sure that the PackageId exists in the
    /// manifest.json in the created package").</summary>
    private static string BuildSummary(ComponentManifest m)
    {
        var s = new StringBuilder();
        s.AppendLine($"Version:    {m.Version}");
        if (m.PackageId is not null)
            s.AppendLine($"Package Id: {m.PackageId}");
        s.AppendLine($"Created:    {m.CreatedUtc:yyyy-MM-dd HH:mm:ss}");
        s.AppendLine($"Framework:  {m.TargetFramework}{(m.IsSelfContained ? ", self-contained" : ", framework-dependent")}");
        s.AppendLine($"Files:      {m.Files.Count} changed/new ({FormatBytes(m.Files.Sum(f => f.SizeBytes))})");
        s.AppendLine($"Config:     {(m.AppSettingsDelta.Count == 0 ? "no appsettings delta" : $"{m.AppSettingsDelta.Count} key(s)")}");
        s.AppendLine($"DB scripts: {(m.DbScripts.Count == 0 ? "none" : string.Join("; ", m.DbScripts.Select(x => $"{x.File} ({x.Kind})")))}");
        return s.ToString();
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F1} MB"
        : bytes >= 1 << 10 ? $"{bytes / (double)(1 << 10):F1} KB"
        : $"{bytes} B";
}
