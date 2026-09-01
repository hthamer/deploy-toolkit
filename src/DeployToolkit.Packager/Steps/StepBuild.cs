using System.Diagnostics;
using DeployToolkit.AppKit;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 6 (plan §10 step 7): writes the delta.zip + manifest via
/// <c>PackageBuilder.BuildAsync</c> and records the Created package in the
/// registry. The Finish button (in the wizard's bottom bar) unlocks only
/// after a successful build; lingering Created packages are surfaced as an
/// orange warning since they will not become diff baselines.
/// </summary>
internal sealed class StepBuild : WizardStep
{
    private readonly TextBox _zipPathBox;
    private readonly Button _buildButton;
    private readonly Label _resultLabel;
    private readonly Label _staleLabel;
    private readonly Button _openFolderButton;
    private readonly Button _copyPathButton;

    private string? _clientName;

    public StepBuild(PackagerWizardForm wizard, PackageDraft draft)
        : base(wizard, draft)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(AppTheme.MakeSectionLabel("Output package (.zip)"));

        var zipRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        zipRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        zipRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _zipPathBox = new TextBox { Dock = DockStyle.Fill };
        var browseButton = new Button { Text = "Browse…" };
        AppTheme.StyleButton(browseButton);
        browseButton.Click += (_, _) => PickZipPath();
        zipRow.Controls.Add(_zipPathBox, 0, 0);
        zipRow.Controls.Add(browseButton, 1, 0);
        layout.Controls.Add(zipRow);

        var actionRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 4, 0, 4),
            WrapContents = false,
        };
        _buildButton = new Button { Text = "Build package" };
        AppTheme.StyleButton(_buildButton);
        _buildButton.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold);
        _buildButton.Click += (_, _) => Guard.FireAndForget(Wizard, "Building package…", BuildAsync);
        _openFolderButton = new Button { Text = "Open folder", Enabled = false };
        AppTheme.StyleButton(_openFolderButton);
        _openFolderButton.Click += (_, _) => OpenOutputFolder();
        _copyPathButton = new Button { Text = "Copy path", Enabled = false };
        AppTheme.StyleButton(_copyPathButton);
        _copyPathButton.Click += (_, _) => CopyZipPath();
        actionRow.Controls.Add(_buildButton);
        actionRow.Controls.Add(_openFolderButton);
        actionRow.Controls.Add(_copyPathButton);
        layout.Controls.Add(actionRow);

        _resultLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 84,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            ForeColor = Color.Black,
        };
        layout.Controls.Add(_resultLabel);

        _staleLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 56,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkOrange,
            Visible = false,
        };
        layout.Controls.Add(_staleLabel);

        Controls.Add(layout);
    }

    public override string Title => "6. Build package";

    public override string Hint =>
        "Write delta.zip + manifest.json and record the package as Created in the registry. Finish unlocks after a successful build.";

    public override bool CanProceed => Draft.BuildResult is not null;

    public override async void OnEnter()
    {
        // Any earlier build result is stale once this step is visited again
        // (state may have changed upstream) — the user must rebuild to finish.
        Draft.BuildResult = null;
        _resultLabel.Text = string.Empty;
        _staleLabel.Visible = false;
        _openFolderButton.Enabled = false;
        _copyPathButton.Enabled = false;

        if (Draft.Component is not null && _clientName is null)
        {
            var componentId = Draft.Component.ComponentId;
            var clientId = Draft.Component.ClientId;
            await Guard.RunAsync(Wizard, "Loading client…", async () =>
            {
                var client = await Wizard.Registry.GetClientAsync(clientId);
                if (client is not null && Draft.Component?.ComponentId == componentId)
                    _clientName = client.Name;
            });
            if (IsDisposed || Wizard.IsDisposed)
                return; // the wizard closed mid-load — nothing to fill
        }

        _zipPathBox.Text = Draft.OutputZipPath ?? DefaultZipPath();
        Wizard.OnDraftChanged();
    }

    public override void OnLeave() => Draft.OutputZipPath = string.IsNullOrWhiteSpace(_zipPathBox.Text) ? null : _zipPathBox.Text.Trim();

    private async Task BuildAsync()
    {
        var component = Draft.Component;
        if (component is null || !Draft.PublishSuccess || Draft.PublishOutputRoot is null)
        {
            AppTheme.Error(this, "Publish has not succeeded yet — go back to step 2 and run publish.");
            return;
        }

        var version = Draft.Version?.Trim();
        if (string.IsNullOrEmpty(version))
        {
            AppTheme.Error(this, "Version is missing — go back to step 2 and enter one.");
            return;
        }

        var zipPath = _zipPathBox.Text.Trim();
        if (zipPath.Length == 0)
        {
            AppTheme.Error(this, "Output zip path is required.");
            return;
        }

        Draft.OutputZipPath = zipPath;

        // The delta step commits on leave, but commit defensively so the very
        // latest grid edits are always included in the request.
        if (_deltaCommit is { } commit)
            commit();

        var request = new PackageBuildRequest(
            component.ComponentId,
            version,
            Draft.PublishOutputRoot,
            zipPath,
            GitCommitSha: Draft.GitSync?.HeadSha,
            AppSettingsDelta: Draft.AppSettingsDelta.Count == 0 ? null : Draft.AppSettingsDelta,
            DbScripts: Draft.DbScripts.Count == 0 ? null : Draft.DbScripts,
            DbScriptSourcePaths: Draft.DbScriptSourcePaths.Count == 0 ? null : Draft.DbScriptSourcePaths,
            ExcludedPaths: Draft.ExcludedPaths.Count == 0 ? null : Draft.ExcludedPaths);

        PackageBuildResult? result = null;
        await Guard.RunAsync(Wizard, "Building package…", async cancellationToken =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
            // BuildAsync hashes the publish output, diffs it, and writes the
            // zip — minutes of work for a self-contained publish. It must not
            // run on the UI thread (user-reported freeze class); the
            // cancellation token lets the busy dialog free the UI, and the
            // late token check prevents an abandoned build from touching it.
            result = await Task.Run(() => Wizard.Builder.BuildAsync(request), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                result = null;
        });

        if (result is not { } built)
            return; // Guard reported the failure — stay on this step

        if (IsDisposed || Wizard.IsDisposed)
            return; // the wizard closed mid-build — nothing left to update

        Draft.BuildResult = built;

        var manifest = built.Manifest;
        _resultLabel.Text =
            $"Package id:    {built.Record.PackageId}\n" +
            $"Zip:           {built.ZipPath}\n" +
            $"Manifest:      {manifest.Files.Count} changed/new file(s), {manifest.DeletedFiles.Count} deleted\n" +
            $"Baseline:      {manifest.BaselineManifest ?? "(none — first package for this component)"}";

        if (built.UnresolvedStalePackages.Count > 0)
        {
            var versions = string.Join(", ", built.UnresolvedStalePackages.Select(p => p.Version));
            _staleLabel.Text =
                $"Warning: {built.UnresolvedStalePackages.Count} earlier package(s) for this component are still " +
                $"Created ({versions}) — resolve them on the Clients screen or they will not become diff baselines.";
            _staleLabel.Visible = true;
        }
        else
        {
            _staleLabel.Visible = false;
        }

        _openFolderButton.Enabled = true;
        _copyPathButton.Enabled = true;
        Wizard.OnDraftChanged();
    }

    private Action? _deltaCommit;

    /// <summary>Wired by the wizard after all steps exist: lets this step ask
    /// the delta step to flush its grid into the draft.</summary>
    public void SetDeltaCommit(Action commit) => _deltaCommit = commit;

    private void PickZipPath()
    {
        using var picker = new SaveFileDialog
        {
            Title = "Output package",
            Filter = "Zip package (*.zip)|*.zip",
            OverwritePrompt = true,
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
        {
            _zipPathBox.Text = picker.FileName;
            Draft.OutputZipPath = picker.FileName;
        }
    }

    private void OpenOutputFolder()
    {
        var zipPath = _zipPathBox.Text.Trim();
        if (zipPath.Length == 0 || !File.Exists(zipPath))
        {
            AppTheme.Error(this, "The package has not been built yet.");
            return;
        }

        try
        {
            // Windows-only exe — explorer.exe always exists where this runs.
            Process.Start("explorer.exe", $"/select,\"{zipPath}\"");
        }
        catch (Exception ex)
        {
            AppTheme.Error(this, $"Could not open the folder: {ex.Message}");
        }
    }

    private void CopyZipPath()
    {
        var zipPath = _zipPathBox.Text.Trim();
        if (zipPath.Length == 0)
            return;

        try
        {
            Clipboard.SetText(zipPath);
        }
        catch (Exception ex)
        {
            // Clipboard can be momentarily locked by another process.
            AppTheme.Error(this, $"Could not copy to the clipboard: {ex.Message}");
        }
    }

    private string DefaultZipPath()
    {
        var component = Draft.Component;
        var clientName = string.IsNullOrWhiteSpace(_clientName) ? "Client" : _clientName!;
        var componentName = component?.Name ?? "Component";
        var version = string.IsNullOrWhiteSpace(Draft.Version) ? "0.0.0" : Draft.Version!;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.CurrentDirectory;

        return Path.Combine(documents, "DeployToolkit", "Packages", clientName, componentName, $"{componentName}-{version}.zip");
    }
}
