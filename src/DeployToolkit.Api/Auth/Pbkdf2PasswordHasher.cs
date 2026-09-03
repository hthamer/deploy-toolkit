using System.Security.Cryptography;
using System.Text;

namespace DeployToolkit.Api.Auth;

/// <summary>Hashes and verifies API-user passwords. The stored form is a
/// versioned PBKDF2 string (see <see cref="Pbkdf2PasswordHasher"/>), never
/// the plaintext.</summary>
public interface IPasswordHasher
{
    /// <summary>Produces the stored <c>PasswordHash</c> string for a new or
    /// changed password (fresh random salt on every call).</summary>
    string Hash(string password);

    /// <summary>Constant-time verification of a candidate password against
    /// a stored hash string. Returns <c>false</c> for malformed/legacy
    /// hashes — never throws.</summary>
    bool Verify(string password, string storedHash);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing (OWASP 2023 guidance: ≥ 210k
/// iterations for SHA-256, 128-bit random salt, 256-bit derived key).
///
/// Storage format — versioned, self-describing, auditable with plain
/// SELECTs (plan §12 spirit — no opaque black boxes):
///
///   <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 subkey&gt;</c>
///
/// The version prefix lets future changes (iterations bump, Argon2id, …)
/// verify old hashes and transparently rehash on the next successful login.
/// Verification always uses <see cref="CryptographicOperations.FixedTimeEquals"/>
/// so response timing does not leak how much of the hash matched.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    /// <summary>PBKDF2 iteration count for NEW hashes. Bumping this does not
    /// invalidate existing rows — the iterations are stored per hash.</summary>
    public const int Iterations = 210_000;

    private const int SaltSizeBytes = 16;
    private const int SubkeySizeBytes = 32;
    private const string FormatPrefix = "pbkdf2-sha256";
    private const char Separator = '$';

    /// <summary>
    /// Pre-computed hash of a throwaway password, verified against when the
    /// submitted username does not exist (or the stored hash is corrupt).
    /// Without it a missing username returns noticeably faster than a wrong
    /// password (no hashing work), letting an attacker enumerate valid
    /// usernames by timing the responses.
    /// </summary>
    private static readonly Lazy<string> TimingEqualizationHash = new(
        () => new Pbkdf2PasswordHasher().Hash("timing-equalization-placeholder"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            SubkeySizeBytes);

        return $"{FormatPrefix}{Separator}{Iterations}{Separator}" +
               $"{Convert.ToBase64String(salt)}{Separator}{Convert.ToBase64String(subkey)}";
    }

    public bool Verify(string password, string storedHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var parts = storedHash?.Split(Separator);
        var wellFormed = parts is { Length: 4 }
            && parts[0] == FormatPrefix
            && int.TryParse(parts[1], out var parsedIterations)
            && parsedIterations > 0;

        // Unknown/corrupt hash format: burn the same PBKDF2 work against the
        // dummy hash and fail — same timing as a real username + wrong
        // password, so format probing buys an attacker nothing.
        if (!wellFormed)
        {
            _ = TimingEqualizationHash.Value;
            VerifyHashString(password, TimingEqualizationHash.Value);
            return false;
        }

        try
        {
            return VerifyHashString(password, storedHash!, parts!);
        }
        catch (FormatException)
        {
            // Corrupt base64 — treat exactly like any other failed login.
            return false;
        }
    }

    private static bool VerifyHashString(string password, string storedHash, string[]? parts = null)
    {
        parts ??= storedHash.Split(Separator);
        var iterations = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
