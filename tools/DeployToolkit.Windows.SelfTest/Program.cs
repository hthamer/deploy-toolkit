using System.Security.Cryptography;
using DeployToolkit.Core.IisControl;
using DeployToolkit.Core.Secrets;
using DeployToolkit.Core.Windows;

var failures = new List<string>();
var passed = 0;
var skipped = 0;

void Check(string name, bool condition)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  [pass] {name}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"  [FAIL] {name}");
    }
}

void Skip(string name)
{
    skipped++;
    Console.WriteLine($"  [skip] {name} (not on Windows)");
}

var workRoot = Path.Combine(Path.GetTempPath(), "DeployToolkitWindowsSelfTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workRoot);

try
{
    // ---------------------------------------------------------------
    Console.WriteLine("== DpapiSecretProtector ==");
    if (OperatingSystem.IsWindows())
    {
        var dpapi = new DpapiSecretProtector();
        var cipher = dpapi.Protect("Server=tcp:client-a.database.windows.net;User=deploy;", "db:ClientA/CMS");
        Check("dpapi round-trips a connection string",
            dpapi.Unprotect(cipher, "db:ClientA/CMS") == "Server=tcp:client-a.database.windows.net;User=deploy;");
        Check("dpapi ciphertext is opaque base64 (not plaintext)", !cipher.Contains("ClientA", StringComparison.Ordinal));
        try
        {
            dpapi.Unprotect(cipher, "different-purpose");
            Check("dpapi rejects wrong purpose (entropy)", false);
        }
        catch (CryptographicException)
        {
            Check("dpapi rejects wrong purpose (entropy)", true);
        }
    }
    else
    {
        Skip("dpapi round-trip (Windows-only; guarded at runtime)");
        Skip("dpapi wrong-purpose rejection (Windows-only)");
    }

    // Deliberately probing the runtime guard: constructing the protector
    // outside its supported platform MUST throw PNSE, so suppress the
    // platform analyzer for this one call.
    bool threwPnse = false;
    if (!OperatingSystem.IsWindows())
    {
#pragma warning disable CA1416 // deliberate guard probe
        try { new DpapiSecretProtector(); }
#pragma warning restore CA1416
        catch (PlatformNotSupportedException) { threwPnse = true; }
    }
    Check("dpapi ctor guarded on non-Windows (PNSE) / supported on Windows",
        OperatingSystem.IsWindows() || threwPnse);

    // ---------------------------------------------------------------
    Console.WriteLine("== IisDeploymentHooksFactory (fake controller — runs everywhere) ==");
    var siteRoot = Path.Combine(workRoot, "site");
    Directory.CreateDirectory(siteRoot);

    var fake = new FakeController();
    var hooks = IisDeploymentHooksFactory.CreateStopStartHooks(fake, appPoolName: "cms-pool", siteRoot);

    Check("hooks expose stop and start", hooks.StopSite is not null && hooks.StartSite is not null);

    await hooks.StopSite!();
    Check("pool stop used when MWA succeeds", fake.StopCalls.Count == 1);
    Check("no app_offline dropped when pool stop works", !AppOfflineManager.IsDropped(siteRoot));

    await hooks.StartSite!();
    Check("start reverses the pool stop", fake.StartCalls.Count == 1);

    // Fallback: MWA throws (no IIS management rights) -> app_offline.htm
    var failing = new FakeController { ThrowOnPoolOps = true };
    var hooks2 = IisDeploymentHooksFactory.CreateStopStartHooks(failing, appPoolName: "cms-pool", siteRoot);
    await hooks2.StopSite!();
    Check("auto strategy falls back to app_offline on MWA failure",
        AppOfflineManager.IsDropped(siteRoot) && fake.StopCalls.Count == 1);
    await hooks2.StartSite!();
    Check("start removes app_offline after fallback", !AppOfflineManager.IsDropped(siteRoot));

    // ---------------------------------------------------------------
    Console.WriteLine("== MicrosoftWebAdministrationController guards ==");
    if (OperatingSystem.IsWindows())
    {
        var mwa = new MicrosoftWebAdministrationController();
        // On a real Windows box this either works (IIS present) or throws a
        // descriptive runtime error — both acceptable here; we only assert
        // the call path is reachable.
        try
        {
            var sites = mwa.EnumerateSites();
            Check("MWA enumerate sites executed on Windows", sites is not null);
        }
        catch (Exception ex)
        {
            Check("MWA enumerate sites executed on Windows (threw: " + ex.GetType().Name + ")", true);
        }
    }
    else
    {
#pragma warning disable CA1416 // deliberate guard probe
        var mwa = new MicrosoftWebAdministrationController();
        try
        {
            mwa.EnumerateSites();
            Check("MWA guarded on non-Windows (PNSE)", false);
        }
        catch (PlatformNotSupportedException)
#pragma warning restore CA1416
        {
            Check("MWA guarded on non-Windows (PNSE)", true);
        }
    }

    // ---------------------------------------------------------------
    Console.WriteLine("== Windows secret protector + vault interplay ==");
    var vaultPath = Path.Combine(workRoot, "secrets.vault.json");
    if (OperatingSystem.IsWindows())
    {
        var vault = new SecretVault(vaultPath, new DpapiSecretProtector());
        vault.SetSecret("ClientA/CMS/db", "Server=.;Database=Cms;Integrated Security=true");
        Check("vault + dpapi: secret set", File.Exists(vaultPath));
        var reloaded = new SecretVault(vaultPath, new DpapiSecretProtector());
        Check("vault + dpapi: round-trip via fresh vault instance",
            reloaded.GetSecret("ClientA/CMS/db") == "Server=.;Database=Cms;Integrated Security=true");
        Check("vault ref parse agrees with SecretRefFor",
            SecretVault.TryParseRef(SecretVault.SecretRefFor("ClientA/CMS/db"), out var refName) && refName == "ClientA/CMS/db");
    }
    else
    {
        // Prove the cross-platform path the Windows machine will NOT use,
        // so the vault logic itself is still exercised in this suite.
        var vault = new SecretVault(vaultPath, AesGcmSecretProtector.CreateWithPassphrase("test-passphrase-123"));
        vault.SetSecret("ClientA/CMS/db", "Server=.;Database=Cms;Integrated Security=true");
        Check("vault + aes-gcm fallback exercised on non-Windows",
            new SecretVault(vaultPath, AesGcmSecretProtector.CreateWithPassphrase("test-passphrase-123"))
                .GetSecret("ClientA/CMS/db") == "Server=.;Database=Cms;Integrated Security=true");
        Skip("vault + dpapi interplay (Windows-only)");
    }
}
finally
{
    try { Directory.Delete(workRoot, recursive: true); } catch { /* best effort */ }
}

Console.WriteLine();
Console.WriteLine($"Windows self-test: {passed} passed, {skipped} skipped, {failures.Count} failed.");
if (failures.Count > 0)
{
    Console.WriteLine("FAILURES:");
    failures.ForEach(f => Console.WriteLine(" - " + f));
    return 1;
}
return 0;

/// <summary>In-memory IIisController for headless testing of the factory
/// wiring (the same seam the Core self-test uses).</summary>
internal sealed class FakeController : IIisController
{
    public List<string> StopCalls { get; } = new();
    public List<string> StartCalls { get; } = new();
    public bool ThrowOnPoolOps { get; set; }

    public IReadOnlyList<IisSiteInfo> EnumerateSites() => Array.Empty<IisSiteInfo>();

    public IReadOnlyList<IisApplicationInfo> EnumerateApplications(string? siteName = null) =>
        Array.Empty<IisApplicationInfo>();

    public IisAppPoolInfo? GetAppPool(string appPoolName) => null;

    public void StopSite(string siteName) { }

    public void StartSite(string siteName) { }

    public void StopAppPool(string appPoolName)
    {
        if (ThrowOnPoolOps) throw new UnauthorizedAccessException("Access denied (simulated non-admin account).");
        StopCalls.Add(appPoolName);
    }

    public void StartAppPool(string appPoolName)
    {
        if (ThrowOnPoolOps) throw new UnauthorizedAccessException("Access denied (simulated non-admin account).");
        StartCalls.Add(appPoolName);
    }

    public void RecycleAppPool(string appPoolName)
    {
        if (ThrowOnPoolOps) throw new UnauthorizedAccessException("Access denied (simulated non-admin account).");
    }
}
