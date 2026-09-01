using LibGit2Sharp;

namespace DeployToolkit.Core.Git;

/// <summary>
/// LibGit2Sharp implementation of <see cref="IGitSynchronizer"/> — all git
/// operations happen as native library calls inside this process (plan §1's
/// "no scripts" spirit applies here too: no git.exe shell-outs, ever).
///
/// Deployment-tooling posture baked into the behavior:
///  - Pulls are <b>fast-forward only</b>. A merge commit created silently on
///    the build machine would mean the packaged SHA is a commit nobody ever
///    reviewed on origin — refused instead, via <see cref="DivergedBranchException"/>.
///  - A dirty tree skips the pull by default and is reported (plan §5: "warn
///    before proceeding, so uncommitted or wrong-branch changes can't
///    accidentally get packaged").
///  - The current branch name and resulting HEAD SHA are always returned so
///    the UI can warn on wrong-branch selections and stamp the manifest.
/// </summary>
public sealed class LibGit2Synchronizer : IGitSynchronizer
{
    public async Task<GitSyncResult> SynchronizeAsync(
        string repositoryPath,
        GitSyncOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GitSyncOptions();
        var fullRoot = Path.GetFullPath(repositoryPath);

        // LibGit2Sharp is synchronous and can hold the calling thread for the
        // duration of a network fetch — run off-thread so WinForms UIs stay
        // responsive without every caller having to remember Task.Run.
        //
        // The WaitAsync wrapper additionally frees the CALLER when the
        // cancellation token fires even though libgit2 cannot abort a fetch
        // mid-flight: the abandoned fetch thread simply ends when the OS
        // times out its socket, while the caller gets its
        // OperationCanceledException immediately (the UI's Guard then stops
        // waiting — it can never hang on this call).
        return await Task.Run(() => SynchronizeCoreAsync(fullRoot, options), cancellationToken)
            .WaitAsync(cancellationToken);
    }

    private static async Task<GitSyncResult> SynchronizeCoreAsync(string repositoryPath, GitSyncOptions options)
    {
        Repository repo;
        try
        {
            repo = new Repository(repositoryPath);
        }
        catch (RepositoryNotFoundException)
        {
            throw new InvalidOperationException(
                $"'{repositoryPath}' is not a git working folder. The Packager expects the project's git working folder, not a publish-output folder (plan §5).");
        }

        using (repo)
        {
            if (repo.Info.IsBare)
                throw new InvalidOperationException(
                    $"'{repositoryPath}' is a bare repository. Point the Packager at a normal working folder with a checkout.");

            if (repo.Head.Tip is null)
                throw new InvalidOperationException(
                    $"'{repositoryPath}' has no commits yet — commit something before packaging.");

            if (repo.Head.FriendlyName == "(no branch)")
                throw new InvalidOperationException(
                    $"'{repositoryPath}' is in detached-HEAD state. Check out a branch before packaging.");

            var branchName = repo.Head.FriendlyName;
            var headShaBefore = repo.Head.Tip.Sha;

            var (uncommitted, untracked) = CaptureStatus(repo);

            // --- Fetch: refresh origin's refs regardless of what comes next.
            //
            // Credentials: LibGit2Sharp performs NO OS credential lookup of
            // its own — an HTTPS fetch without a CredentialsProvider is an
            // anonymous request and fails with "request failed with status
            // code: 401" against any protected remote. Resolve credentials
            // in-process: URL-embedded → options-provided → Windows
            // Credential Manager (where Git Credential Manager / Visual
            // Studio store their git:https://… entries); on a 401/403 offer
            // the interactive prompt exactly once, then fail with a message
            // that names what was tried.
            var remote = repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"Repository '{repositoryPath}' has no remote configured (expected 'origin'). Add one before packaging.");

            // Fast, honest failure for unreachable servers: probing the
            // endpoint with a short timeout converts the classic
            // "fetch hangs for minutes against a dead VPN" experience into
            // an immediate, actionable error (local/file remotes are skipped).
            await GitEndpointProbe.ProbeAsync(remote.Url, GitEndpointProbe.DefaultTimeout)
                .ConfigureAwait(false);

            var credentialRequest = GitCredentialRequest.FromUrl(remote.Url);
            var credentialChain = new GitCredentialChain(
                new UrlEmbeddedCredentialSource(),
                new DelegateCredentialSource("options", request => options.Credential),
                new WindowsCredentialManagerSource());
            var credential = credentialChain.Resolve(credentialRequest);
            var fetchOptions = BuildFetchOptions(credential);

            var attempts = 0;
            while (true)
            {
                attempts++;
                try
                {
                    // (url parameter = remote NAME in LibGit2Sharp's Fetch overloads)
                    repo.Network.Fetch(remote.Name, Array.Empty<string>(), fetchOptions);
                    break;
                }
                catch (LibGit2SharpException ex) when (IsAuthenticationFailure(ex.Message) && attempts == 1)
                {
                    var prompted = options.CredentialPrompt?.Invoke(credentialRequest);
                    if (prompted is null)
                        throw new GitAuthenticationException(credentialRequest.Host, credentialChain.Describe(), ex);

                    credential = prompted; // user supplied credentials — retry once
                    fetchOptions = BuildFetchOptions(credential);
                }
            }

            var trackedTip = repo.Head.TrackedBranch?.Tip
                ?? throw new InvalidOperationException(
                    $"Branch '{branchName}' has no upstream tracking branch. Push it once (git push -u origin {branchName}) or set branch.{branchName}.remote/merge, then retry.");

            if (options.FetchOnly)
            {
                return new GitSyncResult(
                    repositoryPath, branchName, headShaBefore, headShaBefore,
                    GitSyncOutcome.FetchedOnly, uncommitted, untracked);
            }

            // --- Dirty-tree policy (plan §5): warn => skip by default. ---
            if (uncommitted.Count + untracked.Count > 0 && !options.PullEvenIfDirty)
            {
                return new GitSyncResult(
                    repositoryPath, branchName, headShaBefore, headShaBefore,
                    GitSyncOutcome.SkippedDirtyTree, uncommitted, untracked);
            }

            if (trackedTip.Sha == headShaBefore)
            {
                return new GitSyncResult(
                    repositoryPath, branchName, headShaBefore, headShaBefore,
                    GitSyncOutcome.UpToDate, uncommitted, untracked);
            }

            // --- Pull, fast-forward only. Never a merge commit. ---
            // (LibGit2Sharp 0.27 has no Network.Pull; fetch + MergeFetchedRefs
            //  back-to-back is the documented equivalent and gives us full
            //  control over the fast-forward-only policy.)
            MergeResult mergeResult;
            try
            {
                mergeResult = repo.MergeFetchedRefs(
                    BuildSignature(repo),
                    new MergeOptions
                    {
                        FastForwardStrategy = FastForwardStrategy.FastForwardOnly,
                    });
            }
            catch (CheckoutConflictException ex)
            {
                throw new InvalidOperationException(
                    "Fast-forward checkout would overwrite locally-modified files. Commit, stash, or discard your changes, then retry. Details: " + ex.Message);
            }
            catch (NonFastForwardException ex)
            {
                // libgit2 signals FF-only violations by throwing rather than
                // via MergeResult.Status (verified against 0.27.2).
                throw new DivergedBranchException(branchName, headShaBefore, trackedTip.Sha)
                {
                    Source = ex.Source,
                };
            }

            switch (mergeResult.Status)
            {
                case MergeStatus.NonFastForward:
                    // Reached either as a status or via NonFastForwardException
                    // depending on libgit2's mood — handle both.
                    throw new DivergedBranchException(branchName, headShaBefore, trackedTip.Sha);
                case MergeStatus.Conflicts:
                    throw new InvalidOperationException(
                        "Pull reported conflicts, which should be impossible under fast-forward-only. This is a bug — please report it.");
            }

            var headShaAfter = repo.Head.Tip.Sha;
            var (uncommittedAfter, untrackedAfter) = CaptureStatus(repo);

            // A merge that was analyzed as UpToDate is a no-op — report it as
            // such instead of claiming a fast-forward happened.
            var outcome = headShaAfter != headShaBefore
                ? GitSyncOutcome.FastForwarded
                : GitSyncOutcome.UpToDate;

            return new GitSyncResult(
                repositoryPath, branchName, headShaAfter, headShaBefore,
                outcome, uncommittedAfter, untrackedAfter);
        }
    }

    private static FetchOptions BuildFetchOptions(GitCredential? credential) => new()
    {
        CredentialsProvider = credential is null
            ? null
            : (_, _, _) => new UsernamePasswordCredentials
            {
                Username = credential.Username,
                Password = credential.Password,
            },
    };

    /// <summary>PURE: does this libgit2 error text describe an authentication
    /// failure (as opposed to network/not-found problems)? Internal so the
    /// self-test can pin the detection.</summary>
    internal static bool IsAuthenticationFailure(string message)
    {
        // libgit2 phrasings observed in the wild:
        //  - "request failed with status code: 401" (HTTP basic rejected)
        //  - "request failed with status code: 403" (token lacks scope)
        //  - "too many redirects or authentication replays"
        //  - "authentication failed" / "unsupported credential type"
        return message.Contains("status code: 401") ||
               message.Contains("status code: 403") ||
               message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private static (List<string> Uncommitted, List<string> Untracked) CaptureStatus(Repository repo)
    {
        // IncludeUntracked + recursion so a stray new file anywhere under the
        // project is surfaced; .gitignore is respected by libgit2 natively,
        // so bin/obj-style paths that are properly ignored never show up here.
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            Show = StatusShowOption.IndexAndWorkDir,
        });

        var uncommitted = new List<string>();
        foreach (var entry in status)
        {
            var s = entry.State;
            var touchesIndexOrWorkdir =
                s.HasFlag(FileStatus.NewInIndex) ||
                s.HasFlag(FileStatus.ModifiedInIndex) ||
                s.HasFlag(FileStatus.DeletedFromIndex) ||
                s.HasFlag(FileStatus.RenamedInIndex) ||
                s.HasFlag(FileStatus.TypeChangeInIndex) ||
                s.HasFlag(FileStatus.ModifiedInWorkdir) ||
                s.HasFlag(FileStatus.DeletedFromWorkdir) ||
                s.HasFlag(FileStatus.TypeChangeInWorkdir) ||
                s.HasFlag(FileStatus.RenamedInWorkdir);

            if (touchesIndexOrWorkdir)
                uncommitted.Add(entry.FilePath);
        }

        var untracked = new List<string>();
        foreach (var entry in status)
        {
            // A brand-new file is NewInWorkdir only until staged, so the
            // untracked set is exactly the NewInWorkdir entries that are not
            // also index entries.
            if (entry.State == FileStatus.NewInWorkdir)
                untracked.Add(entry.FilePath);
        }

        return (uncommitted, untracked);
    }

    private static Signature BuildSignature(Repository repo)
    {
        // Only needed if a merge commit were to be created — which the
        // FF-only policy prevents — but the Pull API demands one anyway.
        try
        {
            return repo.Config.BuildSignature(DateTimeOffset.UtcNow);
        }
        catch
        {
            return new Signature("DeployToolkit", "deploytoolkit@localhost", DateTimeOffset.UtcNow);
        }
    }
}
