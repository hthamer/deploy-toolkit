namespace DeployToolkit.Core.Targets.AzureAppService;

/// <summary>Publish credentials for one app's Kudu endpoint — exactly what
/// a publish profile contains (user name + password for
/// https://{site}.scm.azurewebsites.net). Never stored in plain text in the
/// registry; keep them in a SecretVault and resolve at deploy time.</summary>
public sealed record KuduCredentials(string SiteName, string Username, string Password)
{
    /// <summary>The SCM (Kudu) base URL for the site.</summary>
    public string ScmBaseUrl =>
        $"https://{Uri.EscapeDataString(SiteName)}.scm.azurewebsites.net";

    public string BasicAuthHeaderValue
    {
        get
        {
            var raw = $"{Username}:{Password}";
            return "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        }
    }
}

/// <summary>Which ARM resource the app settings API should talk to.</summary>
public sealed record AzureTargetSettings(
    string SubscriptionId,
    string ResourceGroup,
    string SiteName)
{
    public const string ArmBaseUrl = "https://management.azure.com";
    public const string AppSettingsApiVersion = "2024-04-01";

    public string AppSettingsUri =>
        $"{ArmBaseUrl}/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}" +
        $"/providers/Microsoft.Web/sites/{SiteName}/config/appsettings?api-version={AppSettingsApiVersion}";
}

/// <summary>
/// Supplies a bearer token for ARM calls without tying Core to the
/// Azure SDK: the UI passes <c>ct => new DefaultAzureCredential()
/// .GetTokenAsync(...)</c> (Azure.Identity lives in the UI project), or a
/// cached token, or any test stub. Null means the app-settings step will
/// be skipped — zip deploy alone is valid.
/// </summary>
public delegate Task<string?> ArmTokenProvider(CancellationToken cancellationToken);

public sealed record KuduDeployResult(bool Success, int HttpStatus, string? DeploymentId, string? ResponseBody);

/// <summary>Subset of Kudu's deployment JSON the tool cares about.</summary>
public sealed record KuduDeploymentInfo(string? Id, string? StatusText, int? Status, string? Message);
