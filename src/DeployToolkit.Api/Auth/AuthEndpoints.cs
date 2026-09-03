using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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

        group.MapGet("/me", GetMeAsync)
            .RequireAuthorization()
            .WithName("Me")
            .WithSummary("Echo back the identity carried by the presented Bearer token.")
            .Produces<MeResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>
    /// The phase-1 endpoint. Flow: find the (active) user case-insensitively
    /// → constant-time PBKDF2 verification → stamp LastLoginUtc → issue the
    /// JWT. Unknown user, wrong password and disabled account all end in
    /// HTTP 401; the message never says WHICH factor failed (no user
    /// enumeration), and the password never appears in logs or responses.
    /// </summary>
    private static async Task<IResult> AuthenticateAsync(
        AuthenticateRequest? request,
        RegistryDbContext db,
        IPasswordHasher hasher,
        JwtTokenService tokens,
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

        // --- credential lookup + verification ---
        var user = await db.ApiUsers
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
        // ToLower() on both sides translates to SQL LOWER on SQL Server and
        // SQLite alike — same case-insensitive semantics the registry uses
        // for client names (see EfCoreRegistryStore.FindClientByNameAsync).

        if (user is null)
        {
            // Burn the same PBKDF2 work a real lookup+verify would: unknown
            // usernames become timing-indistinguishable from wrong passwords.
            hasher.Verify(password, "<malformed>");
            logger.LogWarning(
                "Authentication failed for unknown username '{Username}' from {RemoteIp}.",
                username, http.Connection.RemoteIpAddress);
            return InvalidCredentials();
        }

        if (!user.IsActive)
        {
            logger.LogWarning(
                "Authentication attempt for DISABLED API user '{Username}' from {RemoteIp}.",
                username, http.Connection.RemoteIpAddress);
            return Results.Json(
                new ErrorResponse("Account is disabled. Contact the registry administrator."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!hasher.Verify(password, user.PasswordHash))
        {
            logger.LogWarning(
                "Authentication failed (wrong password) for API user '{Username}' from {RemoteIp}.",
                username, http.Connection.RemoteIpAddress);
            return InvalidCredentials();
        }

        // --- success: stamp last login, issue the token ---
        var issuedAtUtc = DateTimeOffset.UtcNow;
        user.LastLoginUtc = issuedAtUtc;
        await db.SaveChangesAsync();

        var token = tokens.CreateToken(user, issuedAtUtc);
        logger.LogInformation(
            "API user '{Username}' authenticated from {RemoteIp}; token expires {ExpiresAtUtc:O}.",
            username, http.Connection.RemoteIpAddress, token.ExpiresAtUtc);

        return Results.Ok(new AuthenticateResponse(
            Status: "ok",
            Message: "Authentication succeeded.",
            Username: user.Username,
            DisplayName: user.DisplayName,
            TokenType: token.TokenType,
            AccessToken: token.Token,
            ExpiresAtUtc: token.ExpiresAtUtc));
    }

    private static IResult InvalidCredentials() => Results.Json(
        new ErrorResponse("Invalid username or password."),
        statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>Protected sample endpoint (JWT required) — the proof that
    /// the credential model actually secures the API, and the shape later
    /// phases reuse for /api/deploy etc.</summary>
    private static IResult GetMeAsync(
        ClaimsPrincipal principal, JwtTokenService tokens, HttpContext http)
    {
        var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var username = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.UniqueName);
        var displayName = principal.FindFirstValue(JwtTokenService.DisplayNameClaimType);

        // Signature and lifetime were already validated by the bearer
        // handler before we get here; re-reading the header only pulls out
        // the lifetime metadata for the echo.
        var header = http.Request.Headers.Authorization.ToString();
        var bearerToken = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
        tokens.TryReadLifetime(bearerToken, out var issuedAtUtc, out var expiresAtUtc);

        return Results.Ok(new MeResponse(
            UserId: userId ?? string.Empty,
            Username: username ?? string.Empty,
            DisplayName: displayName,
            IssuedAtUtc: issuedAtUtc,
            ExpiresAtUtc: expiresAtUtc));
    }
}
