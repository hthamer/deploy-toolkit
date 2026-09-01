using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployToolkit.Core.Secrets;

/// <summary>
/// A single encrypted entry in a <see cref="SecretVault"/>.
/// </summary>
public sealed record SecretVaultEntry(
    string Name,
    string Ciphertext,
    string Purpose,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? UpdatedUtc);

/// <summary>
/// A JSON-file vault of encrypted secrets — connection strings, publish
/// credentials, API keys. Entries are addressed by name; the registry's
/// <c>DbConnectionRef</c> column stores only the <c>vault://{name}</c>
/// reference (plan §2.2: "pointer to encrypted secret, not the secret
/// itself"), so the shared registry DB never holds plaintext or even the
/// ciphertext — the vault file lives only on the operator's machine
/// (Packager side) or target machine (Deployer side).
///
/// The purpose of each protection operation is derived from the entry name,
/// so ciphertexts cannot be shuffled between entries (swapping the
/// DbConnection-encrypted blob into the Azure-publish slot fails).
/// Writes are atomic (temp + move) and the whole file is versioned.
/// </summary>
public sealed class SecretVault
{
    private const int FormatVersion = 1;

    private readonly string _filePath;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();

    public SecretVault(string filePath, ISecretProtector protector)
    {
        _filePath = filePath;
        _protector = protector;
    }

    /// <summary>The canonical registry reference for a vault entry — this
    /// is what goes into <c>DeploymentComponents.DbConnectionRef</c>.</summary>
    public static string SecretRefFor(string name) => $"vault://{name}";

    /// <summary>Parses a <c>vault://{name}</c> reference stored in the
    /// registry. Returns false for anything that isn't a vault reference
    /// (e.g. a Key Vault URI handled elsewhere).</summary>
    public static bool TryParseRef(string? reference, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith("vault://", StringComparison.OrdinalIgnoreCase))
            return false;
        name = reference["vault://".Length..];
        return name.Length > 0;
    }

    /// <summary>Adds or updates a secret. The value is encrypted with the
    /// entry name as the protection purpose.</summary>
    public void SetSecret(string name, string plaintext)
    {
        ValidateName(name);
        lock (_gate)
        {
            var file = LoadFile();
            var purpose = PurposeFor(name);
            var existing = file.Entries.TryGetValue(name, out var old) ? old : null;
            file.Entries[name] = new SecretVaultEntry(
                Name: name,
                Ciphertext: _protector.Protect(plaintext, purpose),
                Purpose: purpose,
                CreatedUtc: existing?.CreatedUtc ?? DateTimeOffset.UtcNow,
                UpdatedUtc: existing is null ? null : DateTimeOffset.UtcNow);
            SaveFile(file);
        }
    }

    /// <summary>Decrypts and returns a secret, or null when the name is
    /// unknown. Throws <see cref="CryptographicException"/> when the value
    /// can't be decrypted (wrong passphrase/key file, tampering, or a
    /// ciphertext copied in from another entry).</summary>
    public string? GetSecret(string name)
    {
        ValidateName(name);
        lock (_gate)
        {
            var file = LoadFile();
            if (!file.Entries.TryGetValue(name, out var entry)) return null;
            return _protector.Unprotect(entry.Ciphertext, entry.Purpose);
        }
    }

    public bool DeleteSecret(string name)
    {
        ValidateName(name);
        lock (_gate)
        {
            var file = LoadFile();
            if (!file.Entries.Remove(name)) return false;
            SaveFile(file);
            return true;
        }
    }

    public IReadOnlyList<SecretVaultEntry> ListSecrets()
    {
        lock (_gate)
        {
            return LoadFile().Entries.Values
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>The AAD/purpose bound into every entry's ciphertext —
    /// derived from the name so entries can't be swapped.</summary>
    private static string PurposeFor(string name) => $"deploytoolkit:vault:{FormatVersion}:{name}";

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Secret name must not be empty.");
    }

    private VaultFile LoadFile()
    {
        if (!File.Exists(_filePath))
            return new VaultFile(FormatVersion, new Dictionary<string, SecretVaultEntry>(StringComparer.Ordinal));

        var json = File.ReadAllText(_filePath);
        var file = JsonSerializer.Deserialize<VaultFile>(json);
        if (file is null || file.Version > FormatVersion)
            throw new InvalidDataException($"Vault file '{_filePath}' is unreadable or written by a newer version.");
        return file;
    }

    private void SaveFile(VaultFile file)
    {
        var tempPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private sealed record VaultFile(
        int Version,
        Dictionary<string, SecretVaultEntry> Entries);
}
