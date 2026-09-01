namespace DeployToolkit.Core.Publishing;

/// <summary>
/// Structured form of the ASP.NET precompilation options Visual Studio's
/// <b>Precompile Options</b> dialog exposes — the <c>[Configure…]</c>
/// button next to <c>Precompile during publishing</c> in the publish wizard
/// for .NET Framework Web Application projects. These map onto Web
/// Publishing Pipeline (WPP) MSBuild properties consumed by
/// <c>Microsoft.Web.Publishing.AspNetCompileMerge.targets</c>, which in turn
/// drive the underlying <c>aspnet_compiler.exe</c> flags:
///
/// <list type="table">
///  <listheader><term>Property</term><description>MSBuild property → aspnet_compiler flag</description></listheader>
///  <item><term><see cref="Updatable"/></term>
///    <description><c>/p:EnableUpdateable=true</c> → <c>-u</c> (the precompiled
///    site stays editable at runtime — markup is not baked into the
///    assembly).</description></item>
///  <item><term><see cref="UseFixedNames"/></term>
///    <description><c>/p:UseFixedNames=true</c> → <c>-fixednames</c> (one
///    assembly per page/control, named after the source file).</description></item>
///  <item><term><see cref="EmitDebugInfo"/></term>
///    <description><c>/p:DebugSymbols=true</c> → <c>-d</c> (emit PDBs into the
///    precompiled output).</description></item>
/// </list>
///
/// Defaults mirror Visual Studio's dialog defaults: updatable on, fixed
/// names off, debug info off (a release publish should not ship PDBs unless
/// the user explicitly asks).
/// </summary>
public sealed record WebPrecompileOptions(
    bool Updatable = true,
    bool UseFixedNames = false,
    bool EmitDebugInfo = false)
{
    /// <summary>
    /// The default options applied when the user ticks
    /// <c>Precompile during publishing</c> without opening Configure.
    /// Matches VS's out-of-the-box precompile settings (updatable on, fixed
    /// names off, debug info off).
    /// </summary>
    public static WebPrecompileOptions Default { get; } =
        new(Updatable: true, UseFixedNames: false, EmitDebugInfo: false);
}
