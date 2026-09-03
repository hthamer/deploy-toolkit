using System.ComponentModel.DataAnnotations.Schema;

namespace DeployToolkit.Core.EfCore;

/// <summary>
/// An API credential row for the central registry REST API
/// (<c>DeployToolkit.Api</c>). Lives in the SAME registry database the
/// Packager/Deployer already use (plan §2.2) so the API is just another
/// consumer of the single source of truth — no second database to provision
/// or back up.
///
/// Deliberately an infrastructure concern of the API, NOT a deployment-domain
/// concept — which is why this POCO lives in <c>DeployToolkit.Core.EfCore</c>
/// (the only project allowed to carry EF dependencies) instead of
/// <c>DeployToolkit.Core</c>: the domain POCOs stay auth-agnostic, the same
/// way they stay provider-agnostic.
///
/// Password storage policy (plan §12 spirit — never store recoverable
/// secrets): <see cref="PasswordHash"/> holds a salted PBKDF2-SHA256 hash in
/// the versioned string format produced/verified by the API's
/// <c>Pbkdf2PasswordHasher</c>:
///   <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 subkey&gt;</c>
/// The plaintext password is never persisted, logged, or returned.
/// </summary>
public sealed class ApiUser
{
    /// <summary>Stable row id — Guid "N" format (32 chars), matching the key
    /// convention of <see cref="RegistryDbContext"/>'s other entities.</summary>
    public required string UserId { get; init; }

    /// <summary>Login name, case-insensitively unique (unique index in the
    /// model). Trimmed on create/update.</summary>
    public required string Username { get; set; }

    /// <summary>Human-friendly display name (optional — shown in API
    /// responses and audit output).</summary>
    public string? DisplayName { get; set; }

    /// <summary>Versioned PBKDF2 password hash (see type doc). Never null —
    /// a row without a valid hash can never authenticate.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Soft-disable switch: <c>false</c> blocks authentication
    /// without deleting the row (keeps the audit trail intact).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When the credential row was created (UTC).</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Last successful authentication (UTC); null until first
    /// successful login. Updated by the API on every successful login.</summary>
    public DateTimeOffset? LastLoginUtc { get; set; }

    /// <summary>Convenience flag for API responses. Not mapped.</summary>
    [NotMapped]
    public bool HasNeverLoggedIn => LastLoginUtc is null;
}
