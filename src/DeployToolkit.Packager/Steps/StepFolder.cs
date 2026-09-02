using DeployToolkit.AppKit;
using DeployToolkit.Core.Git;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 1 (plan §10 step 1 + §5): pick the project's git working folder →
/// fetch/pull via <see cref="GitSyncPresenter"/> → auto-resolve the
/// component for the folder (prompting through
/// <see cref="ComponentPickerDialog"/> the first time) → surface stale
/// (Created-but-undeployed) packages through <see cref="StalePackagesDialog"/>.
/// Non-git folders are allowed after an explicit confirmation (no commit
/// SHA is recorded then).
/// </summary>
internal sealed class StepFolder : WizardStep
{
    private readonly TextBox _folderBox;
    private readonly Button _browseButton;
    private readonly Button _changeComponentButton;
    private readonly Label _gitSummary;
    private readonly Label _componentSummary;
    private readonly CheckBox _fetchPullBox;

    /// <summary>Guards against a stale (cancelled/abandoned) folder run
    /// clobbering a newer selection's results when its background work
    /// finally completes.</summary>
    private int _selectRunId;

    /// <summary>Lightweight, network-free repo info (remote URL + branch +
    /// HEAD) read immediately when a folder is selected, so the Git box can
    /// show the repo URL before/without running the fetch+pull sync. Null
    /// when the folder isn't a git repo (or the read failed).</summary>
    private GitRepoInfo? _repoInfo;

    public StepFolder(PackagerWizardForm wizard, PackageDraft draft)
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

        layout.Controls.Add(AppTheme.MakeSectionLabel("Project folder (git working folder — not a publish output)"));

        var folderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _folderBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        _browseButton = new Button { Text = "Browse…" };
        AppTheme.StyleButton(_browseButton);
        _browseButton.Click += (_, _) => BrowseForFolder();
        folderRow.Controls.Add(_folderBox, 0, 0);
        folderRow.Controls.Add(_browseButton, 1, 0);
        layout.Controls.Add(folderRow);

        _gitSummary = new Label
        {
            Text = "No folder selected yet.",
            AutoSize = false,
            Height = 74, // Branch+HEAD / Sync outcome / Remote (up to 3 lines)
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };

        // "Fetch & Pull Remote Changes" checkbox — true by default. When
        // unchecked, selecting a git folder skips the fetch+pull sync
        // entirely (no network) and the package uses the current local
        // state as-is. The repo URL + branch + HEAD are still shown (read
        // locally, no network) so the user sees what they're packaging.
        _fetchPullBox = new CheckBox
        {
            Text = "Fetch & Pull Remote Changes",
            Checked = true,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 2, 0, 4),
        };

        var gitGroup = new GroupBox
        {
            Text = "Git",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8),
            Controls = { _gitSummary, _fetchPullBox },
        };
        layout.Controls.Add(gitGroup);

        _componentSummary = new Label
        {
            Text = "No component resolved yet.",
            AutoSize = false,
            Height = 84,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };

        // The folder→component mapping auto-resolves the LAST USED component;
        // "Change…" lets the user swap it without re-picking the folder.
        _changeComponentButton = new Button { Text = "Change…", Enabled = false };
        AppTheme.StyleButton(_changeComponentButton);
        _changeComponentButton.Click += (_, _) => _ = ChangeComponentAsync();

        var componentRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true };
        componentRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        componentRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        componentRow.Controls.Add(_componentSummary, 0, 0);
        componentRow.Controls.Add(_changeComponentButton, 1, 0);

        var componentGroup = new GroupBox
        {
            Text = "Component",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8),
            Controls = { componentRow },
        };
        layout.Controls.Add(componentGroup);

        Controls.Add(layout);
    }

    public override string Title => "1. Folder & component";

    public override string Hint =>
        "Pick the project's git working folder. The tool fetches/pulls, resolves the component for the folder " +
        "(first time: pick or create one — use Change… to swap it later) and checks for stale undeployed packages.";

    public override bool CanProceed => Draft.FolderPath is not null && Draft.Component is not null;

    public override void OnEnter() => UpdateSummary();

    private void BrowseForFolder()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Select the project's git working folder",
            ShowNewFolderButton = false,
        };
        if (Directory.Exists(_folderBox.Text))
            picker.SelectedPath = _folderBox.Text;

        if (picker.ShowDialog(this) == DialogResult.OK)
            _ = SelectFolderAsync(picker.SelectedPath);
    }

    private async Task SelectFolderAsync(string folder)
    {
        var runId = ++_selectRunId;
        bool Stale() => runId != _selectRunId || Wizard.IsDisposed;

        _browseButton.Enabled = false;
        try
        {
            // Read the repo's basic facts (remote URL + branch + HEAD)
            // immediately — network-free — so the Git box shows the repo
            // URL the moment the folder is picked, before any fetch+pull.
            // Stored in _repoInfo so UpdateSummary can render it even when
            // the sync is skipped (non-git folder, or checkbox unchecked).
            _repoInfo = await Task.Run(() => GitRemoteUrlReader.Read(folder));
            if (Stale()) return;
            UpdateGitSummary(); // show the URL + branch + HEAD right away

            // ---- git sync (gated by the "Fetch & Pull Remote Changes" checkbox) ----
            // The checkbox defaults ON. When OFF, skip the fetch+pull entirely
            // — the package uses the current local state as-is (the repo URL +
            // branch + HEAD above are still shown so the user sees what they
            // package). When the folder isn't a git repo, the user confirms
            // "continue without git sync" as before.
            GitSyncResult? git = null;
            if (!IsGitWorkingFolder(folder))
            {
                var proceed = AppTheme.Confirm(Wizard,
                    $"'{folder}' is not a git working folder.\n\n" +
                    "Continue without git sync? (no commit SHA will be recorded)");
                if (proceed != DialogResult.Yes)
                    return;
            }
            else if (!_fetchPullBox.Checked)
            {
                // Git folder, but the user opted out of fetch+pull. Build a
                // GitSyncResult from the local repo info so the manifest still
                // records the branch + HEAD SHA (no commit movement possible).
                git = BuildLocalOnlySyncResult(_repoInfo);
            }
            else
            {
                git = await GitSyncPresenter.SynchronizeWithUiAsync(Wizard, new LibGit2Synchronizer(), folder);
                if (Stale()) return; // a newer selection superseded this run
                if (git is null)
                    return; // user cancelled (or the sync failed and Guard reported it)
            }

            // ---- component resolution (mapping store + registry IO) ----
            DeploymentComponent? component = null;
            var needsPicker = false;
            await Guard.RunAsync(Wizard, "Resolving component for this folder…", async _ =>
            {
                try
                {
                    component = await Wizard.Builder.ResolveComponentForFolderAsync(folder);
                }
                catch (ComponentNotResolvedException)
                {
                    needsPicker = true; // first time this folder is packaged — ask the user
                }
            });
            if (Stale()) return;

            if (needsPicker)
            {
                using var picker = new ComponentPickerDialog(Wizard.Registry, Wizard.Builder, folder);
                if (picker.ShowDialog(Wizard) == DialogResult.OK && picker.ResolvedComponent is { } picked)
                    component = picked;
                if (Stale()) return;
            }

            if (component is not { } resolved)
                return; // still unresolved (cancelled) — stay on this step

            // ---- stale packages (registry IO) — proceed regardless, "Ignore" is valid ----
            IReadOnlyList<PackageRecord> stale = Array.Empty<PackageRecord>();
            await Guard.RunAsync(Wizard, "Checking for stale (created-but-undeployed) packages…", async _ =>
                stale = await Wizard.Builder.CheckForStalePackagesAsync(resolved.ComponentId));
            if (Stale()) return;

            if (stale.Count > 0)
            {
                using var staleDialog = new StalePackagesDialog(Wizard.Registry, stale);
                staleDialog.ShowDialog(Wizard);
            }

            Draft.FolderPath = folder;
            Draft.GitSync = git;
            Draft.Component = resolved;
            UpdateSummary();
            Wizard.OnDraftChanged();
        }
        catch (OperationCanceledException)
        {
            // deliberate cancellation — not an error
        }
        catch (Exception ex)
        {
            if (!Wizard.IsDisposed)
                AppTheme.Error(Wizard, ex, "Folder preparation failed");
        }
        finally
        {
            if (!Stale()) // a newer run owns the button state now
                _browseButton.Enabled = true;
        }
    }

    /// <summary>"Change…" — re-opens the component picker for the CURRENT
    /// folder so a saved (last-used) mapping can be swapped: picking OK
    /// re-registers the folder→component mapping and refreshes the draft.
    /// The stale-package check re-runs for the newly chosen component.</summary>
    private async Task ChangeComponentAsync()
    {
        if (Draft.FolderPath is not { } folder || Wizard.IsDisposed)
            return;

        try
        {
            using var picker = new ComponentPickerDialog(Wizard.Registry, Wizard.Builder, folder);
            if (picker.ShowDialog(Wizard) != DialogResult.OK || picker.ResolvedComponent is not { } picked)
                return;

            if (string.Equals(picked.ComponentId, Draft.Component?.ComponentId, StringComparison.Ordinal))
                return; // same component — nothing changed

            Draft.Component = picked;
            UpdateSummary();
            Wizard.OnDraftChanged();

            IReadOnlyList<PackageRecord> stale = Array.Empty<PackageRecord>();
            await Guard.RunAsync(Wizard, "Checking for stale (created-but-undeployed) packages…", async _ =>
                stale = await Wizard.Builder.CheckForStalePackagesAsync(picked.ComponentId));
            if (Wizard.IsDisposed)
                return;

            if (stale.Count > 0)
            {
                using var staleDialog = new StalePackagesDialog(Wizard.Registry, stale);
                staleDialog.ShowDialog(Wizard);
            }
        }
        catch (Exception ex)
        {
            if (!Wizard.IsDisposed)
                AppTheme.Error(Wizard, ex, "Component change failed");
        }
    }

    private void UpdateSummary()
    {
        // The folder textbox shows the selected path so the user can see
        // (and copy) what was picked — mirrors the Visual Studio "Folder"
        // publish profile UX where the path is visible next to Browse.
        _folderBox.Text = Draft.FolderPath ?? string.Empty;

        UpdateGitSummary();

        if (Draft.Component is { } component)
        {
            _componentSummary.ForeColor = Color.Black;
            _componentSummary.Text = BuildComponentSummary(clientName: null, component);
            _ = LoadClientNameAsync(component.ComponentId, component.ClientId);
        }
        else
        {
            _componentSummary.Text = "No component resolved yet.";
            _componentSummary.ForeColor = Color.DimGray;
        }

        _changeComponentButton.Enabled = Draft.FolderPath is not null && !Wizard.IsDisposed;
    }

    /// <summary>Renders the Git box. Prefers the sync result's RemoteUrl
    /// (authoritative — the sync actually talked to the remote); falls back
    /// to the network-free _repoInfo read (so the URL shows immediately on
    /// folder selection, even when the sync was skipped or hasn't run yet).
    /// Shows "no git sync" when neither is available but a folder is picked.
    /// </summary>
    private void UpdateGitSummary()
    {
        // A finished sync result wins — it carries the branch + HEAD the
        // package is actually built from, plus the sync outcome line.
        if (Draft.GitSync is { } sync)
        {
            _gitSummary.ForeColor = Color.Black;
            _gitSummary.Text = $"Branch: {sync.BranchName}    HEAD: {ShortSha(sync.HeadSha)}\n" +
                               $"Sync outcome: {DescribeOutcome(sync.Outcome, sync.Pulled)}" +
                               (string.IsNullOrEmpty(sync.RemoteUrl)
                                   ? (string.IsNullOrEmpty(_repoInfo?.RemoteUrl) ? string.Empty : $"\nRemote: {_repoInfo!.RemoteUrl}")
                                   : $"\nRemote: {sync.RemoteUrl}");
            return;
        }

        // No sync result — show the local repo info (URL + branch + HEAD)
        // read immediately on folder selection, so the user sees the repo
        // URL right away even before/without running fetch+pull.
        if (_repoInfo is { } info)
        {
            _gitSummary.ForeColor = Color.Black;
            _gitSummary.Text = $"Branch: {info.BranchName}    HEAD: {ShortSha(info.HeadSha)}\n" +
                               (string.IsNullOrEmpty(info.RemoteUrl)
                                   ? "No remote configured."
                                   : $"Remote: {info.RemoteUrl}") +
                               (_fetchPullBox.Checked ? string.Empty : "\nSync skipped (Fetch & Pull unchecked) — packaging local state as-is.");
            return;
        }

        if (Draft.FolderPath is not null)
        {
            _gitSummary.ForeColor = Color.DimGray;
            _gitSummary.Text = "No git sync — no commit SHA will be recorded for this package.";
        }
        else
        {
            _gitSummary.ForeColor = Color.DimGray;
            _gitSummary.Text = "No folder selected yet.";
        }
    }

    /// <summary>Builds a <see cref="GitSyncResult"/> from the local-only repo
    /// info (no fetch, no pull) used when the user unchecked
    /// "Fetch & Pull Remote Changes". Carries the branch + HEAD SHA so the
    /// manifest still records what was packaged; outcome is UpToDate (no
    /// commit movement possible) and the dirty/untracked lists are empty
    /// (the sync never inspected the tree — the user explicitly opted out).
    /// </summary>
    private static GitSyncResult? BuildLocalOnlySyncResult(GitRepoInfo? info)
    {
        if (info is null)
            return null;
        return new GitSyncResult(
            RepositoryPath: string.Empty,
            BranchName: info.BranchName,
            HeadSha: info.HeadSha,
            HeadShaBeforeSync: info.HeadSha,
            Outcome: GitSyncOutcome.UpToDate,
            UncommittedFiles: Array.Empty<string>(),
            UntrackedFiles: Array.Empty<string>(),
            RemoteUrl: info.RemoteUrl);
    }

    private async Task LoadClientNameAsync(string componentId, string clientId)
    {
        await Guard.RunAsync(Wizard, "Loading client…", async _ =>
        {
            var client = await Wizard.Registry.GetClientAsync(clientId);
            if (client is null || Draft.Component is not { } component || component.ComponentId != componentId)
                return;

            _componentSummary.Text = BuildComponentSummary(client.Name, component);
        });
    }

    private static string BuildComponentSummary(string? clientName, DeploymentComponent component) =>
        $"Client: {clientName ?? "(loading…)"}    Component: {component.Name}\n" +
        $"Target: {component.TargetType}    Framework: {component.TargetFramework}    " +
        $"Self-contained: {(component.IsSelfContained ? "yes" : "no")}\n" +
        $"Health check URL: {component.HealthCheckUrl ?? "(none)"}    DB connection ref: {component.DbConnectionRef ?? "(none)"}";

    private static string DescribeOutcome(GitSyncOutcome outcome, bool pulled) => outcome switch
    {
        GitSyncOutcome.UpToDate => pulled ? "pulled (branch moved)" : "branch up to date with origin",
        GitSyncOutcome.FastForwarded => "pulled — fast-forwarded to origin's tip",
        GitSyncOutcome.FetchedOnly => "fetched only (HEAD untouched)",
        GitSyncOutcome.SkippedDirtyTree => "pull SKIPPED — working tree is dirty",
        _ => outcome.ToString(),
    };

    /// <summary>Mirrors LibGit2Sharp's repository discovery: the folder itself
    /// or any ancestor may hold the .git entry (a directory, or a file for
    /// worktrees/submodules).</summary>
    private static bool IsGitWorkingFolder(string folder)
    {
        var dir = new DirectoryInfo(folder);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
                return true;
            dir = dir.Parent;
        }

        return false;
    }
}
