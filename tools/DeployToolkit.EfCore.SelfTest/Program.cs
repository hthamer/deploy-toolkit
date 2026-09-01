using System.Net;
using DeployToolkit.Core.Backup;
using DeployToolkit.Core.Deployment;
using DeployToolkit.Core.EfCore;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;
using Microsoft.EntityFrameworkCore;

// =============================================================================
// Self-test for EfCoreRegistryStore — the EF Core / SQL Server registry
// implementation (plan Phase 1). The model + migrations are provider-neutral,
// so the exact same store that production points at SQL Server / Azure SQL
// runs here against a temp SQLite database, including applying the real
// InitialCreate migration. All semantics checked are the ones the baseline /
// stale-package / deploy-result logic depends on.
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

var workRoot = Path.Combine(Path.GetTempPath(), "DeployToolkitEfTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workRoot);

var dbPath = Path.Combine(workRoot, "registry.db");
var factory = new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<RegistryDbContext>(
    new DbContextOptionsBuilder<RegistryDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .Options);

var registry = new EfCoreRegistryStore(factory);

try
{
    // ---------------------------------------------------------------
    Console.WriteLine("== Schema creation (provider-neutral model over SQLite) ==");
    // Migrations are SQL Server-flavored by design (column types baked in at
    // `dotnet ef migrations add` time) — production schema comes from
    // InitializeAsync()/MigrateAsync against SQL Server. Here the SAME model
    // creates the schema directly via EnsureCreated, which is exactly how
    // test/throwaway stores should bootstrap.
    await registry.EnsureCreatedAsync();
    int tableCount;
    await using (var verify = await factory.CreateDbContextAsync())
    {
        tableCount = await verify.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name IN ('Clients','DeploymentComponents','Packages','DeploymentRuns')")
            .SingleAsync();
    }
    Check("all four registry tables were created", tableCount == 4);

    // Creating twice must be a no-op (idempotent startup path).
    await registry.EnsureCreatedAsync();
    Check("second EnsureCreatedAsync is a no-op", true);

    // ---------------------------------------------------------------
    Console.WriteLine("== Clients & components ==");
    var client = await registry.CreateClientAsync("ClientA", "first client");
    Check("client created with id", client.ClientId.Length == 32);

    var foundIgnoreCase = await registry.FindClientByNameAsync("clienta");
    Check("FindClientByNameAsync is case-insensitive", foundIgnoreCase?.ClientId == client.ClientId);

    var missing = await registry.FindClientByNameAsync("Nope");
    Check("unknown client name returns null", missing is null);

    var fetched = await registry.GetClientAsync(client.ClientId);
    Check("GetClientAsync round-trips (and works across contexts)", fetched?.Name == "ClientA");

    var component = await registry.CreateComponentAsync(new DeploymentComponent
    {
        ComponentId = Guid.NewGuid().ToString("N"),
        ClientId = client.ClientId,
        Name = "CMS",
        TargetType = TargetType.IisLocal,
        TargetFramework = "net8.0",
        IsSelfContained = false,
        HealthCheckUrl = "http://127.0.0.1:59322/health",
    });
    Check("component created", component.ComponentId.Length == 32);

    var components = await registry.GetComponentsForClientAsync(client.ClientId);
    Check("GetComponentsForClientAsync returns the component", components.Count == 1 && components[0].ComponentId == component.ComponentId);

    var byId = await registry.GetComponentAsync(component.ComponentId);
    Check("GetComponentAsync round-trips enum + flags", byId is not null
        && byId.TargetType == TargetType.IisLocal
        && byId.IsSelfContained == false
        && byId.TargetFramework == "net8.0");

    // UpdateComponentAsync: the Packager's publish step writes the framework
    // / self-contained values the user corrected back into the component.
    await registry.UpdateComponentAsync(new DeploymentComponent
    {
        ComponentId = component.ComponentId,
        ClientId = component.ClientId,
        Name = component.Name,
        TargetType = component.TargetType,
        TargetFramework = "net48",
        IsSelfContained = false,
        HealthCheckUrl = component.HealthCheckUrl,
        IisSiteName = component.IisSiteName,
        IisAppPath = component.IisAppPath,
    });
    var afterUpdate = await registry.GetComponentAsync(component.ComponentId);
    Check("UpdateComponentAsync persists the corrected framework",
        afterUpdate is not null && afterUpdate.TargetFramework == "net48" && afterUpdate.HealthCheckUrl == component.HealthCheckUrl);

    // ---------------------------------------------------------------
    Console.WriteLine("== PackageBuilder A/B/C scenario against the EF registry ==");
    var mappingStore = new JsonFileProjectMappingStore(Path.Combine(workRoot, "project-mappings.json"));
    var builder = new PackageBuilder(registry, mappingStore);

    var projectFolder = Path.Combine(workRoot, "projects", "ClientA-CMS");
    Directory.CreateDirectory(projectFolder);
    await builder.RegisterFolderMappingAsync(projectFolder, component.ComponentId);

    // --- Package A: built, never deployed ---
    var publishA = Path.Combine(workRoot, "scenario", "publish-A");
    Directory.CreateDirectory(Path.Combine(publishA, "bin"));
    File.WriteAllText(Path.Combine(publishA, "bin", "App.dll"), "version-A-content");

    var resultA = await builder.BuildAsync(new PackageBuildRequest(
        component.ComponentId, "1.2.0", publishA, Path.Combine(workRoot, "scenario", "A.zip")));
    Check("package A created with 0 stale packages beforehand", resultA.UnresolvedStalePackages.Count == 0);
    Check("package A treats first release as all-new", resultA.Manifest.Files.Count == 1);

    // --- Package B: built later, WILL be deployed ---
    var publishB = Path.Combine(workRoot, "scenario", "publish-B");
    Directory.CreateDirectory(Path.Combine(publishB, "bin"));
    File.WriteAllText(Path.Combine(publishB, "bin", "App.dll"), "version-B-content");

    var resultB = await builder.BuildAsync(new PackageBuildRequest(
        component.ComponentId, "1.3.0", publishB, Path.Combine(workRoot, "scenario", "B.zip")));
    Check("package B sees package A as the one stale/undeployed package",
        resultB.UnresolvedStalePackages.Count == 1 && resultB.UnresolvedStalePackages[0].Version == "1.2.0");
    Check("package B still treats everything as new (A was never deployed, so no baseline exists)",
        resultB.Manifest.Files.Count == 1);

    await registry.MarkDeployedAsync(resultB.Record.PackageId, "hassan", DateTimeOffset.UtcNow);
    await registry.MarkStatusAsync(resultA.Record.PackageId, PackageStatus.Abandoned);

    // --- Package C: built weeks later, must diff against B (the last DEPLOYED), never A ---
    var publishC = Path.Combine(workRoot, "scenario", "publish-C");
    Directory.CreateDirectory(Path.Combine(publishC, "bin"));
    File.WriteAllText(Path.Combine(publishC, "bin", "App.dll"), "version-B-content"); // unchanged from B
    File.WriteAllText(Path.Combine(publishC, "bin", "NewFeature.dll"), "new-in-C");   // new in C

    var resultC = await builder.BuildAsync(new PackageBuildRequest(
        component.ComponentId, "1.4.0", publishC, Path.Combine(workRoot, "scenario", "C.zip")));
    Check("package C sees zero stale packages (A was abandoned)", resultC.UnresolvedStalePackages.Count == 0);
    Check("package C's baseline is B, not A", resultC.Manifest.BaselineManifest == resultB.Record.PackageId);
    Check("package C correctly excludes the file unchanged since B",
        resultC.Manifest.Files.All(f => f.Path != "bin/App.dll"));
    Check("package C correctly includes only the genuinely new file",
        resultC.Manifest.Files.Count == 1 && resultC.Manifest.Files[0].Path == "bin/NewFeature.dll");

    // ---------------------------------------------------------------
    Console.WriteLine("== DeploymentOrchestrator against the EF registry ==");
    var healthShouldPass = true;
    using var listener = new HttpListener();
    listener.Prefixes.Add("http://127.0.0.1:59322/");
    listener.Start();
    var listenerCts = new CancellationTokenSource();
    var listenerTask = Task.Run(async () =>
    {
        while (!listenerCts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            ctx.Response.StatusCode = healthShouldPass ? 200 : 500;
            ctx.Response.Close();
        }
    });

    async Task<bool> HttpHealthCheck(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await client.GetAsync(url);
        return response.IsSuccessStatusCode;
    }

    var deploySiteRoot = Path.Combine(workRoot, "deploy-site");
    Directory.CreateDirectory(Path.Combine(deploySiteRoot, "bin"));
    File.WriteAllText(Path.Combine(deploySiteRoot, "bin", "NewFeature.dll"), "OLD-CONTENT-BEFORE-DEPLOY");
    var appSettingsPath = Path.Combine(deploySiteRoot, "appsettings.json");
    File.WriteAllText(appSettingsPath, """{ "ConnectionStrings": { "Default": "Server=prod;..." } }""");

    var orchestrator = new DeploymentOrchestrator(registry, new BackupManager(Path.Combine(workRoot, "deploy-backups")));
    var stopStartLog = new List<string>();
    var hooks = new DeploymentHooks(
        StopSite: () => { stopStartLog.Add("stop"); return Task.CompletedTask; },
        StartSite: () => { stopStartLog.Add("start"); return Task.CompletedTask; },
        HealthCheck: HttpHealthCheck);

    // --- Happy path ---
    healthShouldPass = true;
    var runOk = await orchestrator.RunAsync(
        new DeploymentRunRequest(resultC.ZipPath, deploySiteRoot, appSettingsPath, "ClientA", "CMS", "hassan", hooks),
        resultC.Record.PackageId);

    Check("successful run reports Success", runOk.Success);
    Check("file was actually deployed", File.ReadAllText(Path.Combine(deploySiteRoot, "bin", "NewFeature.dll")) == "new-in-C");

    var latestDeployed = await registry.GetLatestDeployedPackageAsync(component.ComponentId);
    Check("registry now reports package C as the latest deployed package", latestDeployed?.PackageId == resultC.Record.PackageId);

    // --- Failure path: health check fails -> rollback, NOT marked deployed ---
    var publishD = Path.Combine(workRoot, "scenario", "publish-D");
    Directory.CreateDirectory(Path.Combine(publishD, "bin"));
    File.WriteAllText(Path.Combine(publishD, "bin", "NewFeature.dll"), "BROKEN-version-D");

    var resultD = await builder.BuildAsync(new PackageBuildRequest(
        component.ComponentId, "1.5.0", publishD, Path.Combine(workRoot, "scenario", "D.zip")));

    stopStartLog.Clear();
    healthShouldPass = false;
    var runFailed = await orchestrator.RunAsync(
        new DeploymentRunRequest(resultD.ZipPath, deploySiteRoot, appSettingsPath, "ClientA", "CMS", "hassan", hooks),
        resultD.Record.PackageId);

    Check("failed health check triggers rollback", !runFailed.Success && runFailed.RolledBack);
    Check("file content was restored to pre-deploy (C's content), not left as broken D",
        File.ReadAllText(Path.Combine(deploySiteRoot, "bin", "NewFeature.dll")) == "new-in-C");

    var stillLatestDeployed = await registry.GetLatestDeployedPackageAsync(component.ComponentId);
    Check("package D was NOT marked deployed after rollback", stillLatestDeployed?.PackageId == resultC.Record.PackageId);

    // ---------------------------------------------------------------
    Console.WriteLine("== Deployment run records (audit trail) ==");
    await using (var audit = await factory.CreateDbContextAsync())
    {
        var runs = (await audit.DeploymentRuns
            .Where(r => r.PackageId == resultC.Record.PackageId || r.PackageId == resultD.Record.PackageId)
            .ToListAsync())
            // client-side order: SQLite can't ORDER BY DateTimeOffset
            .OrderBy(r => r.StartedUtc)
            .ToList();

        Check("two runs were recorded", runs.Count == 2);
        Check("successful run recorded Result=Success with passing health check",
            runs.Any(r => r.Result == "Success" && r.HealthCheckResult == true && r.CompletedUtc is not null));
        Check("failed run recorded Result=RolledBack with failing health check",
            runs.Any(r => r.Result == "RolledBack" && r.HealthCheckResult == false && r.CompletedUtc is not null));
        Check("manifest JSON stored on packages deserializes back through ManifestSerializer",
            (await audit.Packages.ToListAsync()).All(p =>
            {
                var m = ManifestSerializer.Deserialize(p.ManifestJson);
                return m.Version == p.Version && m.ComponentId == component.ComponentId;
            }));
    }

    // ---------------------------------------------------------------
    Console.WriteLine("== Client profile & package management (pre-WinForms feature) ==");

    async Task<bool> ThrowsAsync<TEx>(Func<Task> action) where TEx : Exception
    {
        try { await action(); return false; }
        catch (TEx) { return true; }
    }

    var clientB = await registry.CreateClientAsync("ClientB");
    clientB.Notes = "profile round-trip";
    clientB.ContactPhone = "+966 55 123 4567";
    clientB.ContactEmail = "it@clientb.example";
    clientB.GitRepositoryUrl = "https://git.example.com/clientb/site.git";
    clientB.DeploymentBranch = "main";
    clientB.PublishConfiguration = new PublishConfiguration
    {
        DeploymentType = PublishDeploymentType.SelfContained,
        TargetRuntime = "win-x64",
        AdditionalPublishOptions = "-p:PublishTrimmed=false",
    };
    clientB.HasAmc = true;
    clientB.AmcExpiryDate = new DateOnly(2027, 3, 31);
    clientB.InfrastructureManagedBy = ManagedBy.Boxon;
    clientB.HostingAccountManagedBy = "Boxon — shared cPanel";
    await registry.UpdateClientAsync(clientB);

    var reloadedB = await registry.GetClientAsync(clientB.ClientId);
    Check("full client profile round-trips every field (incl. DateOnly + enum)",
        reloadedB is not null
        && reloadedB.ContactPhone == "+966 55 123 4567"
        && reloadedB.ContactEmail == "it@clientb.example"
        && reloadedB.GitRepositoryUrl == "https://git.example.com/clientb/site.git"
        && reloadedB.DeploymentBranch == "main"
        && reloadedB.HasAmc
        && reloadedB.AmcExpiryDate == new DateOnly(2027, 3, 31)
        && reloadedB.InfrastructureManagedBy == ManagedBy.Boxon
        && reloadedB.HostingAccountManagedBy == "Boxon — shared cPanel");
    Check("publish configuration JSON round-trips through the typed accessor",
        reloadedB?.PublishConfiguration is not null
        && reloadedB.PublishConfiguration.DeploymentType == PublishDeploymentType.SelfContained
        && reloadedB.PublishConfiguration.TargetRuntime == "win-x64"
        && reloadedB.PublishConfiguration.AdditionalPublishOptions == "-p:PublishTrimmed=false");

    var allClients = await registry.GetAllClientsAsync();
    Check("GetAllClientsAsync lists both clients ordered by name (case-insensitive)",
        allClients.Count == 2 && allClients[0].Name == "ClientA" && allClients[1].Name == "ClientB");

    clientB.Name = "ClientBeta";
    await registry.UpdateClientAsync(clientB);
    Check("UpdateClientAsync persists a rename", (await registry.GetClientAsync(clientB.ClientId))!.Name == "ClientBeta");

    clientB.Name = "clienta";
    Check("UpdateClientAsync refuses a duplicate (case-insensitive) name",
        await ThrowsAsync<InvalidOperationException>(() => registry.UpdateClientAsync(clientB)));

    // Validation guards — bad data must never reach the database.
    clientB.Name = "";
    Check("empty client name rejected", await ThrowsAsync<ArgumentException>(() => registry.UpdateClientAsync(clientB)));
    clientB.Name = "ClientBeta";
    clientB.ContactEmail = "not-an-email";
    Check("invalid email rejected", await ThrowsAsync<ArgumentException>(() => registry.UpdateClientAsync(clientB)));
    clientB.ContactEmail = "it@clientb.example";
    clientB.GitRepositoryUrl = "not an absolute url";
    Check("non-absolute git URL rejected", await ThrowsAsync<ArgumentException>(() => registry.UpdateClientAsync(clientB)));
    clientB.GitRepositoryUrl = "https://git.example.com/clientb/site.git";
    clientB.DeploymentBranch = "main dev";
    Check("branch containing a space rejected", await ThrowsAsync<ArgumentException>(() => registry.UpdateClientAsync(clientB)));
    clientB.DeploymentBranch = "main";

    Check("updating an unknown client fails", await ThrowsAsync<InvalidOperationException>(() =>
        registry.UpdateClientAsync(new Client { ClientId = Guid.NewGuid().ToString("N"), Name = "Ghost" })));

    // Package-management grid + deletion rules over the A/B/C/D scenario.
    var allPackages = await registry.GetPackagesForComponentAsync(component.ComponentId);
    Check("GetPackagesForComponentAsync returns every package regardless of status", allPackages.Count == 4);
    Check("packages are newest-first (built 1.2.0 → 1.5.0)",
        allPackages[0].Version == "1.5.0" && allPackages[^1].Version == "1.2.0");

    var packageD = await registry.GetPackageAsync(resultD.Record.PackageId);
    Check("GetPackageAsync finds a package by id", packageD?.Version == "1.5.0");
    Check("GetPackageAsync returns null for an unknown id",
        await registry.GetPackageAsync(Guid.NewGuid().ToString("N")) is null);

    Check("deleting a package with run history is refused without deleteRunHistory",
        await ThrowsAsync<InvalidOperationException>(() => registry.DeletePackageAsync(resultD.Record.PackageId)));

    await registry.DeletePackageAsync(resultD.Record.PackageId, deleteRunHistory: true);
    int remainingRunsForD;
    await using (var audit = await factory.CreateDbContextAsync())
        remainingRunsForD = await audit.DeploymentRuns.CountAsync(r => r.PackageId == resultD.Record.PackageId);
    Check("deleteRunHistory:true removes the package together with its run records",
        await registry.GetPackageAsync(resultD.Record.PackageId) is null && remainingRunsForD == 0);

    await registry.DeletePackageAsync(resultA.Record.PackageId);
    Check("deleting a run-less package needs no flag",
        await registry.GetPackageAsync(resultA.Record.PackageId) is null
        && (await registry.GetPackagesForComponentAsync(component.ComponentId)).Count == 2);

    var baselineAfterDeletes = await registry.GetLatestDeployedPackageAsync(component.ComponentId);
    Check("deleting other packages does not disturb the deployed baseline",
        baselineAfterDeletes?.PackageId == resultC.Record.PackageId);

    // Client deletion rules.
    Check("deleting a client that still has components is refused",
        await ThrowsAsync<InvalidOperationException>(() => registry.DeleteClientAsync(client.ClientId)));

    await registry.DeleteClientAsync(clientB.ClientId);
    Check("deleting a component-less client succeeds",
        (await registry.GetAllClientsAsync()).All(c => c.ClientId != clientB.ClientId));

    Check("deleting an unknown client fails",
        await ThrowsAsync<InvalidOperationException>(() => registry.DeleteClientAsync(Guid.NewGuid().ToString("N"))));

    listenerCts.Cancel();
    listener.Stop();
    try { await listenerTask; } catch { /* listener loop exit */ }
}
finally
{
    try { Directory.Delete(workRoot, recursive: true); } catch { /* best effort cleanup */ }
}

Console.WriteLine();
Console.WriteLine($"== {passed} passed, {failures.Count} failed ==");
if (failures.Count > 0)
{
    Console.WriteLine("Failures:");
    foreach (var f in failures) Console.WriteLine($"  - {f}");
    Environment.Exit(1);
}
