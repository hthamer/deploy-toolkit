using System.Text.Json.Serialization;

namespace DeployToolkit.Api.Auth;

/// <summary>
/// Request body of <c>POST /api/auth/authenticate</c>. Bound from camelCase
/// JSON (<c>{"username": …, "password": …}</c>) — exactly what the WinForms
/// clients POST via <c>RegistryApiClient.AuthenticateAsync</c>
/// (DeployToolkit.AppKit), which sends an anonymous camelCase object.
/// </summary>
public sealed record AuthenticateRequest(
    string? Username,
    string? Password);

/// <summary>
/// Successful response of <c>POST /api/auth/authenticate</c> (HTTP 200).
/// Serialized camelCase. The <see cref="AccessToken"/> is a signed JWT
/// (HS256) that must be sent as <c>Authorization: Bearer …</c> on endpoints
/// that require an authenticated caller (phase 2+: <c>/api/deploy</c>).
/// The Deployer's login dialog shows this body verbatim (truncated) as its
/// green "Login OK" detail text — keep it compact and human-readable.
/// </summary>
public sealed record AuthenticateResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("tokenType")] string TokenType,
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc);

/// <summary>Failure body (400/401/429): a single machine- and human-readable
/// error string. Never reveals whether the username or the password was
/// wrong (no user enumeration), and never echoes the submitted password.</summary>
public sealed record ErrorResponse(
    [property: JsonPropertyName("error")] string Error);

/// <summary>Response of <c>GET /api/auth/me</c> (requires a valid Bearer
/// token) — lets a caller (and the phase-2 Deployer) confirm the token it
/// holds is alive and whose it is.</summary>
public sealed record MeResponse(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("issuedAtUtc")] DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc);
