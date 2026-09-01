using System.Security.Cryptography;
using System.Text;

namespace DeployToolkit.Core.Secrets;

/// <summary>
/// Symmetric protector for registry-stored credentials (plan §2.2 "Secrets"
/// and §12 "Registry secrets"): connection strings and cloud credentials
/// are never stored in plain text — only an encrypted payload is.
/// Implementations: <see cref="AesGcmSecretProtector"/> (cross-platform,
/// in DeployToolkit.Core) and the DPAPI-backed protector in
/// DeployToolkit.Core.Windows (user-profile-bound, Windows only).
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> into an opaque,
    /// self-describing string. The <paramref name="purpose"/> binds the
    /// ciphertext to an intended use — decrypting with a different purpose
    /// must fail.</summary>
    string Protect(string plaintext, string purpose);

    /// <summary>Reverses <see cref="Protect"/>. Throws
    /// <see cref="System.Security.Cryptography.CryptographicException"/>
    /// when the payload was tampered with, the key is wrong, or the
    /// purpose doesn't match.</summary>
    string Unprotect(string ciphertext, string purpose);
}

/// <summary>
/// AES-256-GCM protector with two key sources:
///  - a passphrase (PBKDF2/SHA-256, 210k iterations by default, per-payload
///    salt stored in the header), for when the operator wants "one secret
///    I remember";
///  - a raw 256-bit key (optionally persisted as a key file with
///    user-only permissions), for automation-friendly setups.
///
/// Payload format (binary, then Base64):
///   magic "DTSEC1" | mode (1=passphrase, 2=key) | salt (16B, zeroed in key
///   mode) | iterations (4B BE) | nonce (12B) | ciphertext | tag (16B)
/// The purpose string is used as AES-GCM associated data (AAD), so a
/// ciphertext minted for one purpose can never be decrypted under another.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int KeySizeBytes = 32;      // AES-256
    private const int NonceSizeBytes = 12;    // GCM standard
    private const int TagSizeBytes = 16;      // 128-bit auth tag
    private const int SaltSizeBytes = 16;
    private const int IterationsSizeBytes = 4;
    private const int DefaultIterations = 210_000; // OWASP 2023 recommendation for PBKDF2-SHA256

    // Version marker: bump if the layout ever changes; Unprotect refuses
    // payloads from the future rather than decrypting garbage.
    private const string Magic = "DTSEC1";
    private const byte ModePassphrase = 1;
    private const byte ModeKey = 2;

    private readonly byte[] _key;
    private readonly byte[] _salt;
    private readonly uint _iterations;
    private readonly byte _mode;
    // Passphrase mode only: kept so payloads stay self-describing — each
    // payload records its own salt+iterations and the key is re-derived from
    // them at decrypt time. This is what lets a fresh process (or machine)
    // decrypt a vault using just the passphrase.
    private readonly string? _passphrase;

    private AesGcmSecretProtector(byte[] key, byte[] salt, uint iterations, byte mode, string? passphrase = null)
    {
        _key = key;
        _salt = salt;
        _iterations = iterations;
        _mode = mode;
        _passphrase = passphrase;
    }

    /// <summary>Derives a key from a passphrase. The salt is generated once
    /// per protector instance and embedded in every payload it produces —
    /// the same passphrase always decrypts the same vault.</summary>
    public static AesGcmSecretProtector CreateWithPassphrase(string passphrase, uint iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));
        if (iterations < 100_000)
            throw new ArgumentOutOfRangeException(nameof(iterations), "Refusing weak PBKDF2 iteration counts (< 100k).");

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = DeriveKey(passphrase, salt, iterations);
        return new AesGcmSecretProtector(key, salt, iterations, ModePassphrase, passphrase);
    }

    /// <summary>Creates an instance whose NEW payloads use the given
    /// salt (useful for deterministic tests). Existing payloads remain
    /// decryptable regardless of this salt — they carry their own.</summary>
    public static AesGcmSecretProtector FromPassphraseAndSalt(string passphrase, byte[] salt, uint iterations)
    {
        if (salt.Length != SaltSizeBytes)
            throw new ArgumentException($"Salt must be {SaltSizeBytes} bytes.", nameof(salt));
        var key = DeriveKey(passphrase, salt, iterations);
        return new AesGcmSecretProtector(key, (byte[])salt.Clone(), iterations, ModePassphrase, passphrase);
    }

    /// <summary>Uses a caller-supplied 256-bit key (e.g. from a key file or
    /// a hardware token).</summary>
    public static AesGcmSecretProtector CreateWithKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeBytes)
            throw new ArgumentException($"Key must be exactly {KeySizeBytes} bytes (AES-256).", nameof(key));
        return new AesGcmSecretProtector(key.ToArray(), new byte[SaltSizeBytes], 0, ModeKey);
    }

    /// <summary>Loads (or creates, if missing) a 32-byte key file with
    /// user-only read permissions on Unix. Keep this file safe: anyone who
    /// holds it can decrypt everything it protected.</summary>
    public static AesGcmSecretProtector CreateWithKeyFile(string keyFilePath)
    {
        if (File.Exists(keyFilePath))
        {
            var existing = File.ReadAllBytes(keyFilePath);
            if (existing.Length != KeySizeBytes)
                throw new InvalidOperationException(
                    $"Key file '{keyFilePath}' is {existing.Length} bytes; expected {KeySizeBytes}.");
            return CreateWithKey(existing);
        }

        var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFilePath))!);
        File.WriteAllBytes(keyFilePath, key);
        RestrictPermissions(keyFilePath);
        return CreateWithKey(key);
    }

    public string Protect(string plaintext, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var aad = Encoding.UTF8.GetBytes(purpose ?? string.Empty);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];
        using var aes = new AesGcm(_key, TagSizeBytes);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);

        var header = new List<byte>
        {
            // magic + mode
        };
        header.AddRange(Encoding.ASCII.GetBytes(Magic));
        header.Add(_mode);
        header.AddRange(_salt);
        header.AddRange(BitConverter.GetBytes(_iterations));
        header.AddRange(nonce);
        header.AddRange(tag);
        header.AddRange(ciphertext);

        return Convert.ToBase64String(header.ToArray());
    }

    public string Unprotect(string ciphertext, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);
        var data = Convert.FromBase64String(ciphertext);

        var headerBytes = Encoding.ASCII.GetByteCount(Magic);
        if (data.Length < headerBytes + 1 + SaltSizeBytes + IterationsSizeBytes + NonceSizeBytes + TagSizeBytes
            || Encoding.ASCII.GetString(data, 0, headerBytes) != Magic)
            throw new CryptographicException("Not a DeployToolkit secret payload (bad magic or truncated).");

        var offset = headerBytes;
        var mode = data[offset++];

        byte[] key;
        if (mode == ModePassphrase)
        {
            if (_passphrase is null)
                throw new CryptographicException(
                    "This protector instance was created from a raw key and cannot decrypt passphrase-mode payloads.");

            // The payload is self-describing: re-derive the key from the
            // salt+iterations recorded inside it, so any process holding the
            // passphrase can decrypt regardless of its own instance salt.
            var salt = new byte[SaltSizeBytes];
            Buffer.BlockCopy(data, offset, salt, 0, SaltSizeBytes);
            offset += SaltSizeBytes;
            var iterations = BitConverter.ToUInt32(data, offset);
            offset += IterationsSizeBytes;
            if (iterations < 100_000)
                throw new CryptographicException("Payload declares a suspiciously low PBKDF2 iteration count.");
            key = DeriveKey(_passphrase, salt, iterations);
        }
        else if (mode == ModeKey)
        {
            offset += SaltSizeBytes + IterationsSizeBytes; // zeroed placeholder fields
            key = _key;
        }
        else
        {
            throw new CryptographicException($"Unknown payload mode {mode}.");
        }

        var nonce = new byte[NonceSizeBytes];
        Buffer.BlockCopy(data, offset, nonce, 0, NonceSizeBytes);
        offset += NonceSizeBytes;

        var tag = new byte[TagSizeBytes];
        Buffer.BlockCopy(data, offset, tag, 0, TagSizeBytes);
        offset += TagSizeBytes;

        var ciphertextBytes = data.Length - offset;
        var plaintextBytes = new byte[ciphertextBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        // Throws CryptographicException on tag mismatch (tamper, wrong key,
        // or wrong purpose — purpose is bound as associated data).
        aes.Decrypt(nonce, data.AsSpan(offset, ciphertextBytes), tag, plaintextBytes,
            Encoding.UTF8.GetBytes(purpose ?? string.Empty));

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, uint iterations) =>
        // Rfc2898DeriveBytes with HashAlgorithmName.SHA256 = PBKDF2-HMAC-SHA256.
        new Rfc2898DeriveBytes(passphrase, salt, (int)iterations, HashAlgorithmName.SHA256).GetBytes(KeySizeBytes);

    private static void RestrictPermissions(string keyFilePath)
    {
        if (OperatingSystem.IsWindows()) return; // ACLs already restrict to the creating user by default
        try
        {
            File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
            // Older runtimes/platforms without unix mode support — the file
            // keeps default permissions; documented in the README.
        }
    }
}
