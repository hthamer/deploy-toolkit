using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DeployToolkit.Core.Git;

/// <summary>A username/secret pair for git over HTTP(S) (username + password
/// or personal access token).</summary>
public sealed record GitCredential(string Username, string Password);

/// <summary>
/// Everything a credential source may need to look up credentials for one
/// remote. Built from the remote URL by
/// <see cref="GitCredentialRequest.FromUrl"/>; keep the host/target-name
/// derivation logic pure so it is headless-testable.
/// </summary>
public sealed record GitCredentialRequest(string Url, string Scheme, string Host, string? Port, string? UsernameFromUrl)
{
    public static GitCredentialRequest FromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            var port = uri.IsDefaultPort ? null : uri.Port.ToString();
            return new GitCredentialRequest(url, uri.Scheme, uri.Host, port,
                string.IsNullOrEmpty(uri.UserInfo) ? null : uri.UserInfo.Split(':')[0]);
        }

        // Non-HTTP(S) remote (SSH, file) — no credentials apply, but keep the
        // URL so diagnostics are honest about what was attempted.
        return new GitCredentialRequest(url, string.Empty, string.Empty, null, null);
    }
}

/// <summary>One place DeployToolkit looks for git credentials.</summary>
public interface IGitCredentialSource
{
    /// <summary>Human-readable source name (used in failure diagnostics).</summary>
    string Name { get; }

    /// <summary>Returns the credential, or null when this source has none.</summary>
    GitCredential? Resolve(GitCredentialRequest request);
}

/// <summary>Credentials embedded in the remote URL itself
/// (<c>https://user:secret@host/…</c>). Only when a secret is present — a
/// bare <c>user@host</c> URL cannot authenticate.</summary>
public sealed class UrlEmbeddedCredentialSource : IGitCredentialSource
{
    public string Name => "remote URL";

    public GitCredential? Resolve(GitCredentialRequest request)
    {
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) &&
            !string.IsNullOrEmpty(uri.UserInfo) &&
            uri.UserInfo.Contains(':'))
        {
            var separator = uri.UserInfo.IndexOf(':');
            var user = uri.UserInfo[..separator];
            var secret = uri.UserInfo[(separator + 1)..];
            if (secret.Length > 0)
                return new GitCredential(Uri.UnescapeDataString(user), Uri.UnescapeDataString(secret));
        }

        return null;
    }
}

/// <summary>Wraps a caller-provided resolver (an explicit credential or a
/// UI prompt already turned into a delegate).</summary>
public sealed class DelegateCredentialSource : IGitCredentialSource
{
    private readonly Func<GitCredentialRequest, GitCredential?> _resolver;

    public string Name { get; }

    public DelegateCredentialSource(string name, Func<GitCredentialRequest, GitCredential?> resolver)
    {
        Name = name;
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public GitCredential? Resolve(GitCredentialRequest request) => _resolver(request);
}

/// <summary>
/// Reads the credentials Git Credential Manager / Visual Studio store in the
/// Windows Credential Manager (generic credentials with target names such as
/// <c>git:https://dev.azure.com</c>). Pure in-process — no git.exe, so the
/// "no shell-outs" posture of plan §5 stays intact. Inert off-Windows.
/// </summary>
public sealed class WindowsCredentialManagerSource : IGitCredentialSource
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public string Name => "Windows Credential Manager";

    public GitCredential? Resolve(GitCredentialRequest request)
        => IsSupported && request.Host.Length > 0
            ? WindowsCredManager.TryRead(BuildCandidateTargets(request))
            : null;

    /// <summary>Persists a credential under the canonical target so the next
    /// sync finds it without prompting ("remember me"). Returns false when
    /// unsupported or the OS refused the write.</summary>
    public static bool TryRemember(GitCredentialRequest request, GitCredential credential)
        => IsSupported && request.Host.Length > 0 &&
           WindowsCredManager.TryWrite(
               $"git:{request.Scheme}://{request.Host}", credential.Username, credential.Password);

    /// <summary>PURE: the target names to probe, in priority order. GCM
    /// stores <c>git:{scheme}://{host}</c>; older tooling used port/pathful
    /// or bare-URL spellings, so those are probed too.</summary>
    internal static string[] BuildCandidateTargets(GitCredentialRequest request)
    {
        if (request.Host.Length == 0)
            return Array.Empty<string>();

        var candidates = new List<string>
        {
            $"git:{request.Scheme}://{request.Host}",
        };
        if (request.Port is { } port)
            candidates.Add($"git:{request.Scheme}://{request.Host}:{port}");
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segments)
        {
            candidates.Add($"git:{request.Scheme}://{request.Host}/{segments[0]}");
        }
        candidates.Add($"{request.Scheme}://{request.Host}");
        return candidates.Distinct().ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsCredManager
    {
        private const int CredTypeGeneric = 1;
        private const int CredPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWriteW(ref CREDENTIAL credential, int flags);

        [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDeleteW(string target, int type, int flags);

        [DllImport("advapi32")]
        private static extern void CredFree(IntPtr buffer);

        public static GitCredential? TryRead(string[] candidateTargets)
        {
            foreach (var target in candidateTargets)
            {
                if (!CredReadW(target, CredTypeGeneric, 0, out var credentialPtr))
                    continue;

                try
                {
                    var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                    var username = credential.UserName == IntPtr.Zero
                        ? string.Empty
                        : Marshal.PtrToStringUni(credential.UserName) ?? string.Empty;
                    var secret = string.Empty;
                    if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
                    {
                        var blob = new byte[credential.CredentialBlobSize];
                        Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                        secret = DecodeBlob(blob);
                    }

                    if (secret.Length == 0)
                        continue; // an empty secret can never authenticate

                    // Some writers store only the secret (the PAT) with no
                    // username — Azure DevOps accepts any non-empty username
                    // with a PAT.
                    if (username.Length == 0)
                        username = "PersonalAccessToken";

                    return new GitCredential(username, secret);
                }
                finally
                {
                    CredFree(credentialPtr);
                }
            }

            return null;
        }

        public static bool TryWrite(string target, string username, string secret)
        {
            // Replace any stale entry first — CredWrite would keep the old
            // blob when the new one is rejected for some reason.
            CredDeleteW(target, CredTypeGeneric, 0);

            var blob = Encoding.Unicode.GetBytes(secret);
            var blobBuffer = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
            try
            {
                Marshal.Copy(blob, 0, blobBuffer, blob.Length);
                var credential = new CREDENTIAL
                {
                    Type = CredTypeGeneric,
                    TargetName = Marshal.StringToHGlobalUni(target),
                    UserName = Marshal.StringToHGlobalUni(username),
                    CredentialBlob = blobBuffer,
                    CredentialBlobSize = blob.Length,
                    Persist = CredPersistLocalMachine,
                };
                var ok = CredWriteW(ref credential, 0);
                Marshal.FreeHGlobal(credential.TargetName);
                Marshal.FreeHGlobal(credential.UserName);
                if (!ok)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(blobBuffer);
            }
        }

        /// <summary>GCM writes UTF-16 blobs; plain PAT writers use ASCII/UTF-8.
        /// The high bytes of ASCII text are zero, which disambiguates.</summary>
        private static string DecodeBlob(byte[] blob)
        {
            if (blob.Length >= 2 && blob[1] == 0 && blob[0] != 0)
                return Encoding.Unicode.GetString(blob).TrimEnd('\0');
            return Encoding.UTF8.GetString(blob).TrimEnd('\0');
        }
    }
}

/// <summary>Resolves credentials by asking each source in order and returning
/// the first hit.</summary>
public sealed class GitCredentialChain : IGitCredentialSource
{
    private readonly IReadOnlyList<IGitCredentialSource> _sources;

    public GitCredentialChain(IEnumerable<IGitCredentialSource> sources)
        => _sources = sources.ToArray();

    public GitCredentialChain(params IGitCredentialSource[] sources)
        => _sources = sources;

    public string Name => "credential chain";

    public GitCredential? Resolve(GitCredentialRequest request)
    {
        foreach (var source in _sources)
        {
            var credential = source.Resolve(request);
            if (credential is not null)
                return credential;
        }

        return null;
    }

    /// <summary>Source names in probe order, for actionable diagnostics.</summary>
    public string Describe() => string.Join(" → ", _sources.Select(s => s.Name));
}

/// <summary>Raised when a fetch is refused for missing/invalid credentials
/// after every configured source (and the interactive prompt, when wired)
/// had its chance. The message is written for a build/deploy operator.</summary>
public sealed class GitAuthenticationException : Exception
{
    public string Host { get; }

    public GitAuthenticationException(string host, string triedSources, Exception inner)
        : base($"Git authentication failed for '{host}'. DeployToolkit tried, in order: {triedSources}. " +
               "None matched the server. Fix: run 'git fetch' in the repository once (refreshes Git Credential Manager), " +
               "or enter a personal access token when prompted and tick \"Remember\" so it is stored for next time.", inner)
    {
        Host = host;
    }
}
