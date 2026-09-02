using LibGit2Sharp;

namespace DeployToolkit.Core.Git;

/// <summary>
/// The outcome of <see cref="GitTagger.TagAndPushAsync"/>.
/// </summary>
public sealed record GitTagResult(
    bool Success,
    string TagName,
    string? ErrorMessage);

/// <summary>
/// Creates a git tag at a specific commit in a local repository and pushes it
/// to the remote, so marking a package as "Deployed" leaves a trace in the git
/// history that survives across machines (user request: "when I flag the
/// package as deployed, add a tag to the commit of the Git repo with a proper
/// name and the date of deployment so we have a track also in git repo if we
/// want to regenerate these packages again").
/// <para>
/// The tag name is derived from a configurable template
/// (<see cref="RegistryConnectionSettings.GitTagTemplate"/>) with placeholders:
/// <c>{version}</c>, <c>{date}</c> (yyyyMMdd), <c>{datetime}</c>
/// (yyyyMMdd-HHmmss), <c>{component}</c> (sanitized). Default:
/// <c>deploy-{version}-{date}</c> (Option A).
/// </para>
/// <para>
/// Best-effort: a failure to push (no network, auth refused, no remote) is
/// reported but does not undo the local tag creation or the registry's
/// "Mark Deployed" — the tag exists locally and can be pushed later.
/// </para>
/// </summary>
public static class GitTagger
{
    /// <summary>
    /// Formats the tag name from <paramref name="template"/> by replacing the
    /// placeholders. The component name is sanitized (invalid git-ref chars
    /// → '_'). Returns the formatted tag name (never null/empty when the
    /// template is non-empty).
    /// </summary>
    public static string FormatTagName(string template, string version, string componentName, DateTimeOffset? deployedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var now = deployedUtc ?? DateTimeOffset.UtcNow;
        var safeComponent = MakeSafeGitRef(componentName);

        return template
            .Replace("{version}", version, StringComparison.OrdinalIgnoreCase)
            .Replace("{component}", safeComponent, StringComparison.OrdinalIgnoreCase)
            .Replace("{datetime}", now.UtcDateTime.ToString("yyyyMMdd-HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.UtcDateTime.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a git tag named <paramref name="tagName"/> at the commit
    /// <paramref name="commitSha"/> in the repository at
    /// <paramref name="repositoryPath"/>, then pushes the tag to the origin
    /// remote (best-effort — the local tag is created even when the push
    /// fails). Returns a <see cref="GitTagResult"/>.
    /// <para>
    /// When the tag already exists, the method succeeds without re-creating
    /// it (idempotent — marking the same package deployed twice is fine).
    /// </para>
    /// </summary>
    public static async Task<GitTagResult> TagAndPushAsync(
        string repositoryPath,
        string commitSha,
        string tagName,
        string? tagMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            return new GitTagResult(false, tagName ?? string.Empty, "Repository path is required.");
        if (string.IsNullOrWhiteSpace(commitSha))
            return new GitTagResult(false, tagName ?? string.Empty, "Commit SHA is required.");
        if (string.IsNullOrWhiteSpace(tagName))
            return new GitTagResult(false, string.Empty, "Tag name is required.");

        try
        {
            return await Task.Run(() =>
            {
                using var repo = new Repository(repositoryPath);

                // Resolve the commit.
                var commit = repo.Lookup<Commit>(commitSha);
                if (commit is null)
                    return new GitTagResult(false, tagName,
                        $"Commit '{commitSha}' not found in the repository.");

                // Idempotent: if the tag already exists, succeed without re-creating.
                var existing = repo.Tags[tagName];
                if (existing is not null)
                    return new GitTagResult(true, tagName, null); // already tagged

                // Create the tag (annotated when a message is provided, lightweight otherwise).
                if (!string.IsNullOrWhiteSpace(tagMessage))
                    repo.Tags.Add(tagName, commit.Sha, tagMessage);
                else
                    repo.Tags.Add(tagName, commit.Sha);

                // Best-effort push to origin.
                try
                {
                    var remote = repo.Network.Remotes["origin"];
                    if (remote is not null)
                    {
                        var pushRefs = repo.Network.BuildPushOptions(remote, tagName);
                        repo.Network.Push(remote, pushRefs);
                    }
                }
                catch (Exception pushEx)
                {
                    // The local tag was created — the push failure is non-fatal.
                    return new GitTagResult(true, tagName,
                        $"Tag '{tagName}' created locally but push to origin failed: {pushEx.Message}");
                }

                return new GitTagResult(true, tagName, null);
            }, cancellationToken);
        }
        catch (RepositoryNotFoundException)
        {
            return new GitTagResult(false, tagName, $"'{repositoryPath}' is not a git repository.");
        }
        catch (Exception ex)
        {
            return new GitTagResult(false, tagName, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Sanitizes a name for use as a git tag/branch ref: replaces
    /// characters git rejects (spaces, ~, ^, :, ?, *, [, \) with '_'.</summary>
    private static string MakeSafeGitRef(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "component";
        var invalid = new[] { ' ', '~', '^', ':', '?', '*', '[', '\\', ' ' };
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "component" : safe;
    }
}

/// <summary>
/// Extension: builds LibGit2Sharp PushOptions for a tag push with default
/// credential handling (same chain as the synchronizer — URL-embedded,
/// Windows Credential Manager). Returns the refspec to push.
/// </summary>
internal static class RepositoryPushExtensions
{
    internal static string BuildPushOptions(this Network network, Remote remote, string tagName)
    {
        // Return the refspec — the caller calls network.Push with it.
        // LibGit2Sharp 0.27 Push signature: Push(Remote remote, string pushRefSpec)
        // For a tag: refs/tags/<tagName>:refs/tags/<tagName>
        return $"refs/tags/{tagName}:refs/tags/{tagName}";
    }
}
