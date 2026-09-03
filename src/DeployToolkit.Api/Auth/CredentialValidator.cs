using DeployToolkit.Core.EfCore;
using Microsoft.EntityFrameworkCore;

namespace DeployToolkit.Api.Auth;

/// <summary>Outcome of <see cref="CredentialValidator.ValidateAsync"/> —
/// deliberately coarse: unknown user and wrong password are indistinguishable
/// to callers (no user enumeration).</summary>
public enum CredentialValidationResult
{
    /// <summary>Username exists, account is active, password verified.</summary>
    Ok,

    /// <summary>Unknown username OR wrong password (same response/timing).</summary>
    UnknownOrWrongPassword,

    /// <summary>Username exists but <c>IsActive</c> is false.</summary>
    Disabled,
}

/// <summary>
/// Shared credential validation for every credential-bearing endpoint
/// (<c>POST /api/auth/authenticate</c>, <c>POST /api/deploy</c>). One place
/// owns the security semantics:
///
///  * case-insensitive username lookup (SQL LOWER — same semantics the
///    registry uses for client names),
///  * constant-time PBKDF2 verification, and
///  * timing equalization for unknown usernames (the same PBKDF2 work a
///    real verify would burn, so username enumeration by timing buys
///    nothing).
/// </summary>
public static class CredentialValidator
{
    public static async Task<(CredentialValidationResult Result, ApiUser? User)> ValidateAsync(
        RegistryDbContext db,
        IPasswordHasher hasher,
        string username,
        string password,
        CancellationToken ct)
    {
        var user = await db.ApiUsers
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);

        if (user is null)
        {
            // Burn the same PBKDF2 work a real lookup+verify would.
            hasher.Verify(password, "<malformed>");
            return (CredentialValidationResult.UnknownOrWrongPassword, null);
        }

        if (!user.IsActive)
            return (CredentialValidationResult.Disabled, user);

        if (!hasher.Verify(password, user.PasswordHash))
            return (CredentialValidationResult.UnknownOrWrongPassword, null);

        return (CredentialValidationResult.Ok, user);
    }
}
