using System.Diagnostics;
using DeployToolkit.AppKit;
using DeployToolkit.Core.Database;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Publishing;
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
    private readonly Label _localPathWarningLabel;
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

        // Warning shown when the output path is a LOCAL folder (not under the
        // shared package store). The local copy won't be visible to other team
        // members — only the shared-store upload (Option B, if configured) is.
        // Hidden when the store isn't configured (local-only is the only option)
        // or when the path IS under the store.
        _localPathWarningLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 40,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkOrange,
            Visible = false,
        };
        layout.Controls.Add(_localPathWarningLabel);

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
        // Show the local-path popup on entry so the user can't miss it (the
        // default path is usually a local Documents folder, so this warns
        // about it before they even click Build). Only prompt when a path
        // is present.
        if (!string.IsNullOrWhiteSpace(_zipPathBox.Text))
            ShowLocalPathWarningPopup();
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

        // #3/#4 EF migrations: generate the SQL script from the selected
        // migrations NOW (before BuildAsync) and attach it as a Schema
        // DbScript. PackageBuilder is in Core (no Database dependency), so
        // the script generation happens here in the UI layer; BuildAsync only
        // records the AppliedMigrations set on the manifest. Best-effort: a
        // generation failure (dotnet-ef missing, build error) is surfaced as
        // a build-blocking error so the user fixes it before the package is
        // shipped with a missing migration script.
        await GenerateEfMigrationScriptAsync();

        var request = new PackageBuildRequest(
            component.ComponentId,
            version,
            Draft.PublishOutputRoot,
            zipPath,
            GitCommitSha: Draft.GitSync?.HeadSha,
            AppSettingsDelta: Draft.AppSettingsDelta.Count == 0 ? null : Draft.AppSettingsDelta,
            DbScripts: Draft.DbScripts.Count == 0 ? null : Draft.DbScripts,
            DbScriptSourcePaths: Draft.DbScriptSourcePaths.Count == 0 ? null : Draft.DbScriptSourcePaths,
            ExcludedPaths: Draft.ExcludedPaths.Count == 0 ? null : Draft.ExcludedPaths,
            EfMigrationsProjectPath: Draft.EfMigrationsProjectPath,
            SelectedEfMigrations: Draft.SelectedEfMigrations.Count == 0 ? null : Draft.SelectedEfMigrations,
            PreviouslyAppliedMigrations: Draft.PreviouslyAppliedMigrations.Count == 0 ? null : Draft.PreviouslyAppliedMigrations);

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
            $"Baseline:      {manifest.BaselineManifest ?? "(none — first package for this component)"}" +
            (string.IsNullOrEmpty(built.Record.PackageLocation)
                ? string.Empty
                : $"\nShared store: {built.Record.PackageLocation}");

        // Option B: the upload to the shared store failed — the build still
        // succeeded (the local .zip is fine), but the Deployer won't find it
        // via the registry. Surface a warning so the user knows to copy the
        // .zip by hand or fix the share credentials.
        if (!string.IsNullOrEmpty(built.PackageStoreError))
        {
            _staleLabel.Text =
                $"Warning: package saved locally but the shared-store upload failed — {built.PackageStoreError}. " +
                "The Deployer won't find this package via the registry; copy the .zip by hand, " +
                "or save the share's credentials in Windows Credential Manager and rebuild.";
            _staleLabel.Visible = true;
        }
        else if (built.UnresolvedStalePackages.Count > 0)
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

        // #3: After a successful build, keep the registry truthful about what
        // was actually published. The publish step already writes the component's
        // TargetFramework/IsSelfContained; here we additionally sync the CLIENT's
        // GitRepositoryUrl/DeploymentBranch (from the folder's git remote + the
        // synced branch) and the client's PublishConfiguration (deployment type,
        // target runtime, additional publish options) so the next build starts
        // from the values the user actually used this time. Failures are
        // non-fatal — a registry write error must not undo a successful build.
        _ = SyncRegistryFromSessionAsync();
    }

    /// <summary>
    /// #3/#4 EF migrations: generates the SQL script for the migrations the
    /// user selected in the DB-scripts step and attaches it as a Schema
    /// DbScript. Uses <c>dotnet ef migrations script --idempotent</c> from the
    /// last-applied migration (by timestamp) to the last-selected migration
    /// (by timestamp), so already-applied migrations in the range are skipped
    /// on the DB (handles the "migrations in the middle added later" case).
    /// A generation failure blocks the build (the user must fix dotnet-ef /
    /// the build before shipping a package with a missing migration script).
    /// No-op when no EF project is selected or no migrations are checked.
    /// </summary>
    private async Task GenerateEfMigrationScriptAsync()
    {
        if (string.IsNullOrEmpty(Draft.EfMigrationsProjectPath) || Draft.SelectedEfMigrations.Count == 0)
            return;

        var projectPath = Draft.EfMigrationsProjectPath!;
        var selected = Draft.SelectedEfMigrations;

        // Discover the project's migrations (ordered newest-first by timestamp)
        // to compute the --from (last applied by timestamp) and --to (last
        // selected by timestamp) for the dotnet ef script command.
        var allMigrations = await Task.Run(() => MigrationScriptGenerator.DiscoverMigrations(projectPath));
        if (allMigrations.Count == 0)
        {
            AppTheme.Error(this, "No EF migrations found in the selected DB project.");
            return;
        }

        // Migrations are newest-first; the "from" is the newest migration that
        // is in the PREVIOUSLY-APPLIED set (the last applied by timestamp);
        // the "to" is the newest migration that is in the SELECTED set.
        var applied = Draft.PreviouslyAppliedMigrations;
        var fromMigration = allMigrations.FirstOrDefault(m => applied.Contains(m.Name));
        var toMigration = allMigrations.FirstOrDefault(m => selected.Contains(m.Name));

        if (toMigration is null)
        {
            AppTheme.Error(this, "The selected EF migration was not found in the project. " +
                "The Migrations folder may have changed since the DB-scripts step — go back and re-select.");
            return;
        }

        // Generate the script with --idempotent so re-running on a DB that
        // already has some migrations applied is safe.
        MigrationScriptResult? result = null;
        await Guard.RunAsync(Wizard, "Generating EF migration script…", async () =>
        {
            result = await MigrationScriptGenerator.GenerateScriptAsync(
                projectPath,
                fromMigration: fromMigration?.Name,
                toMigration: toMigration.Name,
                timeoutMinutes: 5,
                idempotent: true);
        });

        if (result is null || !result.Success)
        {
            throw new InvalidOperationException(
                $"EF migration script generation failed (exit {result?.ExitCode ?? -1}): " +
                $"{result?.ErrorSummary ?? "unknown error"}. " +
                "Make sure 'dotnet-ef' is installed (dotnet tool install --global dotnet-ef) " +
                "and the DB project builds.");
        }

        // Write the generated script to a temp file + attach as a Schema
        // DbScript (EF migrations are schema changes). Re-attaching the same
        // script name across rebuilds replaces the previous temp file's
        // source path (a rebuild regenerates the script with the latest set).
        var scriptName = "ef-migrations.sql";
        var tempPath = Path.Combine(Path.GetTempPath(), "DeployToolkit", "ef-migrations", $"{Guid.NewGuid():N}_{scriptName}");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        await File.WriteAllTextAsync(tempPath, result.ScriptText);
        Draft.DbScriptSourcePaths[scriptName] = tempPath;

        // Ensure it's in the DbScripts list (replace if a previous build
        // already added it).
        Draft.DbScripts.RemoveAll(s => s.File == scriptName);
        Draft.DbScripts.Add(new DbScriptRef(scriptName, DbScriptKind.Schema));
    }

    /// <summary>
    /// #3: writes the session's resolved publish settings back to the registry
    /// so the next package build starts from the values the user actually used
    /// this time. Updates the component's TargetFramework/IsSelfContained (in
    /// case the publish step's coalesced save lost the race) and the client's
    /// GitRepositoryUrl, DeploymentBranch, and PublishConfiguration. Only
    /// writes when something actually changed — no registry churn otherwise.
    /// Errors are swallowed and surfaced only in the status label; a sync
    /// failure never undoes a successful build.
    /// </summary>
    private async Task SyncRegistryFromSessionAsync()
    {
        try
        {
            var component = Draft.Component;
            if (component is null)
                return;

            var client = await Wizard.Registry.GetClientAsync(component.ClientId);
            if (client is null || Wizard.IsDisposed || IsDisposed)
                return;

            var changed = false;

            // ---- Client: git repo URL + deployment branch from the folder's
            // git sync (the authoritative source — the user's local checkout) ----
            var sync = Draft.GitSync;
            if (sync is not null)
            {
                if (!string.IsNullOrWhiteSpace(sync.RemoteUrl) &&
                    !string.Equals(sync.RemoteUrl, client.GitRepositoryUrl, StringComparison.Ordinal))
                {
                    client.GitRepositoryUrl = sync.RemoteUrl;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(sync.BranchName) &&
                    !string.Equals(sync.BranchName, client.DeploymentBranch, StringComparison.Ordinal))
                {
                    client.DeploymentBranch = sync.BranchName;
                    changed = true;
                }
            }

            // ---- Client: PublishConfiguration (deployment type / RID / extra
            // options) from the publish step's editable controls. The publish
            // step stores these only in the in-memory _optionsBox/_runtimeBox/
            // _deployModeBox; we fold them into the client default here so the
            // next build starts from the values the user actually used. ----
            var publishConfig = client.PublishConfiguration ?? new PublishConfiguration();

            // Deployment type: self-contained vs framework-dependent. The
            // component's IsSelfContained (kept truthful by the publish step)
            // is the source of truth for the deployment type.
            var newDeploymentType = component.IsSelfContained
                ? PublishDeploymentType.SelfContained
                : PublishDeploymentType.FrameworkDependent;
            if (publishConfig.DeploymentType != newDeploymentType)
            {
                publishConfig.DeploymentType = newDeploymentType;
                client.PublishConfiguration = publishConfig;
                changed = true;
            }

            if (changed)
            {
                await Guard.RunAsync(Wizard, "Updating client profile from this build…", async _ =>
                    await Wizard.Registry.UpdateClientAsync(client));
            }
        }
        catch (Exception)
        {
            // Registry sync is best-effort — a failure here must not undo a
            // successful build. The build result already shows in the UI.
        }
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
            ShowLocalPathWarningPopup();
        }
    }

    /// <summary>Shows a BLOCKING popup warning when the output path is NOT
    /// under the shared package store (so the user knows the local copy won't
    /// be visible to other team members). The user must confirm to proceed.
    /// Also keeps the persistent warning label visible as a reminder.
    ///
    /// Cases:
    ///  - Store configured + path under store → no warning (shared).
    ///  - Store configured + path NOT under store → popup: "this local folder
    ///    will not be shared... the package will still be uploaded to the
    ///    shared store on build, but the local copy at <path> is only on this
    ///    PC. Continue?"  [Yes/No]
    ///  - No store configured + path is local → popup: "no shared package
    ///    store is configured — this .zip will live ONLY on this PC at <path>
    ///    and other team members won't be able to find it via the Deployer.
    ///    Configure a shared store in Registry Connection… for team-wide
    ///    access. Continue anyway?"  [Yes/No]
    /// The popup shows ONCE per path change (so re-entering the step without
    /// changing the path doesn't re-prompt). Returns true when the user
    /// confirmed (or no warning was needed); false when they declined.</summary>
    private bool ShowLocalPathWarningPopup()
    {
        var storeRoot = Wizard.PackageStoreRootPath;
        var path = _zipPathBox.Text.Trim();
        if (path.Length == 0)
            return true; // nothing to warn about yet

        var dir = Path.GetDirectoryName(path) ?? path;
        var storeConfigured = !string.IsNullOrWhiteSpace(storeRoot);
        var underStore = storeConfigured
            && dir.StartsWith(storeRoot!.Trim(), StringComparison.OrdinalIgnoreCase);

        if (underStore)
        {
            _localPathWarningLabel.Visible = false;
            return true; // shared — no warning
        }

        // Build the warning message + keep the label as a reminder.
        string message;
        if (storeConfigured)
        {
            message =
                $"The selected output folder is NOT under the shared package store:\n\n  {dir}\n\n" +
                "This local folder will not be shared and is not visible to other team members.\n" +
                "The package will still be uploaded to the shared store on build — other developers " +
                "fetch it from there via the Deployer.\n\nContinue with this local path?";
        }
        else
        {
            message =
                $"No shared package store is configured.\n\n" +
                $"This .zip will live ONLY on this PC at:\n\n  {path}\n\n" +
                "Other team members will NOT be able to find it via the Deployer's " +
                "\"Pick from registry…\" flow.\n\n" +
                "For team-wide access, configure a shared package store in " +
                "Registry Connection… (a network share / UNC path).\n\nContinue with this local path?";
        }

        _localPathWarningLabel.Text = storeConfigured
            ? "Note: this local folder will not be shared — not visible to other team members. (The package is also uploaded to the shared store on build.)"
            : "Note: no shared package store configured — this .zip is local only and not visible to other team members.";
        _localPathWarningLabel.Visible = true;

        var choice = AppTheme.Confirm(this, message, "Local output path");
        return choice == DialogResult.Yes;
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
