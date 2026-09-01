namespace DeployToolkit.Core.Git;

/// <summary>
/// How a synchronize run ended. The Packager's UI renders these as its
/// "git pull" step result (plan §5: fetch → pull → capture SHA → dirty
/// check and warn).
/// </summary>
public enum GitSyncOutcome
{
    /// <summary>Pull ran (or was checked) and the branch already matches origin.</summary>
    UpToDate,

    /// <summary>The pull fast-forwarded HEAD to origin's tip.</summary>
    FastForwarded,

    /// <summary>Fetch ran only (<see cref="GitSyncOptions.FetchOnly"/>); HEAD untouched.</summary>
    FetchedOnly,

    /// <summary>The working tree was dirty, so the pull was skipped (default policy, plan §5 "warn before proceeding"). HEAD untouched; inspect the file lists and retry with <see cref="GitSyncOptions.PullEvenIfDirty"/> if appropriate.</summary>
    SkippedDirtyTree,
}

/// <summary>
/// Options for <see cref="IGitSynchronizer.SynchronizeAsync"/>. Defaults are
/// the safe deployment-tooling posture: never merge, never pull over a dirty
/// tree without being told to.
/// </summary>
public sealed record GitSyncOptions
{
    /// <summary>
    /// When the working tree has uncommitted or untracked files, the pull is
    /// skipped and <see cref="GitSyncOutcome.SkippedDirtyTree"/> is returned
    /// instead. Setting this true proceeds with the pull anyway — safe when
    /// the dirty files are unrelated to what the incoming commits touch;
    /// if the fast-forward checkout would overwrite a locally-modified file
    /// the pull still fails loudly rather than clobbering anything.
    /// </summary>
    public bool PullEvenIfDirty { get; init; }

    /// <summary>
    /// Fetch from origin (updating remote-tracking refs) but never move HEAD.
    /// Useful for a "check for updates" UI button. Takes precedence over
    /// <see cref="PullEvenIfDirty"/>.
    /// </summary>
    public bool FetchOnly { get; init; }

    /// <summary>
    /// Explicit credential used ahead of the automatic sources (Windows
    /// Credential Manager etc.). Mostly for tests and scripted runs; UIs
    /// prefer <see cref="CredentialPrompt"/>.
    /// </summary>
    public GitCredential? Credential { get; init; }

    /// <summary>
    /// Optional interactive prompt, invoked at most once when the fetch fails
    /// authentication and the automatic sources found nothing usable. Called
    /// on the synchronize background thread — implementations must marshal to
    /// the UI thread themselves. Return null to fail with
    /// <see cref="GitAuthenticationException"/>.
    /// </summary>
    public Func<GitCredentialRequest, GitCredential?>? CredentialPrompt { get; init; }
}

/// <summary>
/// The outcome of one <see cref="IGitSynchronizer.SynchronizeAsync"/> run —
/// everything the Packager UI needs to render the git step and to stamp
/// <c>GitCommitSha</c> on the resulting manifest/registry record (plan §5:
/// "capture the resulting commit SHA").
/// </summary>
public sealed record GitSyncResult(
    string RepositoryPath,
    string BranchName,
    string HeadSha,
    string? HeadShaBeforeSync,
    GitSyncOutcome Outcome,
    IReadOnlyList<string> UncommittedFiles,
    IReadOnlyList<string> UntrackedFiles)
{
    /// <summary>True when HEAD changed during this run (i.e. the pull moved it).</summary>
    public bool Pulled => HeadShaBeforeSync is not null && HeadSha != HeadShaBeforeSync;

    /// <summary>Staged and/or workdir modifications to tracked files.</summary>
    public bool HasUncommittedChanges => UncommittedFiles.Count > 0;

    /// <summary>New files git is not tracking yet (ignored files do NOT count — .gitignore is respected).</summary>
    public bool HasUntrackedFiles => UntrackedFiles.Count > 0;

    /// <summary>Anything that would normally block a pull (uncommitted or untracked).</summary>
    public bool IsDirty => HasUncommittedChanges || HasUntrackedFiles;
}

/// <summary>
/// Raised when the local branch and origin have diverged (both have commits
/// the other lacks). The toolkit refuses to auto-merge in that situation —
/// whatever gets packaged must be exactly a commit that exists on origin, so
/// the user resolves the divergence manually and re-runs.
/// </summary>
public sealed class DivergedBranchException : Exception
{
    public string BranchName { get; }
    public string LocalSha { get; }
    public string RemoteSha { get; }

    public DivergedBranchException(string branchName, string localSha, string remoteSha)
        : base($"Branch '{branchName}' has diverged from origin (local {localSha[..Math.Min(12, localSha.Length)]} vs origin {remoteSha[..Math.Min(12, remoteSha.Length)]}). " +
               "The toolkit never creates merge commits — reconcile manually (rebase/merge/push), then retry.")
    {
        BranchName = branchName;
        LocalSha = localSha;
        RemoteSha = remoteSha;
    }
}

/// <summary>
/// Git integration for the Packager (plan §5 and §14 Phase 3), implemented
/// purely in-process with LibGit2Sharp — no git.exe, no scripts.
/// </summary>
public interface IGitSynchronizer
{
    /// <summary>
    /// Runs the plan §5 sequence against a local working folder:
    /// validate → status/dirty check → fetch → pull (fast-forward only) on
    /// the current branch → capture the resulting commit SHA.
    /// </summary>
    Task<GitSyncResult> SynchronizeAsync(
        string repositoryPath,
        GitSyncOptions? options = null,
        CancellationToken cancellationToken = default);
}
