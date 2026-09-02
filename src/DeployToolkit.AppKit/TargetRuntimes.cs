namespace DeployToolkit.AppKit;

/// <summary>
/// The canonical list of .NET target runtime identifiers (RIDs) the Visual
/// Studio publish wizard exposes, plus the "portable" (no-RID) option.
/// Shared by the client-profile editor's Target runtime dropdown and the
/// Packager publish step's runtime box so the two surfaces never drift.
///
/// <b>"portable"</b> (listed first) maps to NO runtime identifier — a
/// framework-dependent publish without <c>-r</c> that runs on any OS matching
/// the framework's architecture. Stored as <c>null</c> in the registry (the
/// existing <c>PublishConfiguration.TargetRuntime</c> contract already treats
/// null as "no RID"). The display label differs by surface:
/// <list type="bullet">
///  <item>Client editor (strict dropdown): <c>"portable"</c> — the VS name.</item>
///  <item>Publish step (editable box): <c>"(project default)"</c> — the
///   existing label, kept for back-compat with the seeded value.</item>
/// </list>
/// Both labels map to the same null RID; <see cref="IsPortableLabel"/> accepts
/// either so the two surfaces interoperate.
///
/// The concrete RIDs (from the Visual Studio publish wizard, .NET Core apps):
/// <list type="bullet">
///  <item><c>win-x64</c> / <c>win-x86</c> — 64-bit / 32-bit Windows.</item>
///  <item><c>win-arm64</c> — Windows on ARM (e.g. SnapDragon devices).</item>
///  <item><c>linux-x64</c> — 64-bit Linux (Ubuntu, Debian, RHEL, …).</item>
///  <item><c>linux-arm64</c> — Linux on ARM64 (Raspberry Pi, AWS Graviton).</item>
///  <item><c>osx-x64</c> — Intel-based Apple macOS.</item>
///  <item><c>osx-arm64</c> — Apple Silicon (M1/M2/M3/M4) macOS.</item>
/// </list>
/// </summary>
public static class TargetRuntimes
{
    /// <summary>The "portable" / no-RID label used in the client editor's
    /// strict dropdown. Stored as null in the registry.</summary>
    public const string PortableLabel = "portable";

    /// <summary>The "portable" / no-RID label used in the Packager publish
    /// step's editable runtime box (kept for back-compat with the seeded
    /// value). Also stored as null.</summary>
    public const string PublishStepPortableLabel = "(project default)";

    /// <summary>The concrete RIDs, in the order Visual Studio lists them
    /// (Windows first, then Linux, then macOS). "portable" is prepended by
    /// the caller via <see cref="AllWithPortableFirst"/>.</summary>
    public static readonly IReadOnlyList<string> ConcreteRids = new[]
    {
        "win-x64",
        "win-x86",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
    };

    /// <summary>The full dropdown list for the client editor:
    /// <c>portable</c> first, then the concrete RIDs.</summary>
    public static IReadOnlyList<string> AllWithPortableFirst
        => _allWithPortableFirst;

    private static readonly string[] _allWithPortableFirst = Prepend(PortableLabel, ConcreteRids);

    /// <summary>True when <paramref name="label"/> is one of the "portable"
    /// / no-RID labels (either surface's spelling). Case-insensitive.</summary>
    public static bool IsPortableLabel(string? label) =>
        string.Equals(label, PortableLabel, StringComparison.OrdinalIgnoreCase)
        || string.Equals(label, PublishStepPortableLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>Converts a dropdown label into the value stored in the
    /// registry: "portable" / "(project default)" → null; everything else →
    /// the trimmed label (the RID verbatim).</summary>
    public static string? LabelToStoredValue(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;
        if (IsPortableLabel(label))
            return null;
        return label.Trim();
    }

    /// <summary>Converts a stored registry value (null or a RID) into the
    /// dropdown label for the client editor: null → "portable"; everything
    /// else → the stored value (so a custom RID stored out-of-band still
    /// shows up rather than being silently blanked).</summary>
    public static string StoredValueToClientLabel(string? stored) =>
        string.IsNullOrEmpty(stored) ? PortableLabel : stored!;

    private static T[] Prepend<T>(T first, IReadOnlyList<T> rest)
    {
        var arr = new T[rest.Count + 1];
        arr[0] = first;
        for (var i = 0; i < rest.Count; i++)
            arr[i + 1] = rest[i];
        return arr;
    }
}
