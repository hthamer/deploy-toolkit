using DeployToolkit.Core.Git;
using LibGit2Sharp;

// =============================================================================
// Self-test for the Phase 3 git integration (plan §5). Exercises the real
// LibGit2Sharp pipeline — a bare "origin" repository plus two clones doing
// real pushes and fast-forward pulls — covering: up-to-date detection,
// fast-forward pull with SHA capture, fetch-only, dirty-tree policy (both
// the default skip and the PullEvenIfDirty override), untracked reporting,
// divergence refusal, and the not-a-repo guard. No network, no git.exe.
// =============================================================================

var failures = new List<string>();
var passed = 0;

void Check(string name, bool condition)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  [pass] {name}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"  [FAIL] {name}");
    }
}

var workRoot = Path.Combine(Path.GetTempPath(), "DeployToolkitGitTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workRoot);

// ---------------------------------------------------------------- helpers

string InitBare(string name)
{
    var path = Path.Combine(workRoot, name);
    Repository.Init(path, isBare: true);
    return path;
}

/// <summary>Creates a working repo with an initial commit and pushes it to the bare origin, becoming the branch everyone shares.</summary>
string SeedRepo(string name, string barePath, out string branchName, out string seedSha)
{
    var path = Path.Combine(workRoot, name);
    Repository.Init(path);
    using var repo = new Repository(path);

    branchName = repo.Head.FriendlyName; // libgit2's default branch for this build

    repo.Config.Set("user.name", "DeployToolkit Test");
    repo.Config.Set("user.email", "test@deploytoolkit.local");

    var remote = repo.Network.Remotes.Add("origin", barePath);

    File.WriteAllText(Path.Combine(path, "README.md"), "# seed\n");
    Commands.Stage(repo, "README.md");
    var sig = new Signature("DeployToolkit Test", "test@deploytoolkit.local", DateTimeOffset.UtcNow);
    seedSha = repo.Commit("seed", sig, sig).Sha;

    repo.Network.Push(remote, $"{branchName}:refs/heads/{branchName}");
    return path;
}

string CloneFrom(string barePath, string name, string branchName)
{
    var dest = Path.Combine(workRoot, name);
    // NOTE: LibGit2Sharp 0.27's Clone() return value is unreliable (it
    // frequently reports the .git folder path instead of the workdir), so we
    // use our own destination path and never trust the return value.
    Repository.Clone(barePath, dest, new CloneOptions { BranchName = branchName });
    // Defensive: make sure branch.<name>.remote/merge exist so pulls know
    // what to track (a normal `git clone` configures this, and so does
    // libgit2's Clone — this costs nothing and removes any ambiguity).
    using var repo = new Repository(dest);
    if (repo.Head.TrackedBranch is null)
    {
        repo.Config.Set($"branch.{branchName}.remote", "origin");
        repo.Config.Set($"branch.{branchName}.merge", $"refs/heads/{branchName}");
    }
    return dest;
}

string CommitFile(string repoPath, string fileName, string content)
{
    using var repo = new Repository(repoPath);
    File.WriteAllText(Path.Combine(repoPath, fileName), content);
    Commands.Stage(repo, fileName);
    var sig = new Signature("DeployToolkit Test", "test@deploytoolkit.local", DateTimeOffset.UtcNow);
    return repo.Commit($"commit {fileName}", sig, sig).Sha;
}

void PushToOrigin(string repoPath)
{
    using var repo = new Repository(repoPath);
    var branch = repo.Head.FriendlyName;
    repo.Network.Push(repo.Network.Remotes["origin"], $"{branch}:refs/heads/{branch}");
}

var synchronizer = new LibGit2Synchronizer();

try
{
    // ---------------------------------------------------------------- setup
    var bare = InitBare("origin.git");
    var seed = SeedRepo("seed", bare, out var branch, out var seedSha);
    var repoA = CloneFrom(bare, "clientA-work", branch);   // "your machine" (Packager)
    var repoB = CloneFrom(bare, "colleague", branch);      // pushes commits from elsewhere

    Console.WriteLine($"== Setup: bare origin + seed + 2 clones (branch '{branch}') ==");

    // ---------------------------------------------------------------- t1
    Console.WriteLine("== t1: sync right after clone is UpToDate with correct SHA/branch ==");
    var r1 = await synchronizer.SynchronizeAsync(repoA);
    Check("outcome is UpToDate", r1.Outcome == GitSyncOutcome.UpToDate);
    Check("HeadSha equals the seed commit", r1.HeadSha == seedSha);
    Check("HeadShaBeforeSync was captured", r1.HeadShaBeforeSync == seedSha);
    Check($"branch name reported ('{branch}')", r1.BranchName == branch);
    Check("Pulled is false", !r1.Pulled);
    Check("tree is clean after clone", !r1.IsDirty);
    // RemoteUrl is surfaced so the UI can display it in the Git section and a
    // successful build can write it back to the client's GitRepositoryUrl.
    Check("RemoteUrl is the origin URL", !string.IsNullOrEmpty(r1.RemoteUrl) && r1.RemoteUrl!.Contains(bare));

    // ---------------------------------------------------------------- t1b
    Console.WriteLine("== t1b: GitRemoteUrlReader reads URL + branch + HEAD (no network) ==");
    var info = GitRemoteUrlReader.Read(repoA);
    Check("GitRemoteUrlReader returns non-null for a git working folder", info is not null);
    Check("RemoteUrl matches the bare origin", info!.RemoteUrl is not null && info.RemoteUrl.Contains(bare));
    Check("BranchName matches the clone branch", info.BranchName == branch);
    Check("HeadSha matches the seed commit", info.HeadSha == seedSha);
    Check("GitRemoteUrlReader returns null for a non-git folder",
        GitRemoteUrlReader.Read(Path.Combine(workRoot, "not-a-repo")) is null);
    Check("GitRemoteUrlReader returns null for a missing path",
        GitRemoteUrlReader.Read(Path.Combine(workRoot, "missing")) is null);
    Check("GitRemoteUrlReader returns null for null/empty path",
        GitRemoteUrlReader.Read(null!) is null && GitRemoteUrlReader.Read("") is null);

    // ---------------------------------------------------------------- t2
    Console.WriteLine("== t2: FetchOnly sees new work but never moves HEAD ==");
    var feature1Sha = CommitFile(repoB, "OTHER.md", "other v1\n");
    PushToOrigin(repoB);
    var r2 = await synchronizer.SynchronizeAsync(repoA, new GitSyncOptions { FetchOnly = true });
    Check("outcome is FetchedOnly", r2.Outcome == GitSyncOutcome.FetchedOnly);
    Check("HEAD did not move", r2.HeadSha == seedSha);
    Check("no dirty state invented", !r2.IsDirty);

    // ---------------------------------------------------------------- t3
    Console.WriteLine("== t3: fast-forward pull captures the new commit SHA ==");
    var r3 = await synchronizer.SynchronizeAsync(repoA);
    Check("outcome is FastForwarded", r3.Outcome == GitSyncOutcome.FastForwarded);
    Check("HeadSha is the pushed commit", r3.HeadSha == feature1Sha);
    Check("HeadShaBeforeSync is the old commit", r3.HeadShaBeforeSync == seedSha);
    Check("Pulled is true", r3.Pulled);
    Check("SHA is 40 lowercase hex", r3.HeadSha.Length == 40 && r3.HeadSha.All(Uri.IsHexDigit));
    Check("pulled file actually checked out into the working dir",
        File.Exists(Path.Combine(repoA, "OTHER.md")));

    // ---------------------------------------------------------------- t4
    Console.WriteLine("== t4: second sync is UpToDate ==");
    var r4 = await synchronizer.SynchronizeAsync(repoA);
    Check("outcome is UpToDate", r4.Outcome == GitSyncOutcome.UpToDate);
    Check("HEAD unchanged", r4.HeadSha == feature1Sha);

    // ---------------------------------------------------------------- t5
    Console.WriteLine("== t5: untracked file blocks the pull and is reported ==");
    File.WriteAllText(Path.Combine(repoA, "new-notes.txt"), "untracked\n");
    var r5 = await synchronizer.SynchronizeAsync(repoA);
    Check("outcome is SkippedDirtyTree", r5.Outcome == GitSyncOutcome.SkippedDirtyTree);
    Check("untracked file listed", r5.UntrackedFiles.Contains("new-notes.txt"));
    Check("no uncommitted tracked changes claimed", !r5.HasUncommittedChanges);
    Check("IsDirty reflects the untracked file", r5.IsDirty);
    Check("HEAD untouched", r5.HeadSha == feature1Sha);

    // ---------------------------------------------------------------- t6
    Console.WriteLine("== t6: modified tracked file blocks the pull ==");
    File.WriteAllText(Path.Combine(repoA, "README.md"), "# locally modified\n");
    var r6 = await synchronizer.SynchronizeAsync(repoA);
    Check("outcome is SkippedDirtyTree", r6.Outcome == GitSyncOutcome.SkippedDirtyTree);
    Check("modified file listed as uncommitted", r6.UncommittedFiles.Contains("README.md"));
    Check("HEAD untouched", r6.HeadSha == feature1Sha);

    // ---------------------------------------------------------------- t7
    Console.WriteLine("== t7: PullEvenIfDirty fast-forwards and preserves the unrelated local edit ==");
    var feature2Sha = CommitFile(repoB, "OTHER.md", "other v2\n");
    PushToOrigin(repoB);
    var r7 = await synchronizer.SynchronizeAsync(repoA, new GitSyncOptions { PullEvenIfDirty = true });
    Check("outcome is FastForwarded", r7.Outcome == GitSyncOutcome.FastForwarded);
    Check("HEAD advanced to feature2", r7.HeadSha == feature2Sha);
    Check("local uncommitted edit survived the pull",
        File.ReadAllText(Path.Combine(repoA, "README.md")) == "# locally modified\n");

    // ---------------------------------------------------------------- t8
    Console.WriteLine("== t8: divergence is refused — never an auto merge commit ==");
    // Clean t5's untracked file first: the dirty-tree policy would (correctly)
    // skip the pull before the merge machinery even runs, masking the
    // divergence check we want to exercise here.
    File.Delete(Path.Combine(repoA, "new-notes.txt"));
    string localCommitSha;
    using (var repo = new Repository(repoA))
    {
        File.WriteAllText(Path.Combine(repoA, "README.md"), "# locally modified (now committed)\n");
        Commands.Stage(repo, "README.md");
        var sig = new Signature("DeployToolkit Test", "test@deploytoolkit.local", DateTimeOffset.UtcNow);
        localCommitSha = repo.Commit("local-only commit", sig, sig).Sha;
    }
    var feature3Sha = CommitFile(repoB, "OTHER.md", "other v3\n");
    PushToOrigin(repoB);

    DivergedBranchException? diverged = null;
    try
    {
        await synchronizer.SynchronizeAsync(repoA);
    }
    catch (DivergedBranchException ex)
    {
        diverged = ex;
    }
    Check("DivergedBranchException was thrown", diverged is not null);
    Check("exception names both sides", diverged is not null
        && diverged.LocalSha == localCommitSha && diverged.RemoteSha == feature3Sha);
    using (var repo = new Repository(repoA))
    {
        Check("HEAD is still the local commit (nothing merged, nothing moved)",
            repo.Head.Tip.Sha == localCommitSha);
        Check("no merge commit was created (single parent)",
            repo.Head.Tip.Parents.Count() == 1);
    }

    // ---------------------------------------------------------------- t9
    Console.WriteLine("== t9: non-repo folder produces an actionable error ==");
    var notARepo = Path.Combine(workRoot, "not-a-repo");
    Directory.CreateDirectory(notARepo);
    File.WriteAllText(Path.Combine(notARepo, "foo.txt"), "not version controlled\n");
    InvalidOperationException? notRepo = null;
    try
    {
        await synchronizer.SynchronizeAsync(notARepo);
    }
    catch (InvalidOperationException ex)
    {
        notRepo = ex;
    }
    Check("InvalidOperationException thrown", notRepo is not null);
    Check("message explains it's not a git working folder", notRepo is not null
        && notRepo.Message.Contains("not a git working folder"));

    // ---------------------------------------------------------------- t10
    Console.WriteLine("== t10: dirty state is re-reported on the successful outcome path ==");
    // repoA is diverged from t8, so exercise dirty-state reporting on repoB
    // (which is clean and up to date after its pushes).
    File.WriteAllText(Path.Combine(repoB, "untracked-b.txt"), "b\n");
    var r10 = await synchronizer.SynchronizeAsync(repoB);
    Check("outcome is SkippedDirtyTree for repoB too", r10.Outcome == GitSyncOutcome.SkippedDirtyTree);
    Check("untracked file listed", r10.UntrackedFiles.Contains("untracked-b.txt"));
}
finally
{
    try { Directory.Delete(workRoot, recursive: true); } catch { /* best effort */ }
}


// ---------------------------------------------------------------- t11
Console.WriteLine("== t11: git credential chain (the 401 fix) ==");
// LibGit2Sharp performs no OS credential lookup of its own; the synchronizer
// now resolves credentials through a chain (URL → options → Windows
// Credential Manager) with a one-shot prompt on auth failure. These checks
// pin the pure parts of that chain.

var devopsUrl = "https://dev.azure.com/org/project/_git/repo";
var devopsRequest = GitCredentialRequest.FromUrl(devopsUrl);
Check("FromUrl parses scheme/host for an Azure DevOps remote",
    devopsRequest.Scheme == "https" && devopsRequest.Host == "dev.azure.com" && devopsRequest.Port is null
    && devopsRequest.UsernameFromUrl is null);
Check("FromUrl keeps a non-http remote without host (no credentials apply)",
    GitCredentialRequest.FromUrl("/some/folder").Host.Length == 0);

var embedded = new UrlEmbeddedCredentialSource()
    .Resolve(GitCredentialRequest.FromUrl("https://user:p%40ss@dev.azure.com/org/_git/repo"));
Check("URL-embedded credentials resolve (user + unescaped secret)",
    embedded is { } embeddedHit && embeddedHit.Username == "user" && embeddedHit.Password == "p@ss");
Check("URL with username but no secret yields no credential",
    new UrlEmbeddedCredentialSource().Resolve(GitCredentialRequest.FromUrl("https://user@dev.azure.com/x")) is null);
Check("plain URL yields no embedded credential",
    new UrlEmbeddedCredentialSource().Resolve(devopsRequest) is null);

var targets = WindowsCredentialManagerSource.BuildCandidateTargets(devopsRequest);
Check("credential-manager targets include the GCM canonical git:https://host",
    targets.Contains("git:https://dev.azure.com"));
Check("credential-manager targets include a first-path-segment spelling",
    targets.Contains("git:https://dev.azure.com/org"));
Check("credential-manager targets include the bare-URL fallback",
    targets.Contains("https://dev.azure.com"));
Check("credential-manager target list has no duplicates",
    targets.Length == targets.Distinct().Count());
Check("credential-manager targets are empty for non-http remotes",
    WindowsCredentialManagerSource.BuildCandidateTargets(GitCredentialRequest.FromUrl("/local/path")).Length == 0);

var portRequest = GitCredentialRequest.FromUrl("https://git.example.com:8443/team/repo.git");
Check("FromUrl keeps a non-default port and the targets probe the portful spelling",
    portRequest.Port == "8443" &&
    WindowsCredentialManagerSource.BuildCandidateTargets(portRequest).Contains("git:https://git.example.com:8443"));

var chain = new GitCredentialChain(
    new UrlEmbeddedCredentialSource(),
    new DelegateCredentialSource("options", _ => null),
    new DelegateCredentialSource("fallback", _ => new GitCredential("u", "p")));
Check("chain skips empty sources and returns the first hit",
    chain.Resolve(devopsRequest) is { } chainHit && chainHit.Username == "u" && chainHit.Password == "p");
Check("chain Describe() names every source in order",
    chain.Describe() == "remote URL → options → fallback");
Check("empty chain finds nothing",
    new GitCredentialChain().Resolve(devopsRequest) is null);
Check("credential-manager source is inert off-Windows (or simply finds nothing)",
    OperatingSystem.IsWindows() || new WindowsCredentialManagerSource().Resolve(devopsRequest) is null);

Check("auth-failure detector recognizes the exact 401 libgit2 wording",
    LibGit2Synchronizer.IsAuthenticationFailure("request failed with status code: 401"));
Check("auth-failure detector recognizes 403 and 'authentication' phrasings",
    LibGit2Synchronizer.IsAuthenticationFailure("request failed with status code: 403") &&
    LibGit2Synchronizer.IsAuthenticationFailure("too many redirects or authentication replays"));
Check("auth-failure detector ignores network/not-found errors",
    !LibGit2Synchronizer.IsAuthenticationFailure("request failed with status code: 404") &&
    !LibGit2Synchronizer.IsAuthenticationFailure("connection timed out"));

// == GitEndpointProbe: endpoint parsing (pure) + injectable-connector probe ==

Check("probe parses a plain https URL (default 443)",
    GitEndpointProbe.ParseEndpoint("https://git.example.com/org/repo.git") == ("git.example.com", 443));
Check("probe parses embedded credentials away and keeps an explicit port",
    GitEndpointProbe.ParseEndpoint("https://build:s3cret@git.example.com:7990/scm/p.git") == ("git.example.com", 7990));
Check("probe parses ssh (default 22) and http (default 80)",
    GitEndpointProbe.ParseEndpoint("ssh://git@server/path/repo.git") == ("server", 22) &&
    GitEndpointProbe.ParseEndpoint("http://server/repo") == ("server", 80));
Check("probe parses the git:// protocol to its reserved port 9418",
    GitEndpointProbe.ParseEndpoint("git://server/repo.git") == ("server", 9418));
Check("probe parses an unknown scheme to 443",
    GitEndpointProbe.ParseEndpoint("vault://server/repo") == ("server", 443));
Check("probe parses a scp-like remote to port 22",
    GitEndpointProbe.ParseEndpoint("git@github.com:group/repo.git") == ("github.com", 22));
Check("probe parses an IPv6 literal with and without a port",
    GitEndpointProbe.ParseEndpoint("https://[2001:db8::1]:8443/repo.git") == ("2001:db8::1", 8443) &&
    GitEndpointProbe.ParseEndpoint("https://[2001:db8::1]/repo.git") == ("2001:db8::1", 443));
Check("probe returns null for local paths and file remotes (nothing to probe)",
    GitEndpointProbe.ParseEndpoint(@"C:\code\repo") is null &&
    GitEndpointProbe.ParseEndpoint("/srv/git/repo") is null &&
    GitEndpointProbe.ParseEndpoint("file:///srv/git/repo") is null &&
    GitEndpointProbe.ParseEndpoint("") is null &&
    GitEndpointProbe.ParseEndpoint("   ") is null);
Check("probe skips the connect entirely for local paths (no throw)",
    GitEndpointProbe.ProbeAsync("/srv/git/repo").IsCompletedSuccessfully ||
    GitEndpointProbe.ProbeAsync("/srv/git/repo").Status == TaskStatus.RanToCompletion);
Check("probe succeeds silently when the connector succeeds",
    GitEndpointProbe.ProbeAsync("https://git.example.com/repo.git",
        connect: (_, _, _, _) => Task.CompletedTask).Status == TaskStatus.RanToCompletion);
try
{
    await GitEndpointProbe.ProbeAsync("https://git.example.com/repo.git",
        timeout: TimeSpan.FromMilliseconds(50),
        connect: (_, _, _, _) => Task.Delay(TimeSpan.FromSeconds(10)));
    Check("probe throws on a black-holed server", false);
}
catch (InvalidOperationException ex)
{
    Check("probe throws a friendly error naming the endpoint on timeout",
        ex.Message.Contains("git.example.com:443") && ex.Message.Contains("network / VPN"));
}
try
{
    await GitEndpointProbe.ProbeAsync("https://git.example.com:8443/repo.git",
        connect: (_, _, _, _) => throw new System.Net.Sockets.SocketException());
    Check("probe throws on a refused connection", false);
}
catch (InvalidOperationException ex)
{
    Check("probe wraps connector failures with the endpoint in the message",
        ex.Message.Contains("git.example.com:8443"));
}
Check("probe rethrows caller cancellation untouched",
    await CaptureStateAsync(() => GitEndpointProbe.ProbeAsync("https://git.example.com/repo.git",
        connect: (_, _, _, ct) => Task.FromCanceled(ct), cancellationToken: new CancellationToken(canceled: true))));

static async Task<bool> CaptureStateAsync(Func<Task> run)
{
    try { await run(); return false; }
    catch (OperationCanceledException) { return true; }
    catch { return false; }
}

// ---------------------------------------------------------------
Console.WriteLine("== GitTagger.FormatTagName (tag template formatting) ==");
Check("default template: deploy-{version}-{date}",
    GitTagger.FormatTagName("deploy-{version}-{date}", "1.0.2", "Website",
        new DateTimeOffset(2026, 9, 2, 14, 30, 15, TimeSpan.Zero)) == "deploy-1.0.2-20260902");
Check("{datetime} placeholder expands to yyyyMMdd-HHmmss",
    GitTagger.FormatTagName("deploy-{version}-{datetime}", "2.0.0", "CMS",
        new DateTimeOffset(2026, 9, 2, 14, 30, 15, TimeSpan.Zero)) == "deploy-2.0.0-20260902-143015");
Check("{component} placeholder is used + sanitized",
    GitTagger.FormatTagName("deploy/{component}/{version}", "1.0", "Web Site",
        new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)) == "deploy/Web_Site/1.0");
Check("empty template returns empty string",
    GitTagger.FormatTagName("", "1.0", "X") == string.Empty);
Check("null template returns empty string",
    GitTagger.FormatTagName(null!, "1.0", "X") == string.Empty);
Check("case-insensitive placeholders",
    GitTagger.FormatTagName("deploy-{VERSION}-{DATE}", "1.0.2", "CMS",
        new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)) == "deploy-1.0.2-20260902");
Check("special chars in component are sanitized to _",
    GitTagger.FormatTagName("deploy-{component}", "1.0", "Web~Site^v2",
        new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)) == "deploy-Web_Site_v2");

Console.WriteLine();
Console.WriteLine($"== {passed} passed, {failures.Count} failed ==");
if (failures.Count > 0)
{
    Console.WriteLine("Failures:");
    foreach (var f in failures) Console.WriteLine($"  - {f}");
    Environment.Exit(1);
}
