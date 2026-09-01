namespace DeployToolkit.Core.Git;

/// <summary>
/// Pre-flight reachability probe for a git remote. A libgit2 fetch that hits
/// a dead VPN / firewall black hole can hang for MINUTES with zero feedback
/// while the UI shows a busy state with nothing to report (and, before the
/// cancellable busy dialog, the app had to be force-closed). Probing the
/// endpoint first with a short timeout turns that hang into an immediate,
/// actionable error.
///
/// Endpoint parsing is PURE and self-test-pinned; the TCP connect itself is
/// injectable so the failure paths are testable headless.
/// </summary>
public static class GitEndpointProbe
{
    /// <summary>Default probe budget. Tight enough to fail fast, loose
    /// enough for a first DNS resolution + TCP handshake over a normal VPN.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Extracts (host, port) from a remote URL. Returns null for anything
    /// that has no network endpoint to probe (local paths, file:// remotes,
    /// empty input).
    /// </summary>
    /// <remarks>Handles:
    ///  - scheme URLs: <c>scheme://[user[:pass]@]host[:port]/path</c> with
    ///    default ports https→443, http→80, ssh→22, git→9418 (unknown
    ///    schemes default to 443);
    ///  - IPv6 literals: <c>https://[2001:db8::1]:8443/path</c>;
    ///  - scp-like syntax: <c>user@host:path/to/repo.git</c> → port 22;
    ///  - everything else (local filesystem paths) → null.</remarks>
    public static (string Host, int Port)? ParseEndpoint(string remoteUrl)
    {
        var url = remoteUrl?.Trim();
        if (string.IsNullOrEmpty(url))
            return null;

        // --- scheme://… form ---
        var schemeSeparator = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            var scheme = url[..schemeSeparator].ToLowerInvariant();
            if (scheme.Length == 0 || scheme == "file")
                return null;

            var authority = url[(schemeSeparator + 3)..];
            var pathStart = authority.IndexOfAny(['/', '?', '#']);
            if (pathStart >= 0)
                authority = authority[..pathStart];
            if (authority.Length == 0)
                return null;

            // Strip userinfo (user, or user:pass — including URL-embedded
            // tokens which are the chain's first credential source).
            var userInfo = authority.LastIndexOf('@');
            if (userInfo >= 0)
                authority = authority[(userInfo + 1)..];
            if (authority.Length == 0)
                return null;

            var defaultPort = scheme switch
            {
                "http" => 80,
                "ssh" => 22,
                "git" => 9418,
                _ => 443,
            };

            // IPv6 literal: [::1]:8443
            if (authority.StartsWith('['))
            {
                var closing = authority.IndexOf(']');
                if (closing < 0)
                    return null;
                var host = authority[1..closing];
                if (host.Length == 0)
                    return null;
                var rest = authority[(closing + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var v6Port) && v6Port > 0)
                    return (host, v6Port);
                return (host, defaultPort);
            }

            var colon = authority.LastIndexOf(':');
            if (colon > 0 && int.TryParse(authority[(colon + 1)..], out var port) && port > 0)
                return (authority[..colon], port);

            return (authority, defaultPort);
        }

        // --- scp-like form: user@host:path (no scheme) ---
        var at = url.IndexOf('@');
        var firstColon = url.IndexOf(':');
        if (at >= 0 && firstColon > at)
        {
            var host = url[(at + 1)..firstColon];
            if (host.Length > 0)
                return (host, 22);
        }

        // Anything else is a local path — nothing to probe.
        return null;
    }

    /// <summary>
    /// Probes the remote's endpoint with a short timeout. Throws
    /// <see cref="InvalidOperationException"/> naming the endpoint when the
    /// server does not accept a connection in time; returns silently when it
    /// does, and silently skips remotes without a network endpoint.
    /// </summary>
    /// <param name="connect">Injectable connector (self-tests); default is a
    /// real TCP connect with the given timeout.</param>
    public static async Task ProbeAsync(
        string remoteUrl,
        TimeSpan? timeout = null,
        Func<string, int, TimeSpan, CancellationToken, Task>? connect = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ParseEndpoint(remoteUrl);
        if (endpoint is null)
            return; // local path / file remote — nothing to probe

        var budget = timeout ?? DefaultTimeout;
        try
        {
            var connector = connect ?? DefaultConnectAsync;
            // WaitAsync here — NOT inside the connector — so the budget is
            // enforced uniformly no matter which connector runs.
            await connector(endpoint.Value.Host, endpoint.Value.Port, budget, cancellationToken)
                .WaitAsync(budget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller-initiated cancellation — propagate untouched
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Git server '{endpoint.Value.Host}:{endpoint.Value.Port}' did not accept a connection " +
                $"within {budget.TotalSeconds:0} s ({ex.GetType().Name}: {ex.Message}). " +
                "Check your network / VPN — the fetch would otherwise hang indefinitely.",
                ex);
        }
    }

    private static async Task DefaultConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        // The probe budget is enforced by ProbeAsync's WaitAsync — the
        // connector only starts the TCP connect (ct still aborts it).
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
    }
}
