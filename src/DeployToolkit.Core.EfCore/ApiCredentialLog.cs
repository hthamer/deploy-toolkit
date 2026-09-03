namespace DeployToolkit.Core.EfCore;

/// <summary>
/// One row per API credential change, registered in the SAME registry
/// database the Packager app uses. This table is THE distribution channel
/// for the rotating API credentials (user requirement: the username and
/// password are always registered in the DATABASE — never in
/// appsettings.json, never in a state file such as the former
/// <c>App_Data/current-api-password.json</c>, and there is deliberately no
/// token flow to distribute instead):
///
///   -- current working credential for a user:
///   SELECT TOP 1 Username, Password, CreatedUtc
///     FROM ApiCredentialLogs
///    WHERE Username = 'admin'
///    ORDER BY CreatedUtc DESC;
///
/// Storage policy — a deliberate, documented trade-off:
///  * <see cref="ApiUser.PasswordHash"/> (PBKDF2-SHA256, one-way) remains
///    the ONLY value used for verification. It is never reversible.
///  * <see cref="Password"/> stores the plaintext of the CURRENT password so
///    the operator/clients can retrieve it after every rotation. Anyone with
///    SELECT access to the registry database can log in until the next
///    rotation — restrict read access to this table accordingly (it is the
///    same trust boundary as the old state file, but centralized, auditable
///    and backed up with the registry).
/// </summary>
public sealed class ApiCredentialLog
{
    /// <summary>Row id — Guid "N" format (32 chars), matching the key
    /// convention of <see cref="RegistryDbContext"/>'s other entities.</summary>
    public required string Id { get; init; }

    /// <summary>The API user this credential belongs to (matches
    /// <see cref="ApiUser.Username"/>; no FK on purpose — credentials must
    /// survive user-row deletion for audit).</summary>
    public required string Username { get; set; }

    /// <summary>The plaintext password that became current at
    /// <see cref="CreatedUtc"/> (see the storage-policy note in the type
    /// doc). Rotation service and seeding write it; nothing else should.</summary>
    public required string Password { get; set; }

    /// <summary>Why the credential changed: <c>InitialSeed</c> (first-run
    /// bootstrap), <c>ScheduledRotation</c> (background rotation cycle) or
    /// <c>Manual</c> (hand-written row).</summary>
    public required string Reason { get; set; }

    /// <summary>When this credential became current (UTC). The latest row
    /// per username is the credential that authenticates right now.</summary>
    public required DateTimeOffset CreatedUtc { get; init; }
}
