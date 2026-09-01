using System.Xml.Linq;

namespace DeployToolkit.Deployer;

/// <summary>
/// Minimal, self-contained parser for Azure App Service <c>.publishsettings</c>
/// files (downloaded from the portal: "Get publish profile"). Extracts
/// exactly what <see cref="DeployToolkit.Core.Targets.AzureAppService.KuduCredentials"/>
/// needs — the SCM endpoint's site name plus its publishing user/password —
/// so the pre-flight panel can fill the Kudu fields from a file instead of
/// hand-copying them.
/// </summary>
public static class PublishSettingsFile
{
    /// <summary>The credentials extracted from the file.</summary>
    public sealed record PublishCredentials(string SiteName, string Username, string Password);

    /// <summary>
    /// Parses the given .publishsettings file. Prefers the MSDeploy profile
    /// (its publishUrl is the *.scm.azurewebsites.net Kudu endpoint); falls
    /// back to the first profile that carries the three attributes. Returns
    /// null (with <paramref name="error"/> filled) instead of throwing so the
    /// UI can show a one-line problem message.
    /// </summary>
    public static PublishCredentials? TryLoad(string path, out string? error)
    {
        error = null;
        try
        {
            var document = XDocument.Load(path);
            var profiles = document.Descendants("publishProfile").ToList();

            var profile =
                profiles.FirstOrDefault(p => (string?)p.Attribute("publishMethod") == "MSDeploy")
                ?? profiles.FirstOrDefault(p =>
                    p.Attribute("publishUrl") is not null &&
                    p.Attribute("userName") is not null &&
                    p.Attribute("userPWD") is not null);

            if (profile is null)
            {
                error = "No publish profile with an endpoint and credentials was found in the file.";
                return null;
            }

            var publishUrl = (string?)profile.Attribute("publishUrl");
            var userName = (string?)profile.Attribute("userName");
            var password = (string?)profile.Attribute("userPWD");

            if (string.IsNullOrWhiteSpace(publishUrl) || string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            {
                error = "The publish profile is missing publishUrl/userName/userPWD.";
                return null;
            }

            var siteName = ExtractSiteName(publishUrl);
            if (string.IsNullOrWhiteSpace(siteName))
            {
                error = $"Could not derive the site name from publishUrl '{publishUrl}' " +
                        "(expected a *.scm.azurewebsites.net endpoint).";
                return null;
            }

            return new PublishCredentials(siteName, userName, password);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            error = $"Could not read the publish settings file: {ex.Message}";
            return null;
        }
    }

    /// <summary>"mysite.scm.azurewebsites.net:443" → "mysite" (the SCM host
    /// prefix is the site name the Kudu client rebuilds its URL from).</summary>
    private static string? ExtractSiteName(string publishUrl)
    {
        var host = publishUrl.Trim();
        var portSeparator = host.LastIndexOf(':');
        if (portSeparator > 0)
            host = host[..portSeparator];

        const string scmSuffix = ".scm.azurewebsites.net";
        var suffixIndex = host.IndexOf(scmSuffix, StringComparison.OrdinalIgnoreCase);
        if (suffixIndex <= 0)
            return null;

        return host[..suffixIndex];
    }
}
