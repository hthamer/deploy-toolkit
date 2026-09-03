using System.Text.Json.Serialization;

namespace DeployToolkit.Api.Deploy;

/// <summary>
/// Request body of <c>POST /api/deploy</c> — the deploy report the Deployer
/// sends after finishing a deployment. Binds from the camelCase JSON that
/// <c>RegistryApiClient.ReportDeploymentAsync</c> (DeployToolkit.AppKit)
/// serializes from its <c>ApiDeploymentReport</c> record — field-for-field
/// identical shape:
/// <c>{"packageId": …, "client": …, "component": …, "version": …,
/// "result": …, "healthCheckPassed": …, "message": …, "deployedBy": …,
/// "startedUtc": …, "completedUtc": …, "targetType": …}</c>.
/// Authentication rides in the HTTP Basic header, NOT in the body (token-free,
/// per-request credentials — same model as the authenticate endpoint).
/// </summary>
public sealed record DeployReportRequest(
    string? PackageId,
    string? Client,
    string? Component,
    string? Version,
    string? Result, // "Success" | "Failed" | "RolledBack"
    bool HealthCheckPassed,
    string? Message,
    string? DeployedBy,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string? TargetType);

/// <summary>Successful response of <c>POST /api/deploy</c> (HTTP 200). The
/// Deployer's log pane shows the message verbatim. <c>packageStatus</c>
/// reflects the registry state after the report — <c>Deployed</c> for a
/// successful run, unchanged (<c>Created</c>) for Failed/RolledBack.</summary>
public sealed record DeployReportResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("packageStatus")] string PackageStatus,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("deployedUtc")] DateTimeOffset? DeployedUtc,
    [property: JsonPropertyName("authenticatedAs")] string AuthenticatedAs);
