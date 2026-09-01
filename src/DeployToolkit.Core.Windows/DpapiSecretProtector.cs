using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DeployToolkit.Core.Secrets;

namespace DeployToolkit.Core.Windows;

/// <summary>
/// Windows DPAPI flavor of <see cref="ISecretProtector"/>: ciphertexts are
/// bound to the CURRENT WINDOWS USER of the machine that created them —
/// the strongest option for the operator's own workstation (nobody can
/// decrypt the vault file even with a copy of it) but NOT portable between
/// machines, which is exactly right for a local secret vault.
///
/// For cross-platform deployments (or a vault that must survive a machine
/// swap) use <see cref="AesGcmSecretProtector"/> from DeployToolkit.Core.
///
/// The purpose string becomes DPAPI entropy, preserving the
/// "ciphertext bound to its intended use" property of the contract.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public DpapiSecretProtector()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "DPAPI is only available on Windows. " +
                "Use DeployToolkit.Core.Secrets.AesGcmSecretProtector (passphrase or key file) instead.");
    }

    public string Protect(string plaintext, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var entropy = string.IsNullOrEmpty(purpose) ? null : Encoding.UTF8.GetBytes(purpose);
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
            plain, entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string ciphertext, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);
        var data = Convert.FromBase64String(ciphertext);
        var entropy = string.IsNullOrEmpty(purpose) ? null : Encoding.UTF8.GetBytes(purpose);
        var plain = System.Security.Cryptography.ProtectedData.Unprotect(
            data, entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
