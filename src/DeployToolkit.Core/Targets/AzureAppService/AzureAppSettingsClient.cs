using System.Text;
using System.Text.Json;

namespace DeployToolkit.Core.Targets.AzureAppService;

/// <summary>
/// Azure App Service application settings via the ARM Configuration API —
/// Azure's equivalent of the appsettings file merge (plan §7/§8.2: "same
/// delta data, different execution path"). GET returns the site's current
/// settings, the delta is applied on top, and the FULL merged set is PUT
/// back (ARM replaces the whole collection — that's how removal works too).
///
/// Auth goes through an <see cref="ArmTokenProvider"/> so this project
/// carries no Azure SDK dependency; the UI plugs in Azure.Identity.
/// </summary>
public sealed class AzureAppSettingsClient
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ArmTokenProvider? _tokenProvider;

    public AzureAppSettingsClient(
        ArmTokenProvider? tokenProvider = null,
        HttpClient? httpClient = null)
    {
        _tokenProvider = tokenProvider;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
    }

    /// <summary>Fetches the site's current application settings
    /// (the "properties" map of the ARM resource).</summary>
    public async Task<Dictionary<string, string>> GetAppSettingsAsync(
        AzureTargetSettings target, CancellationToken cancellationToken = default)
    {
        using var response = await SendArmAsync(HttpMethod.Get, target.AppSettingsUri, content: null, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ARM GET app settings failed: HTTP {(int)response.StatusCode} — {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in properties.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    result[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }
        return result;
    }

    /// <summary>Puts the full settings collection back. ARM semantics:
    /// whatever is in the body becomes the site's settings — keys absent
    /// from the body are REMOVED. Always fetch current settings and merge
    /// (see <see cref="MergeDelta"/>) rather than PUT-ing the delta alone.</summary>
    public async Task<bool> PutAppSettingsAsync(
        AzureTargetSettings target,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new { properties = settings });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await SendArmAsync(HttpMethod.Put, target.AppSettingsUri, content, cancellationToken)
            .ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Applies a manifest's AppSettingsDelta (plan §3 manifest
    /// schema) on top of current settings. Values convert like the file
    /// merger: bool → "true"/"false", numbers → invariant string, anything
    /// else → ToString(); a null value means "remove this key".</summary>
    public static Dictionary<string, string> MergeDelta(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, object?> delta)
    {
        var merged = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var (key, value) in delta)
        {
            if (value is null)
                merged.Remove(key);
            else
                merged[key] = ConvertToString(value);
        }
        return merged;
    }

    private async Task<HttpResponseMessage> SendArmAsync(
        HttpMethod method, string uri, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (content is not null)
            request.Content = content;

        var token = _tokenProvider is null
            ? null
            : await _tokenProvider(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "No ARM token available — supply an ArmTokenProvider (Azure.Identity in the UI layer) to manage app settings.");

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static string ConvertToString(object? value) => value switch
    {
        bool b => b ? "true" : "false",
        null => string.Empty,
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";
}
