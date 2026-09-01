using System.Xml.Linq;

namespace DeployToolkit.Core.Publishing;

/// <summary>
/// Reads the target framework(s) a .csproj declares, so the Packager's
/// publish step can DEFAULT the publish framework to what the project
/// actually targets instead of blindly trusting the component's stored
/// value (a stale component claiming "net10.0" must not publish a
/// .NET Framework 4.8 website with <c>-f net10.0</c>).
///
/// Handles both project styles:
///  • SDK-style: <c>&lt;TargetFramework&gt;net8.0&lt;/TargetFramework&gt;</c>
///    and/or <c>&lt;TargetFrameworks&gt;net8.0;net48&lt;/TargetFrameworks&gt;</c>
///  • Classic (non-SDK): <c>&lt;TargetFrameworkVersion&gt;v4.8&lt;/TargetFrameworkVersion&gt;</c>,
///    normalized to the modern TFM spelling ("v4.8" → "net48").
///
/// Namespaces are ignored (some tooling emits xmlns'd MSBuild) and values
/// are deduplicated in declaration order. A project that declares nothing
/// yields an empty list — the caller falls back to the component's value.
/// </summary>
public static class ProjectTargetFrameworkReader
{
    public static IReadOnlyList<string> ReadTargetFrameworks(string csprojPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(csprojPath, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return Array.Empty<string>(); // unreadable project file — caller falls back
        }

        var frameworks = new List<string>();

        void Add(string? raw)
        {
            var tfm = raw?.Trim();
            if (string.IsNullOrEmpty(tfm))
                return;
            foreach (var part in tfm!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!frameworks.Contains(part))
                    frameworks.Add(part);
        }

        foreach (var element in document.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "TargetFramework":
                case "TargetFrameworks":
                    Add(element.Value);
                    break;

                case "TargetFrameworkVersion":
                    Add(NormalizeFrameworkVersion(element.Value));
                    break;
            }
        }

        return frameworks;
    }

    /// <summary>"v4.8" → "net48", "v4.7.2" → "net472", "v3.5" → "net35" —
    /// the classic MSBuild spelling mapped onto the dotnet-CLI TFM form.
    /// Anything not shaped like a .NET Framework version is returned as-is.</summary>
    public static string NormalizeFrameworkVersion(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Skip(1).All(c => char.IsDigit(c) || c == '.'))
            return "net" + trimmed[1..].Replace(".", string.Empty);

        return trimmed;
    }
}
