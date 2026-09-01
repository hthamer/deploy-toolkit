using System.Net.Http.Headers;
using System.Text;

namespace DeployToolkit.Core.Targets.Plesk;

/// <summary>Outcome of one Plesk XML API call.</summary>
/// <param name="Success">
/// True only when the HTTP call returned 2xx AND the response body did not
/// contain a Plesk error status.
/// </param>
/// <param name="HttpStatus">
/// HTTP status code of the response, or 0 when the request never completed
/// (connection failure, DNS, TLS).
/// </param>
/// <param name="ResponseBody">
/// Raw response body (or exception message when HttpStatus is 0) — kept for
/// diagnostics; the UI layer decides what to show.
/// </param>
public sealed record PleskApiResult(bool Success, int HttpStatus, string ResponseBody);

/// <summary>
/// Restart-over-API client for Plesk's XML API.
///
/// ⚠ NEEDS REAL-CLIENT VALIDATION (plan §7): the request shape below is the
/// documented HTTP contract — POST to <c>{panel}/enterprise/control/agent.php</c>,
/// <c>text/xml</c> body, HTTP Basic auth — which is stable across Plesk
/// versions, but the exact restart OPERATION inside the packet is flagged as
/// unverified: Plesk's XML API schema differs between versions and some
/// operations (e.g. <c>site/restart</c> vs a CLI bridge) behave differently
/// per hosting configuration. Until validated, treat a Successful result as
/// "the API accepted the packet", not as "the site definitely restarted".
///
/// The packet is a single public constant template with a {{SITE_ID}}
/// placeholder — swap it in one line once validated against your client's
/// Plesk version (see README checklist). The <see cref="HttpClient"/> is
/// injectable so self-tests can fake the endpoint.
/// </summary>
public sealed class PleskXmlApiClient : IDisposable
{
    /// <summary>
    /// Plesk XML API request packet used for the restart call. Replace the
    /// {{SITE_ID}} placeholder via <see cref="BuildRestartPacket"/>. This is
    /// deliberately a public const: after validating against a real Plesk
    /// client, change exactly this string (e.g. to a different operator or
    /// to a `websites` CLI bridge) without touching any logic.
    /// </summary>
    public const string DefaultRestartPacketTemplate =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <packet version="1.6.9.0">
          <site>
            <restart>
              <filter>
                <id>{{SITE_ID}}</id>
              </filter>
            </restart>
          </site>
        </packet>
        """;

    /// <summary>Fixed endpoint path of the Plesk XML API.</summary>
    public const string AgentPath = "/enterprise/control/agent.php";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <param name="httpClient">
    /// Client used for the request (injectable for tests; a timeout of ~15s
    /// is sensible for a panel API).
    /// </param>
    /// <param name="ownsHttpClient">
    /// When true, disposing this client also disposes the HttpClient (use
    /// when the api client created it itself).
    /// </param>
    public PleskXmlApiClient(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>
    /// Produces the request packet by substituting {{SITE_ID}} in
    /// <see cref="DefaultRestartPacketTemplate"/> (empty when no SiteId is
    /// configured — some Plesk operations act on the whole subscription).
    /// </summary>
    public static string BuildRestartPacket(string? siteId) =>
        DefaultRestartPacketTemplate.Replace("{{SITE_ID}}", siteId ?? string.Empty);

    /// <summary>
    /// Sends the restart request for the configured site.
    /// Never throws for HTTP-level problems — those are returned as an
    /// unsuccessful <see cref="PleskApiResult"/>; only truly misconfigured
    /// options (no base URL / credentials) throw InvalidOperationException
    /// so callers fail fast with an actionable message. Cancellation still
    /// propagates as OperationCanceledException.
    /// </summary>
    public async Task<PleskApiResult> SendRestartRequestAsync(PleskDeployOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var baseUrl = options.XmlApiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "RestartMode.XmlApi requires PleskDeployOptions.XmlApiBaseUrl (e.g. \"https://plesk.example.com:8443\").");
        }

        if (string.IsNullOrEmpty(options.XmlApiLogin) || options.XmlApiPassword is null)
        {
            throw new InvalidOperationException(
                "RestartMode.XmlApi requires PleskDeployOptions.XmlApiLogin and XmlApiPassword.");
        }

        var url = baseUrl.TrimEnd('/') + AgentPath;
        var packet = BuildRestartPacket(options.SiteId);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(packet, Encoding.UTF8, "text/xml"),
        };

        // Plesk XML API accepts HTTP Basic auth with panel credentials.
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.XmlApiLogin}:{options.XmlApiPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new PleskApiResult(Success: false, HttpStatus: 0, ResponseBody: $"HTTP request to the Plesk XML API failed: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            // Non-2xx → not ok, body kept for diagnostics.
            if (!response.IsSuccessStatusCode)
            {
                return new PleskApiResult(Success: false, HttpStatus: status, ResponseBody: body);
            }

            // Plesk answers 200 even for rejected packets, with
            // <status>error</status> inside the response XML.
            if (body.Contains("<status>error</status>", StringComparison.OrdinalIgnoreCase))
            {
                return new PleskApiResult(Success: false, HttpStatus: status, ResponseBody: body);
            }

            return new PleskApiResult(Success: true, HttpStatus: status, ResponseBody: body);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
