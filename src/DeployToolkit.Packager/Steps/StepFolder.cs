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

    /// <summary>Guards against a stale (cancelled/abandoned) folder run
    /// clobbering a newer selection's results when its background work
    /// finally completes.</summary>
    private int _selectRunId;

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
        var gitGroup = new GroupBox
        {
            Text = "Git",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8),
            Controls = { _gitSummary },
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
            // ---- git sync (GitSyncPresenter already Guard-wraps the IO;
            //      the sync itself is cancellable via the busy dialog and
            //      probes the remote's reachability before fetching) ----
            GitSyncResult? git = null;
            if (!IsGitWorkingFolder(folder))
            {
                var proceed = AppTheme.Confirm(Wizard,
                    $"'{folder}' is not a git working folder.\n\n" +
                    "Continue without git sync? (no commit SHA will be recorded)");
                if (proceed != DialogResult.Yes)
                    return;
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

        if (Draft.GitSync is { } sync)
        {
            _gitSummary.ForeColor = Color.Black;
            _gitSummary.Text = $"Branch: {sync.BranchName}    HEAD: {ShortSha(sync.HeadSha)}\n" +
                               $"Sync outcome: {DescribeOutcome(sync.Outcome, sync.Pulled)}" +
                               (string.IsNullOrEmpty(sync.RemoteUrl)
                                   ? string.Empty
                                   : $"\nRemote: {sync.RemoteUrl}");
        }
        else if (Draft.FolderPath is not null)
        {
            _gitSummary.ForeColor = Color.DimGray;
            _gitSummary.Text = "No git sync — no commit SHA will be recorded for this package.";
        }
        else
        {
            _gitSummary.ForeColor = Color.DimGray;
            _gitSummary.Text = "No folder selected yet.";
        }

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
