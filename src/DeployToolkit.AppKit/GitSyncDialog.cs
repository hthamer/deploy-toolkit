using DeployToolkit.Core.Git;

namespace DeployToolkit.AppKit;

/// <summary>What the user chose in <see cref="GitSyncDialog"/>.</summary>
public enum GitSyncDecision
{
    /// <summary>Continue with the sync result as-is (no pull was performed).</summary>
    Proceed,

    /// <summary>Re-run the sync overriding the dirty tree
    /// (<c>GitSyncOptions.PullEvenIfDirty = true</c>).</summary>
    PullAnyway,

    /// <summary>Stop the packaging flow.</summary>
    Cancel
}

/// <summary>
/// Modal result presenter for a <see cref="GitSyncResult"/> (plan §5:
/// fetch → pull → SHA → dirty-tree warn). Shows the branch, HEAD SHA, sync
/// outcome, and the uncommitted/untracked file lists. When the tree is dirty
/// the user gets the three-way decision
/// (continue / pull anyway / cancel); a clean tree shows a single Continue.
/// </summary>
public sealed class GitSyncDialog : Form
{
    /// <summary>The decision taken (Cancel unless a decision button was pressed).</summary>
    public GitSyncDecision Decision { get; private set; } = GitSyncDecision.Cancel;

    public GitSyncDialog(GitSyncResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Text = "Git synchronize";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(640, 540);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12),
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;

        void AddRow(string label, string value)
        {
            layout.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(2, 4, 8, 4),
            }, 0, row);
            layout.Controls.Add(new Label
            {
                Text = value,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(2, 4, 2, 4),
            }, 1, row);
            row++;
        }

        AddRow("Repository:", result.RepositoryPath);
        AddRow("Branch:", result.BranchName);
        AddRow("HEAD SHA:", ShortSha(result.HeadSha));
        if (result.HeadShaBeforeSync is { } before)
            AddRow("Before sync:", ShortSha(before));
        AddRow("Outcome:", DescribeOutcome(result.Outcome, result.Pulled));

        // File lists — the decision ("can I safely pull?") depends on seeing them.
        var lists = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Height = 280,
        };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.Controls.Add(MakeFileListBox($"Uncommitted changes ({result.UncommittedFiles.Count})", result.UncommittedFiles), 0, 0);
        lists.Controls.Add(MakeFileListBox($"Untracked files ({result.UntrackedFiles.Count})", result.UntrackedFiles), 1, 0);
        layout.Controls.Add(lists, 0, row);
        layout.SetColumnSpan(lists, 2);
        row++;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(12),
            Height = 48,
        };

        Button MakeDecisionButton(string text, GitSyncDecision decision, DialogResult dialogResult)
        {
            var button = new Button { Text = text };
            AppTheme.StyleButton(button);
            button.Click += (_, _) => { Decision = decision; DialogResult = dialogResult; };
            return button;
        }

        if (result.IsDirty)
        {
            var proceed = MakeDecisionButton("Continue without pull", GitSyncDecision.Proceed, DialogResult.OK);
            var pullAnyway = MakeDecisionButton("Pull anyway (overriding dirty tree)", GitSyncDecision.PullAnyway, DialogResult.OK);
            var cancel = MakeDecisionButton("Cancel", GitSyncDecision.Cancel, DialogResult.Cancel);
            buttons.Controls.Add(pullAnyway);
            buttons.Controls.Add(proceed);
            buttons.Controls.Add(cancel);
            AcceptButton = proceed;
            CancelButton = cancel;
        }
        else
        {
            var proceed = MakeDecisionButton("Continue", GitSyncDecision.Proceed, DialogResult.OK);
            buttons.Controls.Add(proceed);
            AcceptButton = proceed;
        }

        Controls.Add(layout);
        Controls.Add(buttons);
    }

    private static string ShortSha(string sha) => sha.Length <= 12 ? sha : sha[..12];

    private static string DescribeOutcome(GitSyncOutcome outcome, bool pulled) => outcome switch
    {
        GitSyncOutcome.UpToDate => pulled
            ? "Pulled (branch moved)."
            : "Branch is up to date with origin.",
        GitSyncOutcome.FastForwarded => "Pulled — fast-forwarded to origin's tip.",
        GitSyncOutcome.FetchedOnly => "Fetched from origin only (HEAD untouched).",
        GitSyncOutcome.SkippedDirtyTree => "Pull SKIPPED — the working tree is dirty. Review the file lists below.",
        _ => outcome.ToString(),
    };

    private static GroupBox MakeFileListBox(string title, IReadOnlyList<string> files)
    {
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
            Font = new Font("Consolas", 9f),
        };
        foreach (var file in files)
            list.Items.Add(file);
        if (files.Count == 0)
            list.Items.Add("(none)");

        return new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Height = 240,
            Controls = { list },
        };
    }
}

/// <summary>
/// UI flow wrapper around <see cref="IGitSynchronizer.SynchronizeAsync"/>:
/// runs the sync under <see cref="Guard.RunAsync"/>, shows the result through
/// <see cref="GitSyncDialog"/> when the tree is dirty, and honors the user's
/// decision (including re-running the sync with
/// <c>GitSyncOptions.PullEvenIfDirty = true</c>). Returns the (possibly
/// re-run) result, or null when the sync failed or the user cancelled.
/// </summary>
public static class GitSyncPresenter
{
    public static async Task<GitSyncResult?> SynchronizeWithUiAsync(Form? owner, IGitSynchronizer sync, string repoPath)
    {
        var result = await RunSyncAsync(owner, sync, repoPath, options: null, "Syncing with origin (fetch + pull)…");
        if (result is null)
            return null;

        if (result.Outcome != GitSyncOutcome.SkippedDirtyTree && !result.IsDirty)
            return result; // clean tree — the flow continues with this result

        using var dialog = new GitSyncDialog(result);
        var choice = owner is null || owner.IsDisposed ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (choice != DialogResult.OK)
            return null; // cancelled
        if (dialog.Decision == GitSyncDecision.Proceed)
            return result; // continue without pulling

        // PullAnyway — re-run with the override; the pull still refuses to
        // clobber a locally-modified tracked file (fails loudly, Core policy).
        return await RunSyncAsync(owner, sync, repoPath, new GitSyncOptions { PullEvenIfDirty = true },
            "Pulling over the dirty tree (as requested)…");
    }

    private static async Task<GitSyncResult?> RunSyncAsync(
        Form? owner, IGitSynchronizer sync, string repoPath, GitSyncOptions? options, string busyText)
    {
        GitSyncResult? result = null;
        DivergedBranchException? diverged = null;

        // Last-resort credential prompt: automatic sources (URL-embedded,
        // Windows Credential Manager / GCM / Visual Studio entries) run first
        // inside the synchronizer; this delegate only fires on a 401/403.
        if (owner is { IsDisposed: false })
            options = (options ?? new GitSyncOptions()) with { CredentialPrompt = GitCredentialUi.CreatePrompt(owner) };

        await Guard.RunAsync(owner, busyText, async cancellationToken =>
        {
            try
            {
                // The token is honored by the synchronizer's WaitAsync wrapper:
                // cancelling a hung fetch frees the UI after Guard's grace
                // period at the latest.
                result = await sync.SynchronizeAsync(repoPath, options, cancellationToken);
            }
            catch (DivergedBranchException dex)
            {
                diverged = dex; // custom presentation — not a generic error
            }
        });

        if (diverged is not null)
        {
            AppTheme.Error(owner, diverged.Message, "Git — diverged branch");
            return null;
        }
        return result; // null on other failures — Guard already reported them
    }
}
