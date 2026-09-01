using System.Text.Json;
using DeployToolkit.AppKit;      // pure registry-connection classes only — no Form/Control is ever instantiated here
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;

// =============================================================================
// Self-test for DeployToolkit.AppKit's PURE surface: RegistryConnectionSettings
// + RegistryConnectionFactory (and the Core stores they build). Deliberately
// headless — it never instantiates any WinForms Form/Control, so it runs on
// Linux/CI; the WinForms types in DeployToolkit.AppKit are lazily loaded and
// never touched. WinForms visuals are verified by compilation only (the
// sandbox has no desktop session).
//
// Check 8 is environment-dependent BY DESIGN but kept strict: with no SQL
// Server reachable, CreateOpenAsync in SqlServer mode must still prove it
// really attempts a connection (throws a connection-layer exception — NOT an
// ArgumentException, which would mean validation rejected the settings before
// any connection attempt).
// =============================================================================

var failures = new List<string>();
var passed = 0;

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

var workRoot = Path.Combine(Path.GetTempPath(), "DeployToolkitAppKitTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workRoot);

try
{
    // ---------------------------------------------------------------
    Console.WriteLine("== RegistryConnectionSettings: persistence ==");
    var settingsPath = Path.Combine(workRoot, "packager-registry.json");

    var sql = new RegistryConnectionSettings
    {
        Mode = RegistryMode.SqlServer,
        ConnectionString = "Server=tcp:example.database.windows.net,1433;Database=DeployToolkitRegistry;Authentication=Active Directory Default;Encrypt=True",
        LocalRoot = null,
    };
    sql.Save(settingsPath);
    var sqlBack = RegistryConnectionSettings.Load(settingsPath);
    Check("sql-mode settings round-trip: mode", sqlBack.Mode == RegistryMode.SqlServer);
    Check("sql-mode settings round-trip: connection string", sqlBack.ConnectionString == sql.ConnectionString);
    Check("sql-mode settings round-trip: local root stays null", sqlBack.LocalRoot is null);

    var local = new RegistryConnectionSettings
    {
        Mode = RegistryMode.LocalFile,
        ConnectionString = null,
        LocalRoot = Path.Combine(workRoot, "some-root"),
    };
    var localPath = Path.Combine(workRoot, "packager-local.json");
    local.Save(localPath);
    var localBack = RegistryConnectionSettings.Load(localPath);
    Check("local-mode settings round-trip: mode", localBack.Mode == RegistryMode.LocalFile);
    Check("local-mode settings round-trip: local root", localBack.LocalRoot == local.LocalRoot);

    Check("saved file uses readable mode string",
        File.ReadAllText(settingsPath).Contains("\"SqlServer\"", StringComparison.Ordinal));

    // ---------------------------------------------------------------
    Console.WriteLine("== RegistryConnectionSettings: tolerance ==");
    var missing = RegistryConnectionSettings.Load(Path.Combine(workRoot, "does-not-exist.json"));
    Check("missing file loads defaults (no throw)",
        missing.Mode == RegistryMode.LocalFile && missing.ConnectionString is null && missing.LocalRoot is null);

    var corruptPath = Path.Combine(workRoot, "corrupt.json");
    File.WriteAllText(corruptPath, "{ this is not json !!!");
    var corrupt = RegistryConnectionSettings.Load(corruptPath);
    Check("corrupt JSON loads defaults (no throw)", corrupt.Mode == RegistryMode.LocalFile && corrupt.LocalRoot is null);

    var wrongShapePath = Path.Combine(workRoot, "wrong-shape.json");
    File.WriteAllText(wrongShapePath, "[1, 2, 3]");
    var wrongShape = RegistryConnectionSettings.Load(wrongShapePath);
    Check("wrong-shape JSON (array) loads defaults (no throw)", wrongShape.Mode == RegistryMode.LocalFile);

    var forwardPath = Path.Combine(workRoot, "forward-compatible.json");
    File.WriteAllText(forwardPath,
        """{"mode":"SqlServer","connectionString":"Server=x","localRoot":null,"someFutureField":{"nested":[1,2]}}""");
    var forward = RegistryConnectionSettings.Load(forwardPath);
    Check("unknown future fields are tolerated and known fields parsed",
        forward.Mode == RegistryMode.SqlServer && forward.ConnectionString == "Server=x");

    var nestedPath = Path.Combine(workRoot, "nested", "deeper", "settings.json");
    new RegistryConnectionSettings { LocalRoot = workRoot }.Save(nestedPath);
    Check("Save creates missing parent directories (atomic-ish write)", File.Exists(nestedPath));
    Check("no temp file left behind after Save", !File.Exists(nestedPath + ".tmp"));
    var reparsed = RegistryConnectionSettings.Load(nestedPath);
    Check("re-parse after nested Save keeps data", reparsed.LocalRoot == workRoot);

    var defaultPath = RegistryConnectionSettings.DefaultSettingsPath;
    Check("DefaultSettingsPath is non-empty and absolute",
        defaultPath.Length > 0 && Path.IsPathRooted(defaultPath));
    Check("DefaultSettingsPath file name and layout",
        Path.GetFileName(defaultPath) == "packager-registry.json" &&
        Path.GetFileName(Path.GetDirectoryName(defaultPath)) == "DeployToolkit");

    // ---------------------------------------------------------------
    Console.WriteLine("== RegistryConnectionFactory: validation ==");
    var validationFailures = 0;
    try { RegistryConnectionFactory.Validate(new RegistryConnectionSettings { Mode = RegistryMode.SqlServer, ConnectionString = " " }); }
    catch (ArgumentException) { validationFailures++; }
    try { RegistryConnectionFactory.Validate(new RegistryConnectionSettings { Mode = RegistryMode.LocalFile, LocalRoot = "" }); }
    catch (ArgumentException) { validationFailures++; }
    try { RegistryConnectionFactory.Validate(new RegistryConnectionSettings { Mode = RegistryMode.SqlServer, ConnectionString = null }); }
    catch (ArgumentException) { validationFailures++; }
    Check("Validate() rejects SqlServer-without-connection-string and LocalFile-without-root", validationFailures == 3);

    var messageFieldNames = true;
    try { RegistryConnectionFactory.Validate(new RegistryConnectionSettings { Mode = RegistryMode.SqlServer, ConnectionString = " " }); }
    catch (ArgumentException ex) { messageFieldNames &= ex.Message.Contains("ConnectionString", StringComparison.Ordinal); }
    try { RegistryConnectionFactory.Validate(new RegistryConnectionSettings { Mode = RegistryMode.LocalFile, LocalRoot = "" }); }
    catch (ArgumentException ex) { messageFieldNames &= ex.Message.Contains("LocalRoot", StringComparison.Ordinal); }
    Check("validation messages name the offending field (actionable)", messageFieldNames);

    var validLocal = new RegistryConnectionSettings { Mode = RegistryMode.LocalFile, LocalRoot = workRoot };
    var validSql = new RegistryConnectionSettings { Mode = RegistryMode.SqlServer, ConnectionString = "Server=whatever" };
    var validOk = true;
    try
    {
        RegistryConnectionFactory.Validate(validLocal);
        RegistryConnectionFactory.Validate(validSql);
    }
    catch (ArgumentException)
    {
        validOk = false;
    }
    Check("Validate() accepts valid settings of both modes", validOk);

    Exception? preValidation = null;
    try
    {
        await RegistryConnectionFactory.CreateOpenAsync(
            new RegistryConnectionSettings { Mode = RegistryMode.SqlServer, ConnectionString = "" });
    }
    catch (Exception ex)
    {
        preValidation = ex;
    }
    Check("CreateOpenAsync validates BEFORE any connection attempt (empty conn string → ArgumentException)",
        preValidation is ArgumentException);

    // ---------------------------------------------------------------
    Console.WriteLine("== RegistryConnectionFactory: LocalFile store, full §19 profile ==");
    var localRoot = Path.Combine(workRoot, "registry-root");
    var localSettings = new RegistryConnectionSettings { Mode = RegistryMode.LocalFile, LocalRoot = localRoot };
    var profileStore = await RegistryConnectionFactory.CreateOpenAsync(localSettings);

    var client = await profileStore.CreateClientAsync("Client Alpha", "self-test client");

    // All 10 §19 profile fields + Notes.
    client.Notes = "self-test client";
    client.ContactPhone = "+966 50 123 4567";
    client.ContactEmail = "contact@clientalpha.example";
    client.GitRepositoryUrl = "https://dev.azure.com/org/ClientAlpha/_git/CMS";
    client.DeploymentBranch = "main";
    client.PublishConfiguration = new PublishConfiguration
    {
        DeploymentType = PublishDeploymentType.SelfContained,
        TargetRuntime = "win-x64",
        AdditionalPublishOptions = "-p:PublishTrimmed=false --nologo",
    };
    client.HasAmc = true;
    client.AmcExpiryDate = new DateOnly(2027, 3, 31);
    client.InfrastructureManagedBy = ManagedBy.Boxon;
    client.HostingAccountManagedBy = "Client — Mr. Saleh";
    await profileStore.UpdateClientAsync(client);

    var reloaded = await profileStore.GetClientAsync(client.ClientId);
    Check("profile round-trip: client found", reloaded is not null);
    Check("profile round-trip: notes", reloaded?.Notes == "self-test client");
    Check("profile round-trip: phone", reloaded?.ContactPhone == "+966 50 123 4567");
    Check("profile round-trip: email", reloaded?.ContactEmail == "contact@clientalpha.example");
    Check("profile round-trip: git URL", reloaded?.GitRepositoryUrl == "https://dev.azure.com/org/ClientAlpha/_git/CMS");
    Check("profile round-trip: branch", reloaded?.DeploymentBranch == "main");
    Check("profile round-trip: typed publish configuration",
        reloaded?.PublishConfiguration?.DeploymentType == PublishDeploymentType.SelfContained &&
        reloaded.PublishConfiguration.TargetRuntime == "win-x64" &&
        reloaded.PublishConfiguration.AdditionalPublishOptions == "-p:PublishTrimmed=false --nologo");
    Check("profile round-trip: stored publish JSON is canonical (enum as string)",
        reloaded?.PublishConfigurationJson?.Contains("\"SelfContained\"", StringComparison.Ordinal) == true);
    Check("profile round-trip: HasAmc", reloaded?.HasAmc == true);
    Check("profile round-trip: AMC expiry 2027-03-31", reloaded?.AmcExpiryDate == new DateOnly(2027, 3, 31));
    Check("profile round-trip: infrastructure managed by", reloaded?.InfrastructureManagedBy == ManagedBy.Boxon);
    Check("profile round-trip: hosting account managed by", reloaded?.HostingAccountManagedBy == "Client — Mr. Saleh");

    await profileStore.DeleteClientAsync(client.ClientId);
    var afterDelete = await profileStore.GetAllClientsAsync();
    Check("client deleted (DeleteClientAsync succeeds)", afterDelete.Count == 0);
    if (profileStore is IDisposable profileDisposable) profileDisposable.Dispose();

    // Trailing slash on the local root must be tolerated end-to-end.
    var trailingRoot = Path.Combine(workRoot, "trailing-root") + Path.DirectorySeparatorChar;
    var trailingStore = await RegistryConnectionFactory.CreateOpenAsync(
        new RegistryConnectionSettings { Mode = RegistryMode.LocalFile, LocalRoot = trailingRoot });
    var trailingClient = await trailingStore.CreateClientAsync("Client Gamma");
    Check("LocalRoot with trailing slash: store created and usable", trailingClient.ClientId.Length == 32);
    if (trailingStore is IDisposable trailingDisposable) trailingDisposable.Dispose();

    // ---------------------------------------------------------------
    Console.WriteLine("== RegistryConnectionFactory: LocalFile store, package lifecycle ==");
    var lifecycleRoot = Path.Combine(workRoot, "lifecycle");
    var lifecycleStore = await RegistryConnectionFactory.CreateOpenAsync(
        new RegistryConnectionSettings { Mode = RegistryMode.LocalFile, LocalRoot = lifecycleRoot });
    var lifecycleClient = await lifecycleStore.CreateClientAsync("Client Beta");
    var component = await lifecycleStore.CreateComponentAsync(new DeploymentComponent
    {
        ComponentId = Guid.NewGuid().ToString("N"),
        ClientId = lifecycleClient.ClientId,
        Name = "CMS",
        TargetType = TargetType.IisLocal,
        TargetFramework = "net8.0",
        IisSiteName = "ClientBeta",
        HealthCheckUrl = "http://127.0.0.1:59322/health",
    });

    var manifest = new ComponentManifest
    {
        ComponentId = component.ComponentId,
        Client = lifecycleClient.Name,
        Component = component.Name,
        Version = "1.2.0",
        CreatedUtc = DateTimeOffset.UtcNow,
        TargetFramework = "net8.0",
        Files = new[] { new ManifestFile("bin/App.dll", "sha256:aaaa", 3) },
    };
    var package = await lifecycleStore.CreatePackageAsync(component.ComponentId, manifest);

    await lifecycleStore.MarkDeployedAsync(package.PackageId, Environment.UserName, DateTimeOffset.UtcNow);
    var deployed = await lifecycleStore.GetPackageAsync(package.PackageId);
    Check("MarkDeployedAsync flips status and records who",
        deployed?.Status == PackageStatus.Deployed && deployed.DeployedBy == Environment.UserName &&
        deployed.DeployedUtc is not null);

    await lifecycleStore.MarkStatusAsync(package.PackageId, PackageStatus.Abandoned);
    var abandoned = await lifecycleStore.GetPackageAsync(package.PackageId);
    Check("MarkStatusAsync can abandon a package", abandoned?.Status == PackageStatus.Abandoned);

    await lifecycleStore.MarkStatusAsync(package.PackageId, PackageStatus.Created);
    var reopened = await lifecycleStore.GetPackageAsync(package.PackageId);
    Check("MarkStatusAsync can re-open a package (back to Created)", reopened?.Status == PackageStatus.Created);

    await lifecycleStore.RecordRunStartAsync(package.PackageId, DateTimeOffset.UtcNow);
    var refused = false;
    try { await lifecycleStore.DeletePackageAsync(package.PackageId, deleteRunHistory: false); }
    catch (InvalidOperationException) { refused = true; }
    Check("DeletePackageAsync refused while run history exists", refused);

    await lifecycleStore.DeletePackageAsync(package.PackageId, deleteRunHistory: true);
    Check("DeletePackageAsync with deleteRunHistory:true removes the package",
        await lifecycleStore.GetPackageAsync(package.PackageId) is null);
    if (lifecycleStore is IDisposable lifecycleDisposable) lifecycleDisposable.Dispose();

    // ---------------------------------------------------------------
    Console.WriteLine("== RegistryConnectionFactory: SqlServer mode attempts a real connection ==");
    // No SQL Server exists in this sandbox — the point of the check is that a
    // syntactically valid connection string passes Validate() and the factory
    // REALLY tries to connect (any connection-layer exception, never an
    // ArgumentException). Connect Timeout=1 + no retry keeps the failure fast.
    var sqlSettings = new RegistryConnectionSettings
    {
        Mode = RegistryMode.SqlServer,
        ConnectionString = "Server=127.0.0.1,1;Database=DeployToolkitRegistry;User Id=probe;Password=probe;" +
                           "Connect Timeout=1;ConnectRetryCount=0;ConnectRetryInterval=1;Encrypt=False",
    };
    RegistryConnectionFactory.Validate(sqlSettings); // must NOT throw
    Console.WriteLine("  (environment-dependent check: no SQL Server here — a connection exception proves the attempt)");
    Exception? connectionFailure = null;
    try
    {
        await RegistryConnectionFactory.CreateOpenAsync(sqlSettings);
    }
    catch (Exception ex)
    {
        connectionFailure = ex;
    }
    Check("SqlServer mode: CreateOpenAsync throws a connection-layer exception (not validation)",
        connectionFailure is not null &&
        connectionFailure is not ArgumentException &&
        connectionFailure is not ArgumentNullException);
    if (connectionFailure is not null)
        Console.WriteLine($"  → got {connectionFailure.GetType().Name}: {connectionFailure.Message.Split('\n')[0]}");
}
finally
{
    try
    {
        Directory.Delete(workRoot, recursive: true);
    }
    catch (IOException)
    {
        // best-effort cleanup
    }
}

Console.WriteLine();
Console.WriteLine("== UiMath: SplitContainer crash guard (deferred min-size / splitter math) ==");
// Background: SplitContainer re-clamps SplitterDistance inside the
// Panel1MinSize / Panel2MinSize / SplitterDistance setters against the
// CURRENT container size; a freshly constructed container is 150px wide, so
// assigning the ClientsScreen min sizes before layout threw
// "SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize".
// These checks pin the gate/clamp math that now routes every assignment.
Check("fresh container (150px) with ClientsScreen min sizes is rejected by the gate",
    !UiMath.CanApplySplit(150, 300, 560, 6, 360));
Check("fresh container yields no legal distance (null → 'skip the assignment')",
    UiMath.SafeSplitterDistance(150, 300, 560, 6, 360) is null);
Check("typical open size (1134px client width) accepts the intended split",
    UiMath.CanApplySplit(1134, 300, 560, 6, 360));
Check("legal distance passes through untouched",
    UiMath.SafeSplitterDistance(1134, 300, 560, 6, 360) == 360);
Check("distance below Panel1MinSize clamps up to the minimum",
    UiMath.SafeSplitterDistance(900, 300, 560, 6, 50) == 300);
Check("distance past the Panel2 boundary clamps down (900-560-6)",
    UiMath.SafeSplitterDistance(900, 300, 560, 6, 800) == 334);
Check("boundary: distance exactly on the Panel2 limit stays legal",
    UiMath.SafeSplitterDistance(866, 300, 560, 6, 300) == 300);
Check("gate rejects distances below Panel1MinSize",
    !UiMath.CanApplySplit(1134, 300, 560, 6, 299));
Check("gate rejects distances past the Panel2 limit",
    !UiMath.CanApplySplit(1134, 300, 560, 6, 569));
Check("horizontal split (components panel at min height) accepts H-266 at H=661",
    UiMath.CanApplySplit(661, 25, 260, 6, 661 - 266));
Check("horizontal split gate rejects the same distance one pixel tighter",
    !UiMath.CanApplySplit(660, 25, 260, 6, 395));

Console.WriteLine("== UiText: elapsed-clock rendering (busy dialog never looks frozen) ==");
Check("elapsed renders whole seconds under a minute",
    UiText.Elapsed(TimeSpan.FromSeconds(0)) == "0 s" &&
    UiText.Elapsed(TimeSpan.FromSeconds(42)) == "42 s");
Check("elapsed renders minutes:seconds between one minute and one hour",
    UiText.Elapsed(TimeSpan.FromSeconds(60)) == "1:00 min" &&
    UiText.Elapsed(TimeSpan.FromSeconds(187)) == "3:07 min");
Check("elapsed renders hours:minutes:seconds beyond one hour",
    UiText.Elapsed(TimeSpan.FromSeconds(3600)) == "1:00:00 h" &&
    UiText.Elapsed(TimeSpan.FromSeconds(3723)) == "1:02:03 h");
Check("elapsed clamps negative values to zero (Stopwatch cannot go back, but be safe)",
    UiText.Elapsed(TimeSpan.FromSeconds(-5)) == "0 s");

// ==============================================================
// ShellScreenPolicy: the Packager MDI shell's screen-switching rules
// (fixes the reported pile-up — "opening another form never closed the
// previous one, so closing meant one form at a time")
// ==============================================================

Console.WriteLine("== ShellScreenPolicy: screen metadata ==");
Check("registry-bound screens are wizard/clients/reconcile, not connection",
    ShellScreenPolicy.IsRegistryBound(ShellScreen.Wizard) &&
    ShellScreenPolicy.IsRegistryBound(ShellScreen.Clients) &&
    ShellScreenPolicy.IsRegistryBound(ShellScreen.Reconcile) &&
    !ShellScreenPolicy.IsRegistryBound(ShellScreen.Connection));
Check("display names are non-empty and distinct for all screens",
    Enum.GetValues<ShellScreen>().Select(ShellScreenPolicy.DisplayName)
        .All(name => !string.IsNullOrEmpty(name) && name.IndexOf(',') < 0 && name.IndexOf('.') < 0)
    && Enum.GetValues<ShellScreen>().Select(ShellScreenPolicy.DisplayName).Distinct().Count() == 4);

Console.WriteLine("== ShellScreenPolicy: opening a stateless screen replaces stateless siblings ==");
var none = Array.Empty<ShellScreen>();

// Wizard + Reconcile open, user opens Clients → Reconcile makes way, wizard stays.
var open = new List<ShellScreen> { ShellScreen.Wizard, ShellScreen.Reconcile };
var d = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Clients, open, none);
Check("opening Clients with wizard+reconcile open closes only reconcile",
    d.Proceed && d.ActivateInstead is null &&
    d.ScreensToClose.Count == 1 && d.ScreensToClose[0] == ShellScreen.Reconcile);
Check("no confirmation when the closed sibling holds no unsaved work",
    !d.ConfirmationRequired);

// Clients open, user opens Reconcile → Clients closes (replaced, never stacked).
open = new List<ShellScreen> { ShellScreen.Clients };
d = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Reconcile, open, none);
Check("opening Reconcile replaces an open Clients screen",
    d.Proceed && d.ScreensToClose.Count == 1 && d.ScreensToClose[0] == ShellScreen.Clients);

// Two stateless screens + wizard, user opens Connection → both make way, wizard stays.
open = new List<ShellScreen> { ShellScreen.Wizard, ShellScreen.Clients, ShellScreen.Reconcile };
d = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Connection, open, none);
Check("opening Connection clears the other stateless screens",
    d.Proceed &&
    d.ScreensToClose.Count == 2 &&
    d.ScreensToClose.Contains(ShellScreen.Clients) &&
    d.ScreensToClose.Contains(ShellScreen.Reconcile));
Check("the in-progress wizard is NEVER closed by opening another screen",
    !d.ScreensToClose.Contains(ShellScreen.Wizard));

Console.WriteLine("== ShellScreenPolicy: re-open an open screen = focus, no close ==");
open = new List<ShellScreen> { ShellScreen.Wizard, ShellScreen.Clients };
d = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Clients, open, none);
Check("opening an already-open stateless screen just activates it",
    !d.Proceed && d.ActivateInstead == ShellScreen.Clients &&
    d.ScreensToClose.Count == 0 && !d.ConfirmationRequired);

Console.WriteLine("== ShellScreenPolicy: the wizard is pinned while in progress ==");
open = new List<ShellScreen> { ShellScreen.Wizard };
var guarded = new List<ShellScreen> { ShellScreen.Wizard };
d = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Clients, open, guarded);
Check("opening Clients with an in-progress wizard keeps the wizard open",
    d.Proceed && d.ScreensToClose.Count == 0 && !d.ConfirmationRequired);

open = new List<ShellScreen> { ShellScreen.Wizard, ShellScreen.Clients };
guarded = new List<ShellScreen> { ShellScreen.Wizard, ShellScreen.Clients };
d = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Reconcile, open, guarded);
Check("guarded siblings closing on a switch need consent (wizard + dirty clients)",
    d.Proceed &&
    d.ScreensToClose.Count == 1 && d.ScreensToClose[0] == ShellScreen.Clients &&
    d.ConfirmationRequired && d.GuardedScreensToClose.Count == 1 &&
    d.GuardedScreensToClose[0] == ShellScreen.Clients);

Console.WriteLine("== ShellScreenPolicy: one wizard at a time ==");
open = new List<ShellScreen> { ShellScreen.Wizard };
guarded = new List<ShellScreen> { ShellScreen.Wizard };
d = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Wizard, open, guarded);
Check("new wizard replaces an in-progress wizard WITH consent",
    d.Proceed && d.ScreensToClose.Count == 1 &&
    d.ScreensToClose[0] == ShellScreen.Wizard &&
    d.ConfirmationRequired && d.GuardedScreensToClose[0] == ShellScreen.Wizard);

open = new List<ShellScreen> { ShellScreen.Wizard };
d = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Wizard, open, none);
Check("new wizard replaces a FRESH (no progress) wizard silently",
    d.Proceed && d.ScreensToClose.Count == 1 &&
    d.ScreensToClose[0] == ShellScreen.Wizard && !d.ConfirmationRequired);

open = new List<ShellScreen> { ShellScreen.Wizard, ShellScreen.Clients };
d = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Wizard, open, none);
Check("new wizard also clears the other open screens",
    d.Proceed &&
    d.ScreensToClose.Count == 2 &&
    d.ScreensToClose.Contains(ShellScreen.Clients) &&
    d.ScreensToClose.Contains(ShellScreen.Wizard) && !d.ConfirmationRequired);

Console.WriteLine("== ShellScreenPolicy: close-all ==");
open = new List<ShellScreen> { ShellScreen.Wizard, ShellScreen.Clients };
guarded = new List<ShellScreen> { ShellScreen.Wizard };
d = ShellScreenPolicy.PlanCloseAll(open, guarded);
Check("close-all closes everything, consent only for the guarded subset",
    d.Proceed && d.ScreensToClose.Count == 2 &&
    d.ConfirmationRequired && d.GuardedScreensToClose.Count == 1 &&
    d.GuardedScreensToClose[0] == ShellScreen.Wizard);

d = ShellScreenPolicy.PlanCloseAll(none, none);
Check("close-all with nothing open is a no-op",
    d.Proceed && d.ScreensToClose.Count == 0 && !d.ConfirmationRequired);

Console.WriteLine("== ShellScreenPolicy: null-argument guards ==");
var nullArgThrown = false;
try
{
    _ = ShellScreenPolicy.PlanScreenOpen(ShellScreen.Clients, null!, none);
}
catch (ArgumentNullException)
{
    nullArgThrown = true;
}
Check("PlanScreenOpen rejects a null open-list", nullArgThrown);

nullArgThrown = false;
try
{
    _ = ShellScreenPolicy.PlanCloseAll(none, null!);
}
catch (ArgumentNullException)
{
    nullArgThrown = true;
}
Check("PlanCloseAll rejects a null guarded-list", nullArgThrown);

Console.WriteLine();
Console.WriteLine($"AppKit registry-connection self-test: {passed} passed, {failures.Count} failed");
if (failures.Count > 0)
{
    Console.WriteLine("Failed checks:");
    foreach (var failure in failures)
        Console.WriteLine($"  - {failure}");
    return 1;
}
return 0;
