using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DeployToolkit.Core.EfCore;
using Microsoft.IdentityModel.Tokens;

namespace DeployToolkit.Api.Auth;

/// <summary>
/// Issues the signed JWT access tokens returned by
/// <c>POST /api/auth/authenticate</c> and validated by the JWT Bearer
/// handler on endpoints marked <c>RequireAuthorization()</c>.
///
/// Design notes:
///  - HS256 (symmetric) with a ≥ 256-bit signing key from configuration —
///    the API is the only issuer and the only validator, so there is no
///    key-distribution problem to solve in phase 1.
///  - Stateless on purpose: no token table, no server-side session. The
///    "logout" story for phase 1 is token expiry (AccessTokenMinutes) —
///    revocation would add store round-trips the Deployer's 30s-timeout
///    login flow does not need. Re-hitting /authenticate is idempotent.
///  - Claims kept minimal: sub (stable UserId), name (username),
///    display_name (optional). Authorization decisions in later phases can
///    extend this (roles per client/component) without breaking the
///    existing clients — they only look at the HTTP status code.
/// </summary>
public sealed class JwtTokenService
{
    /// <summary>Security requirements: signing key ≥ 32 chars (= 256 bits
    /// when the text is ASCII/UTF-8 without multi-byte sequences).</summary>
    public const int MinimumSigningKeyLength = 32;

    public const string DisplayNameClaimType = "display_name";

    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParameters;

    public string Issuer { get; }
    public string Audience { get; }
    public TimeSpan AccessTokenLifetime { get; }

    public JwtTokenService(IConfiguration configuration)
    {
        var section = configuration.GetSection("Auth:Jwt");

        Issuer = section["Issuer"] ?? "DeployToolkit.RegistryApi";
        Audience = section["Audience"] ?? "DeployToolkit.Clients";
        AccessTokenLifetime = TimeSpan.FromMinutes(
            section.GetValue("AccessTokenMinutes", 480));

        var signingKey = section["SigningKey"]
            ?? throw new InvalidOperationException(
                "Auth:Jwt:SigningKey is not configured. Add it to appsettings.json " +
                "(or the Auth__Jwt__SigningKey environment variable) — at least " +
                $"{MinimumSigningKeyLength} characters. Never commit a production key.");
        if (signingKey.Length < MinimumSigningKeyLength)
            throw new InvalidOperationException(
                $"Auth:Jwt:SigningKey is too short ({signingKey.Length} chars, minimum " +
                $"{MinimumSigningKeyLength} = 256 bits for HS256).");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        _signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            // Desktop clients run on machines whose clocks drift — accept a
            // small skew instead of failing logins for 8h-tokens that are
            // off by two minutes.
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        // Fail fast at startup with a clear error if the key/issuer/audience
        // combination can never validate a token (instead of 500s at login).
        _ = _handler.ValidateToken(
            CreateToken(BuildProbeUser(), DateTimeOffset.UtcNow).Token,
            _validationParameters,
            out _);
    }

    /// <summary>Creates the signed access token for a successfully
    /// authenticated user.</summary>
    public IssuedToken CreateToken(ApiUser user, DateTimeOffset issuedAtUtc)
    {
        var expiresAtUtc = issuedAtUtc.Add(AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.Name, user.Username),
        };
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            claims.Add(new Claim(DisplayNameClaimType, user.DisplayName));

        var jwt = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: issuedAtUtc.UtcDateTime,
            expires: expiresAtUtc.UtcDateTime,
            signingCredentials: _signingCredentials);

        return new IssuedToken(
            Token: _handler.WriteToken(jwt),
            TokenType: "Bearer",
            ExpiresAtUtc: expiresAtUtc);
    }

    private static ApiUser BuildProbeUser() => new()
    {
        UserId = Guid.Empty.ToString("N"),
        Username = "startup-selftest",
        PasswordHash = string.Empty,
        CreatedUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Reads the lifetime metadata (iat / exp) out of an already-presented
    /// JWT WITHOUT validating it — signature and lifetime are checked by the
    /// bearer handler before the endpoint runs; this is for echo/reporting
    /// only. Returns false when the string is not a parseable JWT.
    /// </summary>
    public bool TryReadLifetime(string? token, out DateTimeOffset issuedAtUtc, out DateTimeOffset expiresAtUtc)
    {
        issuedAtUtc = DateTimeOffset.MinValue;
        expiresAtUtc = DateTimeOffset.MinValue;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var jwt = _handler.ReadJwtToken(token);
            issuedAtUtc = jwt.ValidFrom == DateTime.MinValue
                ? DateTimeOffset.MinValue
                : new DateTimeOffset(jwt.ValidFrom, TimeSpan.Zero);
            expiresAtUtc = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The token plus its metadata, as echoed in the
    /// authenticate response body.</summary>
    public sealed record IssuedToken(
        string Token,
        string TokenType,
        DateTimeOffset ExpiresAtUtc);
}
