using DeployToolkit.Api.Infrastructure;
using DeployToolkit.Core.EfCore;
using Microsoft.EntityFrameworkCore;

namespace DeployToolkit.Api.Auth;

/// <summary>
/// Phase 1 REST surface: credential validation against the registry
/// database (the same one the Packager app uses).
///
/// The route and payload contract are pinned by the WinForms clients —
/// <c>RegistryApiClient</c> (DeployToolkit.AppKit) POSTs camelCase
/// <c>{"username": …, "password": …}</c> to <c>{baseUrl}/api/auth/authenticate</c>,
/// treats HTTP 2xx as "Login OK" and surfaces any other status code plus
/// the raw response body to the user. Keep the contract stable; add NEW
/// endpoints instead of changing these.
///
/// Deliberately NO bearer/JWT flow: every request authenticates by
/// presenting the username and password that match the credentials saved in
/// the registry database. The background
/// <see cref="PasswordRotationService"/> replaces those passwords on a
/// schedule, and the response's <c>passwordChangedUtc</c> /
/// <c>passwordRotatesAtUtc</c> fields tell callers when that happened /
/// will happen next (from <c>ApiUsers.PasswordChangedUtc</c> and the
/// <c>Auth.Rotation.NextRunUtc</c> row of the ApiSettings table).
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapRegistryAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
            .WithTags("Authentication")
            // Brute-force backstop: fixed window per remote IP (see Program.cs
            // for the limits). The Deployer's login dialog is the only caller
            // in normal operation — a handful of requests per minute.
            .RequireRateLimiting("auth");

        group.MapPost("/authenticate", AuthenticateAsync)
            .WithName("Authenticate")
            .WithSummary("Validate a username/password pair against the credentials saved in the registry database.")
            .Produces<AuthenticateResponse>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);

        return app;
    }

    /// <summary>
    /// The phase-1 endpoint. Flow: find the (active) user case-insensitively
    /// → constant-time PBKDF2 verification → stamp LastLoginUtc → 200 with
    /// the rotation timestamps. Unknown user, wrong password and disabled
    /// account all end in HTTP 401; the message never says WHICH factor
    /// failed (no user enumeration), and the password never appears in
    /// logs or responses.
    /// </summary>
    private static async Task<IResult> AuthenticateAsync(
        AuthenticateRequest? request,
        RegistryDbContext db,
        IPasswordHasher hasher,
        ApiSettingsStore settingsStore,
        ILogger<Program> logger,
        HttpContext http)
    {
        // --- 400: malformed request (the WinForms clients always send both
        // fields; this guards curl / future callers) ---
        var username = request?.Username?.Trim();
        var password = request?.Password;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return Results.Json(
                new ErrorResponse("Both 'username' and 'password' are required."),
                statusCode: StatusCodes.Status400BadRequest);

        // --- credential lookup + verification (shared with /api/deploy;
        //     see CredentialValidator for the security semantics) ---
        var outcome = await CredentialValidator.ValidateAsync(
            db, hasher, username, password!, http.RequestAborted);

        if (outcome.Result == CredentialValidationResult.UnknownOrWrongPassword)
        {
            logger.LogWarning(
                "Authentication failed for '{Username}' from {RemoteIp} " +
                "(unknown username or wrong password).",
                username, http.Connection.RemoteIpAddress);
            return InvalidCredentials();
        }

        if (outcome.Result == CredentialValidationResult.Disabled)
        {
            logger.LogWarning(
                "Authentication attempt for DISABLED API user '{Username}' from {RemoteIp}.",
                username, http.Connection.RemoteIpAddress);
            return Results.Json(
                new ErrorResponse("Account is disabled. Contact the registry administrator."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = outcome.User!;

        // --- success: stamp last login, answer with the rotation metadata ---
        user.LastLoginUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation(
            "API user '{Username}' authenticated from {RemoteIp}.",
            username, http.Connection.RemoteIpAddress);

        return Results.Ok(new AuthenticateResponse(
            Status: "ok",
            Message: "Authentication succeeded.",
            Username: user.Username,
            DisplayName: user.DisplayName,
            PasswordChangedUtc: user.PasswordChangedUtc,
            PasswordRotatesAtUtc: await settingsStore.GetDateTimeUtcAsync(
                ApiSettingKeys.RotationNextRunUtc, http.RequestAborted)));
    }

    private static IResult InvalidCredentials() => Results.Json(
        new ErrorResponse("Invalid username or password."),
        statusCode: StatusCodes.Status401Unauthorized);
}
