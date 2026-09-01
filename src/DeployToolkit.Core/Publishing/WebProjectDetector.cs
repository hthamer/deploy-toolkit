using System.Xml.Linq;

namespace DeployToolkit.Core.Publishing;

/// <summary>
/// Detects whether a <c>.csproj</c> is a classic .NET Framework <b>Web
/// Application</b> project — the non-SDK flavor that imports
/// <c>Microsoft.WebApplication.targets</c>.
///
/// Those projects <b>cannot</b> be published with <c>dotnet publish</c>:
///  • the .NET SDK's MSBuild does not ship the Visual Studio Web
///    Applications targets, so the classic <c>&lt;Import&gt;</c> fails with
///    <c>error MSB4019: The imported project "...Microsoft.WebApplication.targets"
///    was not found</c>;
///  • the restore model is <c>packages.config</c>, which is invisible to
///    <c>dotnet restore</c>, so <c>dotnet publish</c> prints <c>Nothing to do.
///    None of the projects specified contain packages to restore.</c> and
///    then dies on the missing targets import.
///
/// They must instead be published with the full Visual Studio MSBuild using
/// the Web Publishing Pipeline (WPP) targets — see
/// <see cref="MsBuildPublisher"/>. This detector is what routes the
/// Packager's publish step to <see cref="MsBuildPublisher"/> instead of
/// <see cref="DotNetPublisher"/>.
///
/// Signals (any one is enough — both are checked because some hand-edited
/// projects drop the explicit <c>Import</c> while keeping the GUID):
///  <list type="bullet">
///   <item>an <c>&lt;Import&gt;</c> whose <c>Project</c> attribute references
///    <c>Microsoft.WebApplication.targets</c> (the exact import that fails
///    with MSB4019 when the wrong MSBuild hosts the build);</item>
///   <item>a <c>&lt;ProjectTypeGuids&gt;</c> containing the C# Web Application
///    project-type GUID <see cref="WebApplicationProjectTypeGuid"/>.</item>
///  </list>
///
/// Modern SDK-style web projects (<c>Microsoft.NET.Sdk.Web</c>) and the
/// classic Web <i>Site</i> model are intentionally <b>not</b> flagged here —
/// SDK web projects publish fine with <c>dotnet publish</c> (they get the
/// web targets from the SDK, never via a hand-written <c>Import</c>), and
/// Web Sites are out of scope for this tool.
/// </summary>
public static class WebProjectDetector
{
    /// <summary>
    /// The C# Web Application project-type GUID, as it appears in a classic
    /// <c>&lt;ProjectTypeGuids&gt;</c> element (ASP.NET Web Applications,
    /// ASP.NET MVC 4/5, Web Forms on .NET Framework). The companion C#
    /// language GUID is <c>{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</c>; both
    /// appear together, separated by a semicolon.
    /// </summary>
    public const string WebApplicationProjectTypeGuid = "{349C5B8E-1FC2-4E65-AD91-9E64525F2DD3}";

    /// <summary>
    /// True when <paramref name="csprojPath"/> is a classic .NET Framework
    /// Web Application project — the kind that needs the full Visual Studio
    /// MSBuild (not <c>dotnet publish</c>) and the Visual Studio Web
    /// Applications targets. Returns <c>false</c> for SDK-style web projects,
    /// missing/unreadable files, and non-web projects (so the caller can
    /// fall through to the standard <c>dotnet publish</c> path and surface
    /// whatever error that produces).
    /// </summary>
    public static bool IsNetFrameworkWebApp(string? csprojPath)
    {
        if (string.IsNullOrWhiteSpace(csprojPath) || !File.Exists(csprojPath))
            return false;

        XDocument document;
        try
        {
            document = XDocument.Load(csprojPath, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return false; // unreadable — let the dotnet path try and report the real error
        }

        var root = document.Root;
        if (root is null)
            return false;

        // SDK-style projects (any Sdk attribute) get their web targets from
        // the SDK, never from a hand-written <Import> of
        // Microsoft.WebApplication.targets — they publish fine with
        // `dotnet publish`, so they are NOT classic Web Applications.
        if (root.Attribute("Sdk") is not null)
            return false;

        // Signal 1: an <Import> referencing Microsoft.WebApplication.targets.
        foreach (var import in document.Descendants())
        {
            if (!string.Equals(import.Name.LocalName, "Import", StringComparison.OrdinalIgnoreCase))
                continue;

            var projectAttr = import.Attribute("Project");
            if (projectAttr is null)
                continue;

            var projectValue = projectAttr.Value ?? string.Empty;
            if (projectValue.Contains("Microsoft.WebApplication.targets", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Signal 2: <ProjectTypeGuids> containing the Web Application GUID.
        foreach (var element in document.Descendants())
        {
            if (!string.Equals(element.Name.LocalName, "ProjectTypeGuids", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = element.Value ?? string.Empty;
            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(part.Trim(), WebApplicationProjectTypeGuid, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
