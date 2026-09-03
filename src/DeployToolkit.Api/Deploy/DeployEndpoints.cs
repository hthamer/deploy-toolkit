using System.Text;
using DeployToolkit.Api.Auth;
using DeployToolkit.Core.EfCore;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace DeployToolkit.Api.Deploy;

/// <summary>
/// Phase-2 REST surface: the Deployer reports a finished deployment and the
/// API flags the package in the SAME registry database the Packager uses.
///
///   POST /api/deploy  (HTTP Basic credentials + camelCase JSON report)
///
/// The route and payload contract are pinned by the WinForms clients —
/// <c>RegistryApiClient.ReportDeploymentAsync</c> (DeployToolkit.AppKit)
/// POSTs the camelCase <c>ApiDeploymentReport</c> shape to
/// <c>{baseUrl}/api/deploy</c> with the session's registry credentials in
/// the HTTP Basic header. Keep the contract stable.
///
/// Semantics deliberately mirror the local "Mark Deployed" path
/// (<see cref="DeploymentOrchestrator"/> →
/// <c>EfCoreRegistryStore.MarkDeployedAsync</c> + run recording):
///  * Result "Success"     → package.Status = Deployed, DeployedBy,
///                           DeployedUtc stamped (exactly what the
///                           orchestrator does after a green health check);
///  * Failed / RolledBack  → package untouched (it stays in its current
///                           state, e.g. Created — a rolled-back run never
///                           shipped), the outcome is still audited;
///  * every report writes a <c>DeploymentRunRecord</c> with the same fields
///    <c>RecordRunStartAsync</c>/<c>RecordRunCompleteAsync</c> would write.
///
/// Security: token-free per-request credentials over HTTP Basic, validated
/// against <c>ApiUsers</c> by the shared <see cref="CredentialValidator"/>
/// (PBKDF2, constant-time, timing-equalized, no user enumeration). The
/// endpoint sits behind the same per-IP rate limiter as the login endpoint
/// because each report burns a PBKDF2 verify.
/// </summary>
public static class DeployEndpoints
{
    public static IEndpointRouteBuilder MapDeployEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/deploy")
            .WithTags("Deployment")
            // Each report performs a PBKDF2 password verify — same
            // brute-force economics as the login endpoint, same limiter.
            .RequireRateLimiting("auth");

        group.MapPost("/", ReportDeployAsync)
            .WithName("ReportDeployment")
            .WithSummary("Report a finished deployment; a successful run flags the package as Deployed in the registry.")
            .Produces<DeployReportResponse>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> ReportDeployAsync(
        DeployReportRequest? request,
        RegistryDbContext db,
        IPasswordHasher hasher,
        ILogger<Program> logger,
        HttpContext http)
    {
        // --- 401: HTTP Basic credentials (token-free, per-request) --------
        var (username, password, authError) = ParseBasicCredentials(http);
        if (authError is not null)
        {
            logger.LogWarning(
                "Deploy report rejected: {Reason} (from {RemoteIp}).",
                authError, http.Connection.RemoteIpAddress);
            return Unauthorized(http, authError, includeChallenge: true);
        }

        var outcome = await CredentialValidator.ValidateAsync(
            db, hasher, username, password, http.RequestAborted);
        if (outcome.Result == CredentialValidationResult.UnknownOrWrongPassword)
        {
            logger.LogWarning(
                "Deploy report rejected: invalid credentials for '{Username}' (from {RemoteIp}).",
                username, http.Connection.RemoteIpAddress);
            return Unauthorized(http, "Invalid username or password.");
        }

        if (outcome.Result == CredentialValidationResult.Disabled)
        {
            logger.LogWarning(
                "Deploy report rejected: API user '{Username}' is disabled (from {RemoteIp}).",
                username, http.Connection.RemoteIpAddress);
            return Unauthorized(http, "Account is disabled. Contact the registry administrator.");
        }

        var user = outcome.User!;

        // --- 400: report validation ---------------------------------------
        var packageId = request?.PackageId?.Trim();
        if (string.IsNullOrEmpty(packageId))
            return Results.Json(
                new ErrorResponse("A deploy report with a 'packageId' field is required."),
                statusCode: StatusCodes.Status400BadRequest);

        var canonicalResult = (request!.Result?.Trim()).ToLowerInvariant() switch
        {
            "success" => "Success",
            "failed" => "Failed",
            "rolledback" => "RolledBack",
            _ => null,
        };
        if (canonicalResult is null)
            return Results.Json(
                new ErrorResponse(
                    "'result' must be one of: Success, Failed, RolledBack."),
                statusCode: StatusCodes.Status400BadRequest);

        // --- 404: the package must exist in the registry -------------------
        var package = await db.Packages
            .FirstOrDefaultAsync(p => p.PackageId == packageId, http.RequestAborted);
        if (package is null)
            return Results.Json(
                new ErrorResponse(
                    $"Package '{packageId}' was not found in the registry. " +
                    "Package the component first (the Packager creates the row)."),
                statusCode: StatusCodes.Status404NotFound);

        // --- audit trail: same run-record fields as the local path ---------
        var now = DateTimeOffset.UtcNow;
        var run = new DeploymentRunRecord
        {
            RunId = Guid.NewGuid().ToString("N"),
            PackageId = package.PackageId,
            StartedUtc = request.StartedUtc == default ? now : request.StartedUtc,
            CompletedUtc = request.CompletedUtc == default ? now : request.CompletedUtc,
            Result = canonicalResult,
            HealthCheckResult = request.HealthCheckPassed,
            // The live log file lives on the Deployer machine; the API only
            // records the outcome (same as LogPath=null runs elsewhere).
            LogPath = null,
        };
        db.DeploymentRuns.Add(run);

        // --- Mark Deployed (mirror of EfCoreRegistryStore.MarkDeployedAsync)
        DateTimeOffset? deployedUtc = null;
        if (canonicalResult == "Success")
        {
            package.Status = PackageStatus.Deployed;
            package.DeployedBy = string.IsNullOrWhiteSpace(request.DeployedBy)
                ? user.Username
                : request.DeployedBy.Trim();
            package.DeployedUtc = run.CompletedUtc;
            deployedUtc = run.CompletedUtc;
        }

        await db.SaveChangesAsync(http.RequestAborted);

        logger.LogInformation(
            "Deploy report accepted for package '{PackageId}': result {Result}, package now {Status}, " +
            "run {RunId} ({Client}/{Component} {Version}, target {TargetType}), reported by '{ReportedBy}', " +
            "authenticated as '{User}'.",
            package.PackageId, canonicalResult, package.Status, run.RunId,
            request.Client, request.Component, request.Version, request.TargetType,
            request.DeployedBy, user.Username);

        return Results.Ok(new DeployReportResponse(
            Status: "ok",
            Message: canonicalResult == "Success"
                ? "Deployment recorded; package marked as Deployed."
                : $"Deployment outcome '{canonicalResult}' recorded (package status unchanged).",
            PackageId: package.PackageId,
            PackageStatus: package.Status.ToString(),
            RunId: run.RunId,
            Result: canonicalResult,
            DeployedUtc: deployedUtc,
            AuthenticatedAs: user.Username));
    }

    /// <summary>Parses the HTTP Basic authorization header into
    /// (username, password). Splits on the FIRST colon — passwords may
    /// contain colons; usernames cannot.</summary>
    private static (string Username, string Password, string? Error) ParseBasicCredentials(
        HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return ("", "",
                "API credentials required — send HTTP Basic authentication " +
                "with the registry username and password.");
        }

        string decoded;
        try
        {
            var base64 = header["Basic ".Length..].Trim();
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return ("", "", "Malformed HTTP Basic authorization header (invalid base64).");
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
            return ("", "", "Malformed HTTP Basic credentials — expected 'username:password'.");

        var username = decoded[..separator].Trim();
        var password = decoded[(separator + 1)..];
        if (username.Length == 0 || password.Length == 0)
            return ("", "", "Both 'username' and 'password' are required in the Basic credentials.");

        return (username, password, null);
    }

    private static IResult Unauthorized(HttpContext http, string message, bool includeChallenge = false)
    {
        if (includeChallenge)
        {
            // Minimal-API result objects cannot carry custom headers; stamp
            // the challenge on the response before the 401 body is written.
            http.Response.Headers.WWWAuthenticate =
                "Basic realm=\"DeployToolkit Registry API\"";
        }
        return Results.Json(
            new ErrorResponse(message),
            statusCode: StatusCodes.Status401Unauthorized);
    }
}
