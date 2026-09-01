# DeployToolkit.Core.Git — Git integration (plan Phase 3, §5)

LibGit2Sharp wrapper for the Packager's project-selection flow: fetch →
fast-forward-only pull on the current branch → dirty-tree check → capture
the resulting commit SHA. Pure in-process — **no `git.exe`, no scripts**,
and the native libgit2 binaries ship inside the NuGet package, so the
self-contained Packager exe needs no system git installed.

## Usage

```csharp
IGitSynchronizer git = new LibGit2Synchronizer();

var result = await git.SynchronizeAsync(@"C:\src\ClientA-CMS");

switch (result.Outcome)
{
    case GitSyncOutcome.UpToDate:        break;                        // nothing new
    case GitSyncOutcome.FastForwarded:   break;                        // pulled; result.HeadSha is the new tip
    case GitSyncOutcome.SkippedDirtyTree:
        // result.UncommittedFiles / result.UntrackedFiles -> show warning (plan §5)
        // retry with new GitSyncOptions { PullEvenIfDirty = true } if the user proceeds
        break;
    case GitSyncOutcome.FetchedOnly:     break;                        // FetchOnly option
}

manifest.GitCommitSha = result.HeadSha;   // stamp the package with the exact build source
```

## Built-in deployment-tooling posture

- **Fast-forward only.** A diverged local branch throws
  `DivergedBranchException` (with both SHAs) instead of silently creating a
  merge commit — whatever gets packaged must be a commit that exists on
  origin, verifiable by anyone later.
- **Dirty tree skips the pull by default** (plan §5 "warn before
  proceeding"). `UncommittedFiles` (staged/workdir edits to tracked files)
  and `UntrackedFiles` (new files; `.gitignore` is respected) are listed
  separately. `PullEvenIfDirty = true` overrides; if the checkout would
  overwrite a locally-modified file, it still fails loudly.
- **Branch + SHA always reported** so the UI can warn on wrong-branch
  selections and stamp `GitCommitSha` into the manifest/registry record.
- Guarded inputs with actionable errors: not a git folder, bare repo,
  no commits, detached HEAD, no remote, no upstream tracking.

## Self-test

`tools/DeployToolkit.Core.Git.SelfTest` builds real repositories (bare
origin + two clones) and drives real push/pull/fetch/divergence flows —
36 checks, no network and no git.exe required. One quirk to know when
touching this code: **LibGit2Sharp 0.27's `Repository.Clone` return value is
unreliable** (often the `.git` folder path, not the workdir) — the tests
therefore never trust it; do the same in the Packager UI.
