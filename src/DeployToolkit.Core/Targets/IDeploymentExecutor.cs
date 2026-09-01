using DeployToolkit.Core.Manifest;

namespace DeployToolkit.Core.Targets;

public enum TargetType
{
    IisLocal,
    AzureAppService,
    Plesk
}

public sealed record DeploymentResult(bool Success, string Message, bool HealthCheckPassed);

/// <summary>
/// One deployment target's execution logic. The Deployer app picks an
/// implementation based on the component's TargetType and calls it with an
/// already-verified package (see PackageReader.VerifyIntegrity).
///
/// Implementations live in separate projects added once NuGet access is
/// available on the dev machine, since each needs different external
/// packages:
///   - IisLocalExecutor      -> Microsoft.Web.Administration
///   - AzureAppServiceExecutor -> Azure.Identity / plain HttpClient (Kudu + ARM)
///   - PleskExecutor         -> FluentFTP or SSH.NET
///
/// This project (DeployToolkit.Core) intentionally has zero NuGet
/// dependencies, so it stays buildable/testable anywhere — the interface
/// lives here, the implementations don't.
/// </summary>
public interface IDeploymentExecutor
{
    TargetType TargetType { get; }

    Task<DeploymentResult> DeployAsync(
        ComponentManifest manifest,
        string extractedFilesRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
