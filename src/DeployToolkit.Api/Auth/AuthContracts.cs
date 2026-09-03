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
/// Serialized camelCase. Deliberately token-free: the API authenticates each
/// request by validating the submitted username/password pair against the
/// credentials stored in the registry database — no JWT, no bearer flow, no
/// client-side session state. The WinForms clients just check the HTTP
/// status code (200 = Login OK) and may show the body as detail text.
///
/// The two rotation fields let operators and callers see when the current
/// password was set and when the background rotation service will replace
/// it — null while rotation is disabled or before the first rotation.
/// </summary>
public sealed record AuthenticateResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("passwordChangedUtc")] DateTimeOffset? PasswordChangedUtc,
    [property: JsonPropertyName("passwordRotatesAtUtc")] DateTimeOffset? PasswordRotatesAtUtc);

/// <summary>Failure body (400/401/429): a single machine- and human-readable
/// error string. Never reveals whether the username or the password was
/// wrong (no user enumeration), and never echoes the submitted password.</summary>
public sealed record ErrorResponse(
    [property: JsonPropertyName("error")] string Error);
