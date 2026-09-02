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
        Func<GitCredentialRequest, GitCredential?>? credentialPrompt = null,
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
                // LibGit2Sharp 0.27: the annotated overload needs a Signature tagger
                // (Add(name, canonicalName, Signature, message)); the 3-arg overload
                // is Add(name, canonicalName, bool forceOverwrite).
                if (!string.IsNullOrWhiteSpace(tagMessage))
                    repo.Tags.Add(tagName, commit.Sha, BuildTaggerSignature(repo), tagMessage);
                else
                    repo.Tags.Add(tagName, commit.Sha);

                // Best-effort push to origin — with the same credential chain
                // the synchronizer uses. A bare push (no PushOptions) always
                // fails against a protected remote ("anonymous request … 401"),
                // so resolve credentials in-process: URL-embedded → Windows
                // Credential Manager (where Git Credential Manager / Visual
                // Studio store their git:https://… entries); on an auth
                // failure offer the interactive prompt exactly once.
                try
                {
                    var remote = repo.Network.Remotes["origin"];
                    if (remote is not null)
                        PushTag(repo, remote, tagName, credentialPrompt);
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

    /// <summary>Builds the tagger identity for annotated tags from the
    /// repository's git config; falls back to a fixed identity when
    /// user.name/user.email are not configured.</summary>
    private static Signature BuildTaggerSignature(Repository repo)
    {
        try
        {
            var configured = repo.Config.BuildSignature(DateTimeOffset.UtcNow);
            if (!string.IsNullOrWhiteSpace(configured.Name) && !string.IsNullOrWhiteSpace(configured.Email))
                return configured;
        }
        catch
        {
            // No usable git config — use the fallback identity below.
        }

        return new Signature("DeployToolkit", "deploytoolkit@localhost", DateTimeOffset.UtcNow);
    }

    /// <summary>Pushes <c>refs/tags/&lt;tagName&gt;</c> to the remote with the
    /// credential chain (URL-embedded → Windows Credential Manager → optional
    /// interactive prompt on an auth failure, retried once). Mirrors the
    /// synchronizer's fetch loop so both flows behave identically.</summary>
    private static void PushTag(
        Repository repo, Remote remote, string tagName,
        Func<GitCredentialRequest, GitCredential?>? credentialPrompt)
    {
        var credentialRequest = GitCredentialRequest.FromUrl(remote.Url);
        var credentialChain = new GitCredentialChain(
            new UrlEmbeddedCredentialSource(),
            new WindowsCredentialManagerSource());
        var credential = credentialChain.Resolve(credentialRequest);

        var pushRefSpec = $"refs/tags/{tagName}:refs/tags/{tagName}";
        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                repo.Network.Push(remote, pushRefSpec, BuildPushOptions(credential));
                return;
            }
            catch (LibGit2SharpException ex) when (IsAuthenticationFailure(ex.Message) && attempts == 1)
            {
                credential = credentialPrompt?.Invoke(credentialRequest);
                if (credential is null)
                    throw new GitAuthenticationException(credentialRequest.Host, credentialChain.Describe(), ex);
            }
        }
    }

    private static PushOptions BuildPushOptions(GitCredential? credential) => new()
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
    /// failure (as opposed to network/not-found problems)? Same detector the
    /// synchronizer uses — pinned there.</summary>
    private static bool IsAuthenticationFailure(string message) =>
        LibGit2Synchronizer.IsAuthenticationFailure(message);

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
