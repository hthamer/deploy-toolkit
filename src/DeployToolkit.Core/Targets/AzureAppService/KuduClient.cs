using System.Text;
using System.Text.Json;

namespace DeployToolkit.Core.Targets.AzureAppService;

/// <summary>
/// Kudu ZipDeploy client (plan §7, AzureAppServiceExecutor): pure
/// HttpClient calls to https://{app}.scm.azurewebsites.net — no script,
/// no shell-out, and no RDP needed at all (runs from the Packager machine
/// or anywhere with network access).
///
/// ZipDeploy runs synchronously (isAsync=false) by default so the response
/// tells us whether deployment succeeded; large packages can switch to
/// isAsync=true and poll GetLatestDeploymentAsync.
/// </summary>
public sealed class KuduClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly KuduCredentials _credentials;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public KuduClient(KuduCredentials credentials, HttpClient? httpClient = null)
    {
        _credentials = credentials;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        // Sane default so the tool doesn't hang on a stalled SCM endpoint;
        // big self-contained packages may raise this.
        if (_ownsHttpClient)
            _http.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <summary>POSTs a zip to /api/zipdeploy. Success = 200 (sync) or
    /// 202 (async accepted).</summary>
    public async Task<KuduDeployResult> DeployZipAsync(
        Stream zipContent, bool isAsync = false, CancellationToken cancellationToken = default)
    {
        var url = $"{_credentials.ScmBaseUrl}/api/zipdeploy?isAsync={(isAsync ? "true" : "false")}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StreamContent(zipContent),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", _credentials.BasicAuthHeaderValue["Basic ".Length..]);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = response.Content is null ? null : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var ok = response.StatusCode == System.Net.HttpStatusCode.OK
                 || response.StatusCode == System.Net.HttpStatusCode.Accepted;
        return new KuduDeployResult(ok, (int)response.StatusCode, TryExtractDeploymentId(body), body);
    }

    /// <summary>GET /api/deployments/latest — used after an async deploy
    /// (or by the UI's "what's actually on the site" view).</summary>
    public async Task<KuduDeploymentInfo?> GetLatestDeploymentAsync(CancellationToken cancellationToken = default)
    {
        var url = $"{_credentials.ScmBaseUrl}/api/deployments/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", _credentials.BasicAuthHeaderValue["Basic ".Length..]);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            string? statusText = root.TryGetProperty("statusText", out var stEl) ? stEl.GetString() : null;
            int? status = root.TryGetProperty("status", out var st) && st.TryGetInt32(out var s) ? s : null;
            string? message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
            return new KuduDeploymentInfo(id, statusText, status, message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryExtractDeploymentId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
