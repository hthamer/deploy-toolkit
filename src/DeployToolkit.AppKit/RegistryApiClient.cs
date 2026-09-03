using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeployToolkit.AppKit;

/// <summary>
/// Client for the central registry REST API's authentication endpoint:
/// POST <c>{baseUrl}/api/auth/authenticate</c> with a JSON body
/// <c>{"username": …, "password": …}</c>. HTTP 200 means authenticated;
/// anything else — including transport errors — fails, and the status code
/// plus the raw response body are surfaced to the caller so the UI can show
/// the API's own error message to the user.
/// Also reports completed deployments to <c>{baseUrl}/api/deploy</c>
/// (see <see cref="ReportDeploymentAsync"/>).
/// </summary>
public static class RegistryApiClient
{
    /// <summary>Path of the authenticate endpoint, relative to the base URL.
    /// Change here if the API's route differs.</summary>
    public const string AuthenticatePath = "api/auth/authenticate";

    /// <summary>Path of the deploy endpoint, relative to the base URL.
    /// Change here if the API's route differs.</summary>
    public const string DeployPath = "api/deploy";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Attempts an API login. Never throws: transport failures and
    /// non-200 responses come back as (false, detail). On success the detail
    /// is the response body (e.g. a token payload); on failure it is
    /// "HTTP {code} — {body}" or the exception message.</summary>
    public static async Task<(bool Success, string Detail)> AuthenticateAsync(
        string baseUrl, string username, string password)
    {
        var url = $"{baseUrl.TrimEnd('/')}/{AuthenticatePath}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(url, content).ConfigureAwait(false);
            var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();

            if (response.IsSuccessStatusCode)
                return (true, body);

            return (false, string.IsNullOrWhiteSpace(body)
                ? $"HTTP {(int)response.StatusCode} {response.StatusCode}"
                : $"HTTP {(int)response.StatusCode} {response.StatusCode} — {Truncate(body)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // TaskCanceledException on timeout; unwrap to a readable message.
            return (false, ex.Message);
        }
    }

    /// <summary>Reports a completed deployment to the central API's deploy
    /// endpoint (POST <c>{baseUrl}/api/deploy</c>, camelCase JSON body of
    /// <see cref="ApiDeploymentReport"/>). The session's registry credentials
    /// ride in the HTTP Basic header — the API is token-free: every request
    /// presents the username/password that match the credentials saved in the
    /// registry database. Never throws: HTTP 2xx is (true, detail), anything
    /// else is (false, detail) with the status code and the API's response
    /// body.</summary>
    public static async Task<(bool Success, string Detail)> ReportDeploymentAsync(
        string baseUrl, ApiDeploymentReport report, string? username, string? password)
    {
        var url = $"{baseUrl.TrimEnd('/')}/{DeployPath}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Token-free per-request credentials (same pair the Login button
        // verified against /api/auth/authenticate). Missing credentials are
        // still sent as no auth — the API answers 401 with a readable body.
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        }

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(report, JsonOptions),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(url, content).ConfigureAwait(false);
            var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();

            if (response.IsSuccessStatusCode)
                return (true, string.IsNullOrWhiteSpace(body)
                    ? $"HTTP {(int)response.StatusCode}"
                    : body);

            return (false, string.IsNullOrWhiteSpace(body)
                ? $"HTTP {(int)response.StatusCode} {response.StatusCode}"
                : $"HTTP {(int)response.StatusCode} {response.StatusCode} — {Truncate(body)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    private static string Truncate(string text, int maxChars = 400)
        => text.Length <= maxChars ? text : text[..maxChars] + "…";
}

/// <summary>Payload POSTed to <c>{baseUrl}/api/deploy</c> when the Deployer
/// marks a package as deployed. Serialized camelCase to match typical ASP.NET
/// API binding.</summary>
public sealed record ApiDeploymentReport(
    string PackageId,
    string Client,
    string Component,
    string Version,
    string Result, // "Success" | "Failed" | "RolledBack"
    bool HealthCheckPassed,
    string Message,
    string DeployedBy,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string TargetType);
