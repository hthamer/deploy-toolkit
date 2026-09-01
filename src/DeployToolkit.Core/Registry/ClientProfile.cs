using System.Text.Json;
using System.Text.Json.Serialization;
using DeployToolkit.Core.Publishing;

namespace DeployToolkit.Core.Registry;

/// <summary>
/// Who owns/manages a piece of a client's infrastructure. Used for the
/// client-profile question "Infrastructure is managed by whom?".
/// Deliberately a two-value enum (matching how the business talks about it)
/// rather than free text, so the registry stays queryable; the hosting
/// account question is free text instead (see <see cref="Client.HostingAccountManagedBy"/>).
/// </summary>
public enum ManagedBy
{
    Boxon,
    Client
}

/// <summary>
/// The .NET publish "shape" of a client's deployments, stored per client as
/// the default starting point for every new component/package built for
/// them (plan §5: the Packager runs dotnet publish using the framework /
/// self-contained settings). Kept intentionally small — the component still
/// carries the authoritative TargetFramework/IsSelfContained per target
/// (§6); this is the client-level default.
/// </summary>
public sealed class PublishConfiguration
{
    /// <summary>Framework-dependent vs self-contained publish.</summary>
    public PublishDeploymentType DeploymentType { get; set; } = PublishDeploymentType.FrameworkDependent;

    /// <summary>
    /// Target runtime identifier passed as <c>-r</c> (e.g. "win-x64",
    /// "win-x86"), or null for the project's portable default.
    /// </summary>
    public string? TargetRuntime { get; set; }

    /// <summary>
    /// Extra verbatim arguments appended to <c>dotnet publish</c> after the
    /// standard ones (e.g. <c>-p:PublishTrimmed=false --nologo</c>). Passed
    /// through unmodified — no shell, no quoting magic (plan §1).
    /// </summary>
    public string? AdditionalPublishOptions { get; set; }

    /// <summary>
    /// Maps this client-level default onto a <see cref="PublishSettings"/>
    /// for <see cref="DotNetPublisher"/>. The runtime identifier (when set)
    /// is appended to the additional arguments as <c>-r &lt;rid&gt;</c>,
    /// because PublishSettings carries no dedicated RID field.
    /// </summary>
    public PublishSettings ToPublishSettings(string projectPath)
    {
        if (!string.IsNullOrWhiteSpace(TargetRuntime) && TargetRuntime!.Contains(' '))
            throw new ArgumentException("TargetRuntime must be a single runtime identifier without spaces (e.g. win-x64).");

        var additional = AdditionalPublishOptions;
        if (!string.IsNullOrWhiteSpace(TargetRuntime))
            additional = string.IsNullOrWhiteSpace(additional)
                ? $"-r {TargetRuntime}"
                : $"-r {TargetRuntime} {additional}";

        return new PublishSettings(
            ProjectPath: projectPath,
            TargetFramework: null, // per-component decision, not a client default
            SelfContained: DeploymentType == PublishDeploymentType.SelfContained,
            Configuration: "Release",
            AdditionalArguments: additional);
    }
}

public enum PublishDeploymentType
{
    FrameworkDependent,
    SelfContained
}

/// <summary>
/// Serializes <see cref="PublishConfiguration"/> to/from the JSON stored in
/// <see cref="Client.PublishConfigurationJson"/>. Same pattern as
/// ManifestJson/ManifestSerializer: one serializer, used by every store and
/// the UI, so the bytes in the registry are always canonical. Enums are
/// written as readable strings so the column can be audited with plain
/// SELECTs.
/// </summary>
public static class PublishConfigurationSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string? Serialize(PublishConfiguration? configuration)
        => configuration is null ? null : JsonSerializer.Serialize(configuration, Options);

    public static PublishConfiguration? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PublishConfiguration>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Stored PublishConfigurationJson is not valid JSON: {ex.Message}", ex);
        }
    }
}
