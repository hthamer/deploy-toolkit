using LibGit2Sharp;

namespace DeployToolkit.Core.Git;

/// <summary>
/// Lightweight, network-free read of a git working folder's basic facts:
/// the remote URL (the <c>origin</c> remote, or the first remote when origin
/// is absent), the current branch name, and the HEAD commit SHA. Used by
/// the Packager's folder step to show the repo URL in the Git box
/// IMMEDIATELY after the user picks a folder — before / without running the
/// fetch+pull sync (user request: "after I select the folder it will not
/// write there the repository url of the selected folder").
///
/// Never throws: returns <c>null</c> when the path isn't a git working
/// folder, has no commits, or is in detached-HEAD state. No fetch, no
/// network — just reads the local refs and config.
/// </summary>
public sealed record GitRepoInfo(string? RemoteUrl, string BranchName, string HeadSha);

public static class GitRemoteUrlReader
{
    /// <summary>
    /// Reads the remote URL + branch + HEAD of the git working folder at
    /// <paramref name="repositoryPath"/>. Returns null when the path isn't
    /// a git repo, has no commits, or is detached-HEAD. Never throws.
    /// </summary>
    public static GitRepoInfo? Read(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return null;

        Repository repo;
        try
        {
            repo = new Repository(repositoryPath);
        }
        catch (RepositoryNotFoundException)
        {
            return null;
        }
        catch (Exception)
        {
            // LibGit2Sharp can throw on corrupted repos / locked indexes —
            // never crash the wizard over a read-only probe.
            return null;
        }

        using (repo)
        {
            if (repo.Info.IsBare)
                return null;

            if (repo.Head.Tip is null)
                return null; // no commits yet

            var branchName = repo.Head.FriendlyName;
            if (branchName == "(no branch)")
                return null; // detached-HEAD — don't show a misleading branch

            var remote = repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault();
            var remoteUrl = remote?.Url;

            return new GitRepoInfo(remoteUrl, branchName, repo.Head.Tip.Sha);
        }
    }
}
