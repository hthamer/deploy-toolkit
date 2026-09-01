namespace DeployToolkit.Core.Packaging;

/// <summary>
/// The sensitive-files policy for a delta package: files whose names match
/// the well-known configuration / secrets set are NEVER packaged, because
/// overwriting them on the target server is dangerous — the build machine's
/// copy may have local developer secrets, connection strings, or API keys
/// that don't belong in production, and a delta package has no trace of
/// them, so replacing them blindly could leave the site broken or leak
/// secrets. User request: "always make sure to ignore the sensitive files
/// like appsettings.json and web.config as we don't publish it to
/// production (we don't have any trace of these files, so replacing it in
/// production is very dangerous step)."
///
/// The filter is enforced centrally in <see cref="PackageBuilder.BuildAsync"/>
/// so it CANNOT be bypassed from the UI — even if the user checks "Include"
/// on an <c>appsettings.json</c> row in the diff grid, the build still drops
/// it. The UI surfaces these rows as permanently-excluded (disabled checkbox)
/// so the policy is visible, not silent.
///
/// Matching is by <b>file name</b> (case-insensitive, any folder depth), so
/// <c>appsettings.json</c>, <c>appsettings.Development.json</c>,
/// <c>appsettings.Production.json</c>, <c>wwwroot/appsettings.json</c> and
/// <c>Config\appsettings.Staging.json</c> all match — but
/// <c>appsettings.json.bak</c>, <c>appsettings.example.json</c> and files
/// merely <i>starting</i> with <c>appsettings</c> do NOT (the whole file name
/// must equal one of the known entries, after lower-casing).
/// </summary>
public static class SensitiveFileFilter
{
    /// <summary>
    /// The well-known sensitive file names excluded from every delta package.
    /// Stored lower-cased for an O(1) case-insensitive contains check.
    /// Covers:
    ///  - <c>appsettings.json</c> and its per-environment variants
    ///    (<c>appsettings.Development.json</c>, <c>appsettings.Production.json</c>,
    ///    <c>appsettings.Staging.json</c>, …) — ASP.NET Core config + secrets.
    ///  - <c>web.config</c> and the classic <c>app.config</c> /
    ///    <c>&lt;assembly&gt;.dll.config</c> — may carry connection strings and
    ///    machine keys; classic WebForms/ASP.NET apps put secrets in web.config.
    ///  - <c>connectionstrings.json</c> — explicit connection-string store.
    ///  - <c>secrets.json</c> — the ASP.NET Core user-secrets file name.
    /// </summary>
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "appsettings.json",
        "appsettings.development.json",
        "appsettings.staging.json",
        "appsettings.production.json",
        "appsettings.test.json",
        "appsettings.local.json",
        "web.config",
        "app.config",
        "connectionstrings.json",
        "secrets.json",
    };

    /// <summary>
    /// True when <paramref name="manifestPath"/>'s file name (the last segment
    /// of the forward-slash relative path the manifest uses) matches one of
    /// the sensitive names. Case-insensitive; matches at any folder depth.
    /// </summary>
    public static bool IsSensitive(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            return false;

        var name = manifestPath;
        var lastSlash = manifestPath.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < manifestPath.Length - 1)
            name = manifestPath[(lastSlash + 1)..];

        return SensitiveNames.Contains(name);
    }

    /// <summary>
    /// True when the file-name part of <paramref name="manifestPath"/> starts
    /// with <c>appsettings.</c> and ends with <c>.json</c> — catches any
    /// per-environment <c>appsettings.&lt;EnvName&gt;.json</c> variant not
    /// enumerated in <see cref="SensitiveNames"/> (e.g. a custom
    /// <c>appsettings.QA.json</c>). Kept separate from the exact-name set so
    /// a typo like <c>appsettings_extra.json</c> still ships (it's not a known
    /// env file). Use <see cref="IsSensitive"/> for the final verdict.
    /// </summary>
    private static bool IsAppSettingsEnvVariant(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        return fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && fileName.Length > "appsettings.json".Length;
    }

    /// <summary>
    /// The full verdict: exact-name match OR an <c>appsettings.*.json</c>
    /// per-environment variant. Used by <see cref="PackageBuilder.BuildAsync"/>
    /// to drop the file from the delta, and by the diff-step UI to render the
    /// row as permanently-excluded.
    /// </summary>
    public static bool IsSensitiveOrAppSettingsVariant(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            return false;

        var name = manifestPath;
        var lastSlash = manifestPath.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < manifestPath.Length - 1)
            name = manifestPath[(lastSlash + 1)..];

        return SensitiveNames.Contains(name) || IsAppSettingsEnvVariant(name);
    }
}
