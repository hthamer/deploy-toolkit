using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeployToolkit.Core.Backup;
using DeployToolkit.Core.Config;
using DeployToolkit.Core.Deployment;
using DeployToolkit.Core.IisControl;
using DeployToolkit.Core.Logging;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Publishing;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Secrets;
using DeployToolkit.Core.Targets;
using DeployToolkit.Core.Targets.AzureAppService;

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

var workRoot = Path.Combine(Path.GetTempPath(), "DeployToolkitSelfTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workRoot);

try
{
    // ---------------------------------------------------------------
    Console.WriteLine("== ManifestHasher ==");
    var publishV1 = Path.Combine(workRoot, "publish-v1");
    Directory.CreateDirectory(Path.Combine(publishV1, "bin"));
    File.WriteAllText(Path.Combine(publishV1, "bin", "App.dll"), "fake-dll-content-v1");
    File.WriteAllText(Path.Combine(publishV1, "appsettings.json"), "{}");

    var hashedV1 = ManifestHasher.HashFolder(publishV1);
    Check("hashes every file", hashedV1.Count == 2);
    Check("paths use forward slashes", hashedV1.Any(f => f.Path == "bin/App.dll"));
    Check("hash format is sha256:<hex>", hashedV1.All(f => f.Hash.StartsWith("sha256:") && f.Hash.Length == 71));

    var rehash = ManifestHasher.HashFile(Path.Combine(publishV1, "bin", "App.dll"));
    Check("hashing is deterministic", rehash == hashedV1.First(f => f.Path == "bin/App.dll").Hash);

    // ---------------------------------------------------------------
    Console.WriteLine("== ManifestDiffEngine ==");
    var publishV2 = Path.Combine(workRoot, "publish-v2");
    Directory.CreateDirectory(Path.Combine(publishV2, "bin"));
    File.WriteAllText(Path.Combine(publishV2, "bin", "App.dll"), "fake-dll-content-v2"); // changed
    File.WriteAllText(Path.Combine(publishV2, "appsettings.json"), "{}");                 // unchanged
    File.WriteAllText(Path.Combine(publishV2, "bin", "NewLib.dll"), "brand-new");         // new
    // note: appsettings.json unchanged, App.dll changed, NewLib.dll new, nothing deleted from v1->v2

    var hashedV2 = ManifestHasher.HashFolder(publishV2);
    var diff = ManifestDiffEngine.Diff(hashedV2, hashedV1);

    Check("changed file detected", diff.ChangedOrNewFiles.Any(f => f.Path == "bin/App.dll"));
    Check("new file detected", diff.ChangedOrNewFiles.Any(f => f.Path == "bin/NewLib.dll"));
    Check("unchanged file excluded from delta", diff.ChangedOrNewFiles.All(f => f.Path != "appsettings.json"));
    Check("delta count is exactly 2 (changed + new)", diff.ChangedOrNewFiles.Count == 2);

    var hashedV3WithDeletion = hashedV2.Where(f => f.Path != "bin/NewLib.dll").ToList();
    var diffWithDeletion = ManifestDiffEngine.Diff(hashedV3WithDeletion, hashedV2);
    Check("deleted file detected", diffWithDeletion.DeletedFiles.Contains("bin/NewLib.dll"));

    var firstEverDiff = ManifestDiffEngine.Diff(hashedV1, baselineFiles: null);
    Check("first-ever release treats everything as new", firstEverDiff.ChangedOrNewFiles.Count == hashedV1.Count);

    // ---------------------------------------------------------------
    Console.WriteLine("== Package round-trip (write -> read -> verify -> extract) ==");
    var manifest = new ComponentManifest
    {
        ComponentId = "comp-1",
        Client = "ClientA",
        Component = "CMS",
        Version = "1.4.0",
        CreatedUtc = DateTimeOffset.UtcNow,
        GitCommitSha = "abc123",
        TargetFramework = "net8.0",
        IsSelfContained = false,
        Files = diff.ChangedOrNewFiles,
        AppSettingsDelta = new Dictionary<string, object?>
        {
            ["Smtp:Host"] = "smtp.newhost.com",
            ["Feature:NewToggle"] = true,
        },
        HealthCheckUrl = "https://clienta.example.com/health",
    };

    var zipPath = Path.Combine(workRoot, "delta.zip");
    PackageWriter.Write(manifest, publishV2, diff.ChangedOrNewFiles, outputZipPath: zipPath);
    Check("package zip was created", File.Exists(zipPath));

    var readBack = PackageReader.ReadManifest(zipPath);
    Check("manifest round-trips: version", readBack.Version == "1.4.0");
    Check("manifest round-trips: file count", readBack.Files.Count == diff.ChangedOrNewFiles.Count);
    Check("manifest round-trips: appsettings delta", readBack.AppSettingsDelta["Smtp:Host"]?.ToString() == "smtp.newhost.com");

    var integrity = PackageReader.VerifyIntegrity(zipPath);
    Check("integrity check passes on an untampered package", integrity.IsValid);

    var extractRoot = Path.Combine(workRoot, "extracted");
    var extracted = PackageReader.ExtractFiles(zipPath, extractRoot);
    Check("extracted expected number of files", extracted.Count == diff.ChangedOrNewFiles.Count);
    Check("extracted file content matches source",
        File.ReadAllText(Path.Combine(extractRoot, "bin", "App.dll")) == "fake-dll-content-v2");

    // ---------------------------------------------------------------
    Console.WriteLine("== Integrity check catches tampering ==");
    var tamperedZip = Path.Combine(workRoot, "delta-tampered.zip");
    File.Copy(zipPath, tamperedZip);
    using (var archive = System.IO.Compression.ZipFile.Open(tamperedZip, System.IO.Compression.ZipArchiveMode.Update))
    {
        var entry = archive.GetEntry("files/bin/App.dll")!;
        using var s = entry.Open();
        s.SetLength(0);
        using var w = new StreamWriter(s);
        w.Write("corrupted-content");
    }
    var tamperedResult = PackageReader.VerifyIntegrity(tamperedZip);
    Check("tampered package fails integrity check", !tamperedResult.IsValid);
    Check("tampered package reports the specific file", tamperedResult.Problems.Any(p => p.Contains("bin/App.dll")));

    // ---------------------------------------------------------------
    Console.WriteLine("== AppSettingsMerger ==");
    var existingAppSettings = """
    {
      "ConnectionStrings": { "Default": "Server=prod;Database=Real;..." },
      "Smtp": { "Host": "old-smtp.example.com", "Port": 587 },
      "AllowedHosts": "*"
    }
    """;

    var delta = new Dictionary<string, object?>
    {
        ["Smtp:Host"] = "smtp.newhost.com",
        ["Feature:NewToggle"] = true,
    };

    var preview = AppSettingsMerger.Preview(existingAppSettings, delta);
    Check("preview reports exactly the changed/new keys", preview.Count == 2);
    Check("preview shows old value for existing key", preview.First(c => c.DottedKey == "Smtp:Host").OldValue?.ToString() == "old-smtp.example.com");
    Check("preview flags brand-new key correctly", preview.First(c => c.DottedKey == "Feature:NewToggle").IsNewKey);

    var merged = AppSettingsMerger.Apply(existingAppSettings, delta);
    Check("merge preserves untouched connection string", merged.Contains("Server=prod;Database=Real"));
    Check("merge preserves untouched Smtp:Port", merged.Contains("587"));
    Check("merge applies new Smtp:Host", merged.Contains("smtp.newhost.com"));
    Check("merge creates new nested Feature:NewToggle", merged.Contains("\"Feature\"") && merged.Contains("\"NewToggle\": true"));

    var noopDelta = new Dictionary<string, object?> { ["Smtp:Port"] = 587 };
    var noopPreview = AppSettingsMerger.Preview(existingAppSettings, noopDelta);
    Check("no-op delta produces zero reported changes", noopPreview.Count == 0);

    // ---------------------------------------------------------------
    Console.WriteLine("== BackupManager ==");
    var siteRoot = Path.Combine(workRoot, "site");
    Directory.CreateDirectory(Path.Combine(siteRoot, "bin"));
    File.WriteAllText(Path.Combine(siteRoot, "bin", "App.dll"), "content-before-deploy");

    var backupsRoot = Path.Combine(workRoot, "Backups");
    var backupMgr = new BackupManager(backupsRoot);
    var fixedNow = new DateTimeOffset(2026, 8, 30, 15, 12, 0, TimeSpan.Zero);
    var backupFolder = backupMgr.Backup("ClientA", "CMS", siteRoot, new[] { "bin/App.dll" }, fixedNow);

    Check("backup lands under yyyyMMdd folder", backupFolder.Contains(Path.Combine(backupsRoot, "20260830")));
    Check("backup zip was written", File.Exists(Path.Combine(backupFolder, "files.zip")));
    Check("backup manifest was written", File.Exists(Path.Combine(backupFolder, "backup-manifest.json")));

    // simulate a deploy overwriting the file, then roll back
    File.WriteAllText(Path.Combine(siteRoot, "bin", "App.dll"), "content-after-bad-deploy");
    backupMgr.Rollback(backupFolder);
    Check("rollback restores original content", File.ReadAllText(Path.Combine(siteRoot, "bin", "App.dll")) == "content-before-deploy");

    // ---------------------------------------------------------------
    Console.WriteLine("== LocalFileRegistryStore + PackageBuilder: baseline & stale-package scenario ==");
    var registryRoot = Path.Combine(workRoot, "registry");
    var registry = new LocalFileRegistryStore(registryRoot);
    var mappingStore = new JsonFileProjectMappingStore(Path.Combine(workRoot, "project-mappings.json"));
    var builder = new PackageBuilder(registry, mappingStore);

    var projectFolder = Path.Combine(workRoot, "projects", "ClientA-CMS");
    Directory.CreateDirectory(projectFolder);

    var resolvedBeforeRegistration = false;
    try
    {
        await builder.ResolveComponentForFolderAsync(projectFolder);
        resolvedBeforeRegistration = true;
    }
    catch (ComponentNotResolvedException) { /* expected */ }
    Check("unresolved folder throws ComponentNotResolvedException", !resolvedBeforeRegistration);

    var component = await builder.CreateClientAndComponentAsync(
        projectFolder, "ClientA", "CMS", TargetType.IisLocal, "net8.0", isSelfContained: false,
        healthCheckUrl: "http://127.0.0.1:59321/health");
    Check("component was created", component.ComponentId is not null);

    var resolvedAgain = await builder.ResolveComponentForFolderAsync(projectFolder);
    Check("second selection of the same folder auto-resolves the same component", resolvedAgain.ComponentId == component.ComponentId);

    // --- Package A: built, never deployed ---
    var publishA = Path.Combine(workRoot, "scenario", "publish-A");
    Directory.CreateDirectory(Path.Combine(publishA, "bin"));
    File.WriteAllText(Path.Combine(publishA, "bin", "App.dll"), "version-A-content");

    var resultA = await builder.BuildAsync(new PackageBuildRequest(
        component.ComponentId, "1.2.0", publishA, Path.Combine(workRoot, "scenario", "A.zip")));
    Check("package A created with 0 stale packages beforehand", resultA.UnresolvedStalePackages.Count == 0);
    Check("package A treats first release as all-new", resultA.Manifest.Files.Count == 1);
    // Deliberately NOT marking A as deployed — it represents "built, then abandoned".

    // --- Package B: built later, WILL be deployed ---
    var publishB = Path.Combine(workRoot, "scenario", "publish-B");
    Directory.CreateDirectory(Path.Combine(publishB, "bin"));
    File.WriteAllText(Path.Combine(publishB, "bin", "App.dll"), "version-B-content"); // changed from A

    var resultB = await builder.BuildAsync(new PackageBuildRequest(
        component.ComponentId, "1.3.0", publishB, Path.Combine(workRoot, "scenario", "B.zip")));
    Check("package B sees package A as the one stale/undeployed package", resultB.UnresolvedStalePackages.Count == 1 && resultB.UnresolvedStalePackages[0].Version == "1.2.0");
    // B still diffs against "nothing deployed yet", same as A did, since A was never marked deployed.
    Check("package B still treats everything as new (A was never deployed, so no baseline exists)", resultB.Manifest.Files.Count == 1);

    await registry.MarkDeployedAsync(resultB.Record.PackageId, "hassan", DateTimeOffset.UtcNow);
    await registry.MarkStatusAsync(resultA.Record.PackageId, PackageStatus.Abandoned);

    // --- Package C: built weeks later, must diff against B (the last DEPLOYED), never A ---
    var publishC = Path.Combine(workRoot, "scenario", "publish-C");
    Directory.CreateDirectory(Path.Combine(publishC, "bin"));
    File.WriteAllText(Path.Combine(publishC, "bin", "App.dll"), "version-B-content"); // unchanged from B
    File.WriteAllText(Path.Combine(publishC, "bin", "NewFeature.dll"), "new-in-C");    // new in C

    var resultC = await builder.BuildAsync(new PackageBuildRequest(
        component.ComponentId, "1.4.0", publishC, Path.Combine(workRoot, "scenario", "C.zip")));
    Check("package C sees zero stale packages (A was abandoned)", resultC.UnresolvedStalePackages.Count == 0);
    Check("package C's baseline is B, not A", resultC.Manifest.BaselineManifest == resultB.Record.PackageId);
    Check("package C correctly excludes the file unchanged since B", resultC.Manifest.Files.All(f => f.Path != "bin/App.dll"));
    Check("package C correctly includes only the genuinely new file", resultC.Manifest.Files.Count == 1 && resultC.Manifest.Files[0].Path == "bin/NewFeature.dll");

    // ---------------------------------------------------------------
    Console.WriteLine("== DeploymentOrchestrator: full run with real health check + rollback on failure ==");

    // A tiny local HTTP server standing in for the site's health endpoint —
    // this lets us test the real HttpClient-based health check path without
    // needing external network access.
    var healthShouldPass = true;
    using var listener = new HttpListener();
    listener.Prefixes.Add("http://127.0.0.1:59321/");
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

    // --- Happy path: health check passes -> package marked Deployed ---
    healthShouldPass = true;
    var runOk = await orchestrator.RunAsync(
        new DeploymentRunRequest(
            resultC.ZipPath, deploySiteRoot, appSettingsPath, "ClientA", "CMS", "hassan", hooks),
        resultC.Record.PackageId);

    Check("successful run reports Success", runOk.Success);
    Check("successful run did not roll back", !runOk.RolledBack);
    Check("stop then start were both called", stopStartLog.SequenceEqual(new[] { "stop", "start" }));
    Check("file was actually deployed", File.ReadAllText(Path.Combine(deploySiteRoot, "bin", "NewFeature.dll")) == "new-in-C");

    var latestDeployed = await registry.GetLatestDeployedPackageAsync(component.ComponentId);
    Check("registry now reports package C as the latest deployed package", latestDeployed?.PackageId == resultC.Record.PackageId);

    // --- Failure path: health check fails -> automatic rollback, package NOT marked deployed ---
    var publishD = Path.Combine(workRoot, "scenario", "publish-D");
    Directory.CreateDirectory(Path.Combine(publishD, "bin"));
    File.WriteAllText(Path.Combine(publishD, "bin", "NewFeature.dll"), "BROKEN-version-D");

    var resultD = await builder.BuildAsync(new PackageBuildRequest(
        component.ComponentId, "1.5.0", publishD, Path.Combine(workRoot, "scenario", "D.zip")));

    stopStartLog.Clear();
    healthShouldPass = false; // simulate the new deploy breaking the site
    var runFailed = await orchestrator.RunAsync(
        new DeploymentRunRequest(
            resultD.ZipPath, deploySiteRoot, appSettingsPath, "ClientA", "CMS", "hassan", hooks),
        resultD.Record.PackageId);

    Check("failed health check reports Success = false", !runFailed.Success);
    Check("failed health check triggers rollback", runFailed.RolledBack);
    Check("file content was restored to pre-deploy (C's content), not left as broken D", File.ReadAllText(Path.Combine(deploySiteRoot, "bin", "NewFeature.dll")) == "new-in-C");

    var stillLatestDeployed = await registry.GetLatestDeployedPackageAsync(component.ComponentId);
    Check("package D was NOT marked deployed after rollback", stillLatestDeployed?.PackageId == resultC.Record.PackageId);

    var undeployedAfterD = await registry.GetUndeployedPackagesAsync(component.ComponentId);
    Check("package D remains in Created status, available as a future stale-package warning", undeployedAfterD.Any(p => p.PackageId == resultD.Record.PackageId));

    // ---------------------------------------------------------------
    Console.WriteLine("== RunLogger (JSON-lines, plan §8.6) ==");
    var logRoot = Path.Combine(workRoot, "logs");
    var loggedEntries = new List<RunLogEntry>();
    using (var logger = new RunLogger(logRoot, "ClientA", "CMS/backup"))
    {
        logger.EntryLogged += e => loggedEntries.Add(e);
        logger.Info("Deployment starting");
        logger.Warn("appsettings delta contains 1 key");
        logger.Error("simulated failure detail");
    }
    Check("log file created under sanitized client/component path", Directory.Exists(Path.Combine(logRoot, "ClientA", "CMS_backup")));
    var logPath = Directory.GetFiles(Path.Combine(logRoot, "ClientA", "CMS_backup")).Single();
    var parsed = RunLogger.ReadLog(logPath);
    Check("three entries written and parseable", parsed.Count == 3);
    Check("levels preserved", parsed.Select(e => e.Level).SequenceEqual(new[] { "INFO", "WARN", "ERROR" }));
    Check("entries are JSON objects with ts/level/msg", parsed.All(e => e.TimestampUtc != default));
    Check("EntryLogged fired for every entry", loggedEntries.Count == 3);

    // ---------------------------------------------------------------
    Console.WriteLine("== PackageReader.ReadEntryText ==");
    var previewZip = Path.Combine(workRoot, "preview.zip");
    var previewManifest = new ComponentManifest
    {
        ComponentId = "comp-preview",
        Client = "ClientA",
        Component = "CMS",
        Version = "0.0.1",
        CreatedUtc = DateTimeOffset.UtcNow,
        TargetFramework = "net8.0",
        Files = Array.Empty<ManifestFile>(),
        DbScripts = new[] { new DbScriptRef("001_add_index.sql", DbScriptKind.Schema) },
    };
    var previewDbDir = Path.Combine(workRoot, "preview-db");
    Directory.CreateDirectory(previewDbDir);
    File.WriteAllText(Path.Combine(previewDbDir, "001_add_index.sql"), "CREATE INDEX IX_Test ON dbo.Test (Id);\nGO\n");
    PackageWriter.Write(previewManifest, publishOutputRoot: workRoot, filesToInclude: Array.Empty<ManifestFile>(),
        dbScriptSourcePaths: new Dictionary<string, string> { ["001_add_index.sql"] = Path.Combine(previewDbDir, "001_add_index.sql") },
        outputZipPath: previewZip);
    Check("db script readable straight from the zip (no extraction)",
        PackageReader.ReadEntryText(previewZip, "db/001_add_index.sql").Contains("CREATE INDEX"));
    try
    {
        PackageReader.ReadEntryText(previewZip, "db/nope.sql");
        Check("missing entry throws FileNotFoundException", false);
    }
    catch (FileNotFoundException) { Check("missing entry throws FileNotFoundException", true); }

    // ---------------------------------------------------------------
    Console.WriteLine("== Offline reconciliation (plan §2.2 / §9 offline fallback) ==");
    var offlineRegistryRoot = Path.Combine(workRoot, "offline-registry");
    var offlineResults = Path.Combine(workRoot, "offline-results");
    var offlineRegistry = new LocalFileRegistryStore(offlineRegistryRoot);
    var offlineClient = await offlineRegistry.CreateClientAsync("ClientOffline");
    var offlineComponent = await offlineRegistry.CreateComponentAsync(new DeploymentComponent
    {
        ComponentId = Guid.NewGuid().ToString("N"),
        ClientId = offlineClient.ClientId,
        Name = "CMS",
        TargetType = TargetType.IisLocal,
        TargetFramework = "net8.0",
    });

    var offlineManifest = new ComponentManifest
    {
        ComponentId = offlineComponent.ComponentId,
        Client = "ClientOffline",
        Component = "CMS",
        Version = "2.0.0",
        CreatedUtc = DateTimeOffset.UtcNow,
        TargetFramework = "net8.0",
    };
    var pkgSuccess = await offlineRegistry.CreatePackageAsync(offlineComponent.ComponentId, offlineManifest);
    var pkgFailed = await offlineRegistry.CreatePackageAsync(offlineComponent.ComponentId, offlineManifest);

    // Simulate what the Deployer wrote while the registry was unreachable.
    await OfflineResultWriter.WriteAsync(offlineResults, new OfflineRunResult(
        OfflineRunResult.CurrentSchemaVersion, pkgSuccess.PackageId, offlineComponent.ComponentId,
        "ClientOffline", "CMS",
        Result: "Success", HealthCheckResult: true, Message: "ok", DeployedBy: "hassan",
        StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-5), CompletedUtc: DateTimeOffset.UtcNow,
        LogLines: new[] { "10:00:00 stop", "10:00:05 deployed", "10:00:09 health ok" }));
    await OfflineResultWriter.WriteAsync(offlineResults, new OfflineRunResult(
        OfflineRunResult.CurrentSchemaVersion, pkgFailed.PackageId, offlineComponent.ComponentId,
        "ClientOffline", "CMS",
        Result: "Failed", HealthCheckResult: false, Message: "boom", DeployedBy: "hassan",
        StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-4), CompletedUtc: DateTimeOffset.UtcNow,
        LogLines: new[] { "10:01:00 stop", "10:01:03 exception" }));
    File.WriteAllText(Path.Combine(offlineResults, "garbage.offline-result.json"), "{ not json");

    var reconciler = new OfflineReconciler(offlineRegistry);
    var report1 = await reconciler.ReconcileAsync(offlineResults);
    Check("two results reconciled", report1.Reconciled == 2);
    Check("corrupt file reported as an error", report1.Errors.Count == 1 && report1.Errors[0].Contains("garbage"));
    var latestAfterOffline = await offlineRegistry.GetLatestDeployedPackageAsync(offlineComponent.ComponentId);
    Check("successful offline run flipped its package to Deployed", latestAfterOffline?.PackageId == pkgSuccess.PackageId);
    var undeployedAfterOffline = await offlineRegistry.GetUndeployedPackagesAsync(offlineComponent.ComponentId);
    Check("failed offline run left its package Created (redeployable)",
        undeployedAfterOffline.Any(p => p.PackageId == pkgFailed.PackageId));
    Check("deploy log written alongside the result",
        File.Exists(Path.Combine(offlineResults, pkgSuccess.PackageId + ".deploy.log")));

    var report2 = await reconciler.ReconcileAsync(offlineResults);
    Check("reconciliation is idempotent (markers)", report2.Reconciled == 0 && report2.Skipped == 2 && report2.Errors.Count == 1);
    foreach (var marker in Directory.GetFiles(offlineResults, "*.reconciled"))
        File.Delete(marker);
    var report3 = await reconciler.ReconcileAsync(offlineResults);
    Check("already-Deployed package skipped even without marker (registry state wins)",
        report3.Reconciled == 1 /* the failed one is replayed */ && report3.Skipped == 1 && report3.Errors.Count == 1);

    // ---------------------------------------------------------------
    Console.WriteLine("== Secrets: AesGcmSecretProtector + SecretVault (plan §11) ==");
    var protector = AesGcmSecretProtector.CreateWithPassphrase("correct horse battery staple");
    var secretCipher = protector.Protect("Server=tcp:client-a.database.windows.net;User=deployer;", "db:ClientA/CMS");
    Check("ciphertext does not leak the plaintext", !secretCipher.Contains("client-a", StringComparison.Ordinal));
    Check("payload starts with the DTSEC1 magic",
        Convert.FromBase64String(secretCipher).AsSpan()[..6].SequenceEqual("DTSEC1"u8));
    Check("round-trips with the same passphrase",
        protector.Unprotect(secretCipher, "db:ClientA/CMS") == "Server=tcp:client-a.database.windows.net;User=deployer;");

    // A fresh process/machine only knows the passphrase — payload must be
    // self-describing (salt + iterations inside), no stored state needed.
    var freshInstance = AesGcmSecretProtector.CreateWithPassphrase("correct horse battery staple");
    Check("fresh instance with same passphrase decrypts (self-describing payload)",
        freshInstance.Unprotect(secretCipher, "db:ClientA/CMS").StartsWith("Server=tcp:"));
    try
    {
        AesGcmSecretProtector.CreateWithPassphrase("wrong passphrase").Unprotect(secretCipher, "db:ClientA/CMS");
        Check("wrong passphrase fails", false);
    }
    catch (CryptographicException) { Check("wrong passphrase fails", true); }
    try
    {
        protector.Unprotect(secretCipher, "azure:publish");
        Check("wrong purpose fails (AAD binding)", false);
    }
    catch (CryptographicException) { Check("wrong purpose fails (AAD binding)", true); }

    var keyProtector = AesGcmSecretProtector.CreateWithKeyFile(Path.Combine(workRoot, "secret.key"));
    Check("key file created with exactly 32 bytes", new FileInfo(Path.Combine(workRoot, "secret.key")).Length == 32);
    Check("key-file mode round-trips",
        keyProtector.Unprotect(keyProtector.Protect("plesk-password-123", "plesk"), "plesk") == "plesk-password-123");
    Check("reloading the key file decrypts old payloads",
        AesGcmSecretProtector.CreateWithKeyFile(Path.Combine(workRoot, "secret.key"))
            .Unprotect(keyProtector.Protect("plesk-password-123", "plesk"), "plesk") == "plesk-password-123");

    var vaultPath = Path.Combine(workRoot, "core-secrets.vault.json");
    var vault = new SecretVault(vaultPath, keyProtector);
    vault.SetSecret("ClientA/CMS/db", "Server=.;Database=Cms;");
    vault.SetSecret("ClientA/CMS/azure-publish", "user$publisher");
    Check("vault lists both entries", vault.ListSecrets().Count == 2);
    Check("vault round-trips a secret", vault.GetSecret("ClientA/CMS/db") == "Server=.;Database=Cms;");
    Check("vault ref format parses back", SecretVault.TryParseRef(SecretVault.SecretRefFor("ClientA/CMS/db"), out var refName) && refName == "ClientA/CMS/db");
    Check("non-vault refs are rejected", !SecretVault.TryParseRef("https://kv.vault.azure.net/secrets/x", out _));

    // Deterministic tamper: flip one character inside the stored ciphertext
    // (JSON stays valid, the AES-GCM auth tag no longer matches).
    var vaultNode = JsonNode.Parse(File.ReadAllText(vaultPath))!;
    var cipherNode = vaultNode["Entries"]!["ClientA/CMS/db"]!["Ciphertext"]!;
    var cipherText = cipherNode.GetValue<string>();
    cipherNode.ReplaceWith(JsonValue.Create(cipherText[0] == 'A' ? 'B' + cipherText[1..] : 'A' + cipherText[1..]));
    File.WriteAllText(vaultPath, vaultNode.ToJsonString());
    try
    {
        vault.GetSecret("ClientA/CMS/db");
        Check("tampered ciphertext fails loudly", false);
    }
    catch (CryptographicException) { Check("tampered ciphertext fails loudly", true); }
    Check("vault delete removes the entry", vault.DeleteSecret("ClientA/CMS/azure-publish"));

    // ---------------------------------------------------------------
    Console.WriteLine("== IIS control abstraction (plan §6, headless) ==");
    // Local helper: DeploymentComponent is a class (init-only props), so
    // build variants explicitly instead of `with`.
    static DeploymentComponent Variant(DeploymentComponent c, string? site = null, string? app = null) =>
        new()
        {
            ComponentId = c.ComponentId,
            ClientId = c.ClientId,
            Name = c.Name,
            TargetType = c.TargetType,
            TargetFramework = c.TargetFramework,
            IisSiteName = site,
            IisAppPath = app,
        };

    var iisController = new FakeIisController();
    var iisMappingStore = new IisTargetMappingStore(Path.Combine(workRoot, "iis-targets.json"));
    var resolver = new IisTargetResolver(iisController, iisMappingStore);

    var iisComponent = new DeploymentComponent
    {
        ComponentId = "comp-iis-1",
        ClientId = "client-1",
        Name = "CMS",
        TargetType = TargetType.IisLocal,
        TargetFramework = "net8.0",
        IisSiteName = "ClientA",
        IisAppPath = "/cms",
    };
    var resolved = resolver.Resolve(iisComponent);
    Check("component config resolves to the live application", resolved.Resolved && resolved.Target!.AppPath == "/cms");
    Check("physical path + app pool come from live IIS data",
        resolved.Target!.PhysicalPath == @"C:\sites\ClientA\cms" && resolved.Target.AppPoolName == "cms-pool");

    var resolvedWithTrailingSlash = resolver.Resolve(Variant(iisComponent, site: "ClientA", app: "/cms/"));
    Check("app path normalization tolerates trailing slash", resolvedWithTrailingSlash.Resolved);

    var wrongApp = resolver.Resolve(Variant(iisComponent, app: "/wrong"));
    Check("unknown app path is unresolved with candidates for the picker",
        !wrongApp.Resolved && wrongApp.Candidates.Count == 3);

    var noSite = resolver.Resolve(Variant(iisComponent, site: null, app: null));
    Check("component without site config enumerates all apps as candidates",
        !noSite.Resolved && noSite.Candidates.Count == 3);

    resolver.SaveMapping(iisComponent.ComponentId, resolved.Target!);
    var remapped = resolver.Resolve(Variant(iisComponent, site: "OldSite", app: "/old"));
    Check("machine-local mapping overrides component config and stays live-verified",
        remapped.Resolved && remapped.Target!.SiteName == "ClientA");

    Console.WriteLine("== IIS stop strategy: app-pool first, app_offline fallback ==");
    var stopRoot = Path.Combine(workRoot, "stop-root");
    Directory.CreateDirectory(stopRoot);
    var stopper = new IisSiteStopController(iisController, "cms-pool", stopRoot);
    var stopOutcome = stopper.Stop();
    Check("healthy account: pool stopped, no app_offline",
        !stopOutcome.UsedAppOffline && stopper.Start().UsedAppOffline == false && iisController.StoppedPools.Single() == "cms-pool");

    var restricted = new FakeIisController { ThrowOnPoolOps = true };
    var restrictedStopper = new IisSiteStopController(restricted, "cms-pool", stopRoot);
    var fallbackOutcome = restrictedStopper.Stop();
    Check("account without IIS rights: app_offline.htm dropped",
        fallbackOutcome.UsedAppOffline && AppOfflineManager.IsDropped(stopRoot));
    var startOutcome = restrictedStopper.Start();
    Check("start removes app_offline.htm (site back online)",
        startOutcome.UsedAppOffline && !AppOfflineManager.IsDropped(stopRoot));

    var offlineOnly = new IisSiteStopController(restricted, appPoolName: null, stopRoot, IisStopStrategy.AppOffline);
    Check("AppOffline strategy needs no app pool at all",
        offlineOnly.Stop().UsedAppOffline && AppOfflineManager.IsDropped(stopRoot));

    var poolWithoutName = new IisSiteStopController(iisController, appPoolName: null, stopRoot, IisStopStrategy.AppPool);
    try
    {
        poolWithoutName.Stop();
        Check("AppPool strategy without pool name fails fast", false);
    }
    catch (InvalidOperationException) { Check("AppPool strategy without pool name fails fast", true); }

    // ---------------------------------------------------------------
    Console.WriteLine("== DotNetPublisher (Phase 4 engine piece) ==");
    var dotnetPath = DotNetPublisher.ResolveDotNetExecutable();
    Check("dotnet executable located", dotnetPath is not null && File.Exists(dotnetPath!));

    var pubSettings1 = new PublishSettings(
        ProjectPath: @"C:\repos\ClientA CMS\CMS.csproj",
        TargetFramework: "net8.0",
        SelfContained: false,
        OutputDirectory: @"C:\out\ClientA CMS");
    var args1 = DotNetPublisher.BuildArguments(pubSettings1);
    Check("arguments quote spaces in project path", args1.Contains("\"C:\\repos\\ClientA CMS\\CMS.csproj\""));
    Check("arguments include configuration + framework", args1.Contains("-c Release") && args1.Contains("-f net8.0"));
    Check("arguments include --self-contained false for framework-dependent", args1.Contains("--self-contained false"));
    Check("arguments quote the output directory", args1.Contains("\"C:\\out\\ClientA CMS\""));
    var args2 = DotNetPublisher.BuildArguments(pubSettings1 with { SelfContained = true, AdditionalArguments = "-p:PublishSingleFile=true" });
    Check("self-contained flag flips and extra args appended",
        args2.Contains("--self-contained true") && args2.EndsWith("-p:PublishSingleFile=true"));

    // Real publish — small, but proves process plumbing end to end.
    if (dotnetPath is not null)
    {
        var pubProj = Path.Combine(workRoot, "publish-probe", "ProbeApp");
        Directory.CreateDirectory(pubProj);
        File.WriteAllText(Path.Combine(pubProj, "ProbeApp.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net8.0</TargetFramework>\n    <Nullable>disable</Nullable>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(pubProj, "Program.cs"), "System.Console.WriteLine(\"probe\");\n");
        var pubOut = Path.Combine(workRoot, "publish-probe", "out");
        var publish = await DotNetPublisher.PublishAsync(
            new PublishSettings(pubProj, TargetFramework: "net8.0", SelfContained: false, OutputDirectory: pubOut),
            timeoutMinutes: 5);
        Check("real dotnet publish succeeded", publish.Success && !publish.TimedOut);
        Check("real publish produced the app dll", File.Exists(Path.Combine(pubOut, "ProbeApp.dll")));
        Check("publish output lines captured", publish.OutputLines.Any(l => l.Contains("ProbeApp", StringComparison.OrdinalIgnoreCase)));
        var failedPublish = await DotNetPublisher.PublishAsync(
            new PublishSettings(pubProj, TargetFramework: "net8.0", SelfContained: false,
                OutputDirectory: pubOut, AdditionalArguments: "-t:ThisTargetDoesNotExist"),
            timeoutMinutes: 5);
        Check("failing publish reports non-zero exit + error summary", !failedPublish.Success && failedPublish.ErrorSummary is not null);
    }

    // ---------------------------------------------------------------
    Console.WriteLine("== Azure App Service executor (plan §12, fake-handler tested) ==");
    var azHandler = new FakeHttpHandler();
    var kuduCreds = new KuduCredentials("clienta-cms", "$clienta-cms", "publish-password");
    var armTarget = new AzureTargetSettings("sub-1", "rg-clienta", "clienta-cms");

    // 1. Kudu wire format
    azHandler.EnqueueResponse(HttpStatusCode.OK, "{}");
    var kudu = new KuduClient(kuduCreds, new HttpClient(azHandler));
    var zipBytes = new MemoryStream();
    using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
    using (var entry = zip.CreateEntry("index.html").Open())
    using (var w = new StreamWriter(entry)) w.Write("hello");
    zipBytes.Position = 0;
    var kuduResult = await kudu.DeployZipAsync(zipBytes);
    var kuduRequest = azHandler.Requests.Single();
    Check("kudu posts to the SCM zipdeploy endpoint",
        kuduRequest.Method == HttpMethod.Post && kuduRequest.RequestUri!.ToString().StartsWith("https://clienta-cms.scm.azurewebsites.net/api/zipdeploy"));
    Check("kudu uses basic auth from publish credentials",
        kuduRequest.Headers.Authorization!.Scheme == "Basic" &&
        kuduRequest.Headers.Authorization.Parameter == Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("$clienta-cms:publish-password")));
    Check("kudu body is application/zip", kuduRequest.Content!.Headers.ContentType!.MediaType == "application/zip");
    Check("kudu 200 is success", kuduResult.Success);

    // 2. ARM appsettings: GET parse + PUT merge
    var armHandler = new FakeHttpHandler();
    armHandler.EnqueueResponse(HttpStatusCode.OK,
        "{\"properties\":{\"ExistingKey\":\"keep-me\",\"WwwRoot\":\"site\\\\wwwroot\"}}");
    armHandler.EnqueueResponse(HttpStatusCode.OK, "{\"properties\":{}}");
    var settingsClient = new AzureAppSettingsClient(ct => Task.FromResult<string?>("arm-token-123"), new HttpClient(armHandler));
    var current = await settingsClient.GetAppSettingsAsync(armTarget);
    Check("ARM GET parses the properties map", current["ExistingKey"] == "keep-me");
    var armMerged = AzureAppSettingsClient.MergeDelta(current, new Dictionary<string, object?>
    {
        ["Smtp:Host"] = "smtp.newhost.com",
        ["Feature:NewToggle"] = true,
        ["Retries"] = 5,
        ["ExistingKey"] = null, // null delta = remove
    });
    var putOk = await settingsClient.PutAppSettingsAsync(armTarget, armMerged);
    Check("ARM PUT succeeded with bearer token", putOk && armHandler.Requests.Last().Headers.Authorization!.Parameter == "arm-token-123");
    var putBody = System.Text.Encoding.UTF8.GetString(armHandler.Bodies.Last());
    Check("delta applied on top of existing settings (existing kept, bool/int stringified)",
        putBody.Contains("smtp.newhost.com") && putBody.Contains("true") && putBody.Contains("\"5\""));
    var putDoc = JsonDocument.Parse(putBody);
    Check("null delta key removed the entry",
        !putDoc.RootElement.GetProperty("properties").TryGetProperty("ExistingKey", out _));

    // 3. Executor end-to-end against the fakes
    var azRoot = Path.Combine(workRoot, "azure-publish");
    Directory.CreateDirectory(Path.Combine(azRoot, "bin"));
    File.WriteAllText(Path.Combine(azRoot, "bin", "App.dll"), "azure-dll");
    File.WriteAllText(Path.Combine(azRoot, "web.config"), "<configuration />");
    var azManifest = new ComponentManifest
    {
        ComponentId = "comp-azure",
        Client = "ClientA",
        Component = "CMS",
        Version = "1.0.0",
        CreatedUtc = DateTimeOffset.UtcNow,
        TargetFramework = "net8.0",
        Files = new ManifestFile[]
        {
            new("bin/App.dll", "sha256:x", 9),
            new("web.config", "sha256:y", 16),
        },
        AppSettingsDelta = new Dictionary<string, object?> { ["Smtp:Host"] = "smtp.newhost.com" },
    };

    var execHandler = new FakeHttpHandler();
    execHandler.EnqueueResponse(HttpStatusCode.OK, "{\"id\":\"dep-1\"}");                       // kudu
    execHandler.EnqueueResponse(HttpStatusCode.OK, "{\"properties\":{\"Old\":\"v\"}}");        // arm GET
    execHandler.EnqueueResponse(HttpStatusCode.OK, "{\"properties\":{}}");                     // arm PUT
    var executor = new AzureAppServiceExecutor(
        new KuduClient(kuduCreds, new HttpClient(execHandler)),
        new AzureAppSettingsClient(ct => Task.FromResult<string?>("t"), new HttpClient(execHandler)),
        armTarget);
    var azProgress = new List<string>();
    var azResult = await executor.DeployAsync(azManifest, azRoot, new CollectingProgress(azProgress), CancellationToken.None);
    Check("azure executor reports success", azResult.Success);
    Check("executor streamed per-step progress", azProgress.Any(p => p.Contains("Kudu")) && azProgress.Any(p => p.Contains("app setting")));

    var uploadedZip = new ZipArchive(new MemoryStream(execHandler.Bodies[0]));
    var uploadedEntries = uploadedZip.Entries.Select(e => e.FullName).ToHashSet();
    Check("uploaded zip contains exactly the manifest files (forward-slash names)",
        uploadedEntries.SetEquals(new[] { "bin/App.dll", "web.config" }));
    using (var entryStream = uploadedZip.GetEntry("bin/App.dll")!.Open())
    using (var reader = new StreamReader(entryStream))
        Check("uploaded content matches the local file", reader.ReadToEnd() == "azure-dll");
    var finalSettings = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(execHandler.Bodies[2])).RootElement.GetProperty("properties");
    Check("app settings PUT carries existing + delta", finalSettings.GetProperty("Old").GetString() == "v" && finalSettings.GetProperty("Smtp:Host").GetString() == "smtp.newhost.com");

    // 4. Missing local file fails loudly (no partial uploads)
    var brokenExecutor = new AzureAppServiceExecutor(new KuduClient(kuduCreds, new HttpClient(new FakeHttpHandler())));
    try
    {
        await brokenExecutor.DeployAsync(
            new ComponentManifest
            {
                ComponentId = "comp-azure",
                Client = "ClientA",
                Component = "CMS",
                Version = "1.0.0",
                CreatedUtc = DateTimeOffset.UtcNow,
                TargetFramework = "net8.0",
                Files = new ManifestFile[] { new("missing.dll", "sha256:z", 3) },
            },
            azRoot, null, CancellationToken.None);
        Check("missing package file fails loudly", false);
    }
    catch (FileNotFoundException) { Check("missing package file fails loudly", true); }

    // ---------------------------------------------------------------
    Console.WriteLine("== PublishConfiguration + client/package management (pre-WinForms feature) ==");

    var cfg = new PublishConfiguration
    {
        DeploymentType = PublishDeploymentType.SelfContained,
        TargetRuntime = "win-x64",
        AdditionalPublishOptions = "-p:PublishTrimmed=false --nologo",
    };
    var cfgJson = PublishConfigurationSerializer.Serialize(cfg);
    var cfgBack = PublishConfigurationSerializer.Parse(cfgJson);
    Check("publish configuration JSON round-trips", cfgBack is not null
        && cfgBack.DeploymentType == PublishDeploymentType.SelfContained
        && cfgBack.TargetRuntime == "win-x64"
        && cfgBack.AdditionalPublishOptions == "-p:PublishTrimmed=false --nologo");
    Check("publish configuration serializer handles nulls",
        PublishConfigurationSerializer.Serialize(null) is null && PublishConfigurationSerializer.Parse(null) is null);
    Check("publish configuration enums are written as readable strings", cfgJson!.Contains("SelfContained"));
    var malformedConfigRefused = false;
    try { PublishConfigurationSerializer.Parse("{ not json"); }
    catch (InvalidOperationException) { malformedConfigRefused = true; }
    Check("malformed publish configuration JSON fails with a clear error", malformedConfigRefused);

    var bridge = cfg.ToPublishSettings(@"C:\src\site");
    Check("client publish configuration maps onto DotNetPublisher settings",
        bridge.SelfContained && bridge.AdditionalArguments == "-r win-x64 -p:PublishTrimmed=false --nologo");
    var bridgeFd = new PublishConfiguration { DeploymentType = PublishDeploymentType.FrameworkDependent }.ToPublishSettings(@"C:\src\site");
    Check("framework-dependent default maps to SelfContained=false with no -r",
        !bridgeFd.SelfContained && string.IsNullOrEmpty(bridgeFd.AdditionalArguments));
    var badRidRefused = false;
    try { new PublishConfiguration { TargetRuntime = "win x64" }.ToPublishSettings("p"); }
    catch (ArgumentException) { badRidRefused = true; }
    Check("publish configuration refuses a RID containing spaces", badRidRefused);

    // Client-profile CRUD + package management against the file store — the
    // same rules the EF store enforces, so the offline fallback behaves
    // identically behind IRegistryStore.
    var mgmtRegistry = new LocalFileRegistryStore(Path.Combine(workRoot, "mgmt-registry"));
    var mgmtClient = await mgmtRegistry.CreateClientAsync("  ClientMgmt  ");
    Check("CreateClientAsync trims the client name", mgmtClient.Name == "ClientMgmt");

    mgmtClient.ContactPhone = "+966 55 987 6543";
    mgmtClient.ContactEmail = "ops@mgmt.example";
    mgmtClient.GitRepositoryUrl = "https://git.example.com/mgmt/site.git";
    mgmtClient.DeploymentBranch = "release";
    mgmtClient.PublishConfiguration = cfg;
    mgmtClient.HasAmc = false;
    mgmtClient.AmcExpiryDate = null;
    mgmtClient.InfrastructureManagedBy = ManagedBy.Client;
    mgmtClient.HostingAccountManagedBy = "Client — Mr. Saleh";
    await mgmtRegistry.UpdateClientAsync(mgmtClient);
    var mgmtReloaded = await mgmtRegistry.GetClientAsync(mgmtClient.ClientId);
    Check("file store round-trips the full client profile", mgmtReloaded is not null
        && mgmtReloaded.ContactPhone == "+966 55 987 6543"
        && mgmtReloaded.ContactEmail == "ops@mgmt.example"
        && mgmtReloaded.GitRepositoryUrl == "https://git.example.com/mgmt/site.git"
        && mgmtReloaded.DeploymentBranch == "release"
        && mgmtReloaded.PublishConfiguration?.DeploymentType == PublishDeploymentType.SelfContained
        && mgmtReloaded.HasAmc == false
        && mgmtReloaded.InfrastructureManagedBy == ManagedBy.Client
        && mgmtReloaded.HostingAccountManagedBy == "Client — Mr. Saleh");

    var duplicateCreateRefused = false;
    try { await mgmtRegistry.CreateClientAsync("clientmgmt"); }
    catch (InvalidOperationException) { duplicateCreateRefused = true; }
    Check("CreateClientAsync refuses duplicate (case-insensitive) names", duplicateCreateRefused);

    mgmtClient.ContactEmail = "no-at-sign";
    var profileValidationRefused = false;
    try { await mgmtRegistry.UpdateClientAsync(mgmtClient); }
    catch (ArgumentException) { profileValidationRefused = true; }
    Check("UpdateClientAsync validates the profile before persisting (file store)", profileValidationRefused);
    mgmtClient.ContactEmail = "ops@mgmt.example";

    var mgmtComponent = await mgmtRegistry.CreateComponentAsync(new DeploymentComponent
    {
        ComponentId = Guid.NewGuid().ToString("N"),
        ClientId = mgmtClient.ClientId,
        Name = "Portal",
        TargetType = TargetType.IisLocal,
        TargetFramework = "net8.0",
    });

    ComponentManifest MakeManifest(string version) => new()
    {
        ComponentId = mgmtComponent.ComponentId,
        Client = mgmtClient.Name,
        Component = "Portal",
        Version = version,
        CreatedUtc = DateTimeOffset.UtcNow,
        TargetFramework = "net8.0",
    };

    var p1 = await mgmtRegistry.CreatePackageAsync(mgmtComponent.ComponentId, MakeManifest("1.0.0"));
    var p2 = await mgmtRegistry.CreatePackageAsync(mgmtComponent.ComponentId, MakeManifest("1.1.0"));
    var p3 = await mgmtRegistry.CreatePackageAsync(mgmtComponent.ComponentId, MakeManifest("1.2.0"));
    await mgmtRegistry.MarkDeployedAsync(p2.PackageId, "hassan", DateTimeOffset.UtcNow);

    var mgmtGrid = await mgmtRegistry.GetPackagesForComponentAsync(mgmtComponent.ComponentId);
    Check("package grid is newest-first across all statuses",
        mgmtGrid.Count == 3 && mgmtGrid[0].Version == "1.2.0" && mgmtGrid[2].Version == "1.0.0");
    Check("flag-as-deployed marks exactly the chosen package",
        (await mgmtRegistry.GetPackageAsync(p2.PackageId))!.Status == PackageStatus.Deployed
        && (await mgmtRegistry.GetPackageAsync(p1.PackageId))!.Status == PackageStatus.Created);
    Check("latest-deployed baseline follows the flagged package",
        (await mgmtRegistry.GetLatestDeployedPackageAsync(mgmtComponent.ComponentId))?.PackageId == p2.PackageId);

    var mgmtRun = await mgmtRegistry.RecordRunStartAsync(p2.PackageId, DateTimeOffset.UtcNow);
    await mgmtRegistry.RecordRunCompleteAsync(mgmtRun.RunId, "Success", true, null);

    var fileDeleteGuarded = false;
    try { await mgmtRegistry.DeletePackageAsync(p2.PackageId); }
    catch (InvalidOperationException) { fileDeleteGuarded = true; }
    Check("file store refuses deleting a package with run history", fileDeleteGuarded);

    await mgmtRegistry.DeletePackageAsync(p2.PackageId, deleteRunHistory: true);
    var runsJsonAfter = await File.ReadAllTextAsync(
        Path.Combine(workRoot, "mgmt-registry", "runs", mgmtComponent.ComponentId + ".json"));
    Check("file store deleteRunHistory:true removes package + its run records",
        await mgmtRegistry.GetPackageAsync(p2.PackageId) is null && !runsJsonAfter.Contains(p2.PackageId));

    await mgmtRegistry.DeletePackageAsync(p1.PackageId);
    Check("file store deletes a run-less package without any flag",
        (await mgmtRegistry.GetPackagesForComponentAsync(mgmtComponent.ComponentId)).Count == 1);

    var fileClientDeleteGuarded = false;
    try { await mgmtRegistry.DeleteClientAsync(mgmtClient.ClientId); }
    catch (InvalidOperationException) { fileClientDeleteGuarded = true; }
    Check("file store refuses deleting a client that still has components", fileClientDeleteGuarded);

    var throwaway = await mgmtRegistry.CreateClientAsync("Throwaway");
    await mgmtRegistry.DeleteClientAsync(throwaway.ClientId);
    Check("file store deletes a component-less client",
        (await mgmtRegistry.GetAllClientsAsync()).All(c => c.ClientId != throwaway.ClientId));

    // ---------------------------------------------------------------
    Console.WriteLine("== PackageBuildRequest.ExcludedPaths: manual diff exclusions (plan §10 step 4) ==");
    var exRegistry = new LocalFileRegistryStore(Path.Combine(workRoot, "exclusion-registry"));
    var exBuilder = new PackageBuilder(
        exRegistry, new JsonFileProjectMappingStore(Path.Combine(workRoot, "exclusion-mappings.json")));
    var exComponent = await exBuilder.CreateClientAndComponentAsync(
        Path.Combine(workRoot, "exclusion-project"), "ClientX", "Widget", TargetType.IisLocal, "net8.0",
        isSelfContained: false);

    // Baseline release: three files, built and DEPLOYED so it becomes the diff baseline.
    var exPublishV1 = Path.Combine(workRoot, "exclusion", "publish-v1");
    Directory.CreateDirectory(exPublishV1);
    File.WriteAllText(Path.Combine(exPublishV1, "Keep.txt"), "keep-v1");
    File.WriteAllText(Path.Combine(exPublishV1, "Drop.txt"), "drop-v1");
    File.WriteAllText(Path.Combine(exPublishV1, "Gone.txt"), "gone-v1");

    var exBaseline = await exBuilder.BuildAsync(new PackageBuildRequest(
        exComponent.ComponentId, "1.0.0", exPublishV1, Path.Combine(workRoot, "exclusion", "v1.zip")));
    await exRegistry.MarkDeployedAsync(exBaseline.Record.PackageId, "tester", DateTimeOffset.UtcNow);

    // Next release: Keep.txt changed (stays in), Drop.txt changed but EXCLUDED,
    // Gone.txt deleted (deleted-file entry against the baseline manifest).
    var exPublishV2 = Path.Combine(workRoot, "exclusion", "publish-v2");
    Directory.CreateDirectory(exPublishV2);
    File.WriteAllText(Path.Combine(exPublishV2, "Keep.txt"), "keep-v2");
    File.WriteAllText(Path.Combine(exPublishV2, "Drop.txt"), "drop-v2");

    var exExcluded = await exBuilder.BuildAsync(new PackageBuildRequest(
        exComponent.ComponentId, "1.0.1", exPublishV2, Path.Combine(workRoot, "exclusion", "v2-excluded.zip"),
        ExcludedPaths: new[] { "/DROP.TXT" })); // leading slash + case: exercises the normalization rule

    Check("excluded file absent from manifest.Files (leading-slash + case-insensitive match)",
        exExcluded.Manifest.Files.All(f => !string.Equals(f.Path, "Drop.txt", StringComparison.OrdinalIgnoreCase))
        && exExcluded.Manifest.Files.Count == 1);

    var exPlain = await exBuilder.BuildAsync(new PackageBuildRequest(
        exComponent.ComponentId, "1.0.2", exPublishV2, Path.Combine(workRoot, "exclusion", "v2-plain.zip")));

    Check("non-excluded files still present, incl. the deleted-file entry from the deployed baseline",
        exExcluded.Manifest.Files.Any(f => f.Path == "Keep.txt")
        && exExcluded.Manifest.DeletedFiles.Contains("Gone.txt"));

    Check("ExcludedPaths=null behaves exactly as before (all changed files + deleted entry present)",
        exPlain.Manifest.Files.Count == 2
        && exPlain.Manifest.Files.Any(f => f.Path == "Keep.txt")
        && exPlain.Manifest.Files.Any(f => f.Path == "Drop.txt")
        && exPlain.Manifest.DeletedFiles.Contains("Gone.txt"));

    // ---------------------------------------------------------------
    Console.WriteLine("== ProjectTargetFrameworkReader (publish framework auto-detect) ==");
    var tfmRoot = Path.Combine(workRoot, "tfm");
    Directory.CreateDirectory(tfmRoot);

    var sdkSingle = Path.Combine(tfmRoot, "SdkSingle.csproj");
    File.WriteAllText(sdkSingle, """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net48</TargetFramework>
          </PropertyGroup>
        </Project>
        """);

    var sdkMulti = Path.Combine(tfmRoot, "SdkMulti.csproj");
    File.WriteAllText(sdkMulti, """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>net8.0-windows;net48</TargetFrameworks>
            <TargetFramework>net8.0-windows</TargetFramework>
          </PropertyGroup>
        </Project>
        """);

    var classic = Path.Combine(tfmRoot, "Classic.csproj");
    File.WriteAllText(classic, """
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
          </PropertyGroup>
        </Project>
        """);

    var noTfm = Path.Combine(tfmRoot, "NoTfm.csproj");
    File.WriteAllText(noTfm, "<Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>");

    Check("SDK-style single TargetFramework is read",
        ProjectTargetFrameworkReader.ReadTargetFrameworks(sdkSingle).SequenceEqual(["net48"]));

    Check("SDK-style TargetFrameworks list is read in order and deduped against TargetFramework",
        ProjectTargetFrameworkReader.ReadTargetFrameworks(sdkMulti).SequenceEqual(["net8.0-windows", "net48"]));

    Check("classic TargetFrameworkVersion v4.8 normalizes to net48",
        ProjectTargetFrameworkReader.ReadTargetFrameworks(classic).SequenceEqual(["net48"]));

    Check("project without any TFM yields an empty list",
        ProjectTargetFrameworkReader.ReadTargetFrameworks(noTfm).Count == 0);

    Check("unreadable/missing csproj yields an empty list (no throw)",
        ProjectTargetFrameworkReader.ReadTargetFrameworks(Path.Combine(tfmRoot, "missing.csproj")).Count == 0);

    Check("v4.7.2 normalizes to net472", ProjectTargetFrameworkReader.NormalizeFrameworkVersion("v4.7.2") == "net472");
    Check("v3.5 normalizes to net35", ProjectTargetFrameworkReader.NormalizeFrameworkVersion("v3.5") == "net35");
    Check("modern TFM passes through untouched", ProjectTargetFrameworkReader.NormalizeFrameworkVersion("net10.0") == "net10.0");

    // ---------------------------------------------------------------
    Console.WriteLine("== IRegistryStore.UpdateComponentAsync (publish settings write-back) ==");
    var updRegistry = new LocalFileRegistryStore(Path.Combine(workRoot, "update-registry"));
    var updClient = await updRegistry.CreateClientAsync("UpdateCo");
    var updComponent = await updRegistry.CreateComponentAsync(new DeploymentComponent
    {
        ComponentId = Guid.NewGuid().ToString("N"),
        ClientId = updClient.ClientId,
        Name = "Website",
        TargetType = TargetType.IisLocal,
        TargetFramework = "net10.0", // the stale value the user reported
        IsSelfContained = true,
    });

    var corrected = new DeploymentComponent
    {
        ComponentId = updComponent.ComponentId,
        ClientId = updComponent.ClientId,
        Name = updComponent.Name,
        TargetType = updComponent.TargetType,
        TargetFramework = "net48", // corrected by the publish step
        IsSelfContained = false,   // forced off for .NET Framework targets
        HealthCheckUrl = updComponent.HealthCheckUrl,
    };
    await updRegistry.UpdateComponentAsync(corrected);

    var reread = await updRegistry.GetComponentAsync(updComponent.ComponentId);
    Check("updated framework is persisted", reread?.TargetFramework == "net48");
    Check("updated self-contained flag is persisted", reread?.IsSelfContained == false);
    Check("untouched fields survive the update", reread is not null && reread.Name == "Website" && reread.ClientId == updClient.ClientId);
    Check("components-for-client reflects the update",
        (await updRegistry.GetComponentsForClientAsync(updClient.ClientId)).Single(c => c.ComponentId == updComponent.ComponentId).TargetFramework == "net48");

    var updateThrew = false;
    try
    {
        await updRegistry.UpdateComponentAsync(new DeploymentComponent
        {
            ComponentId = "does-not-exist",
            ClientId = updClient.ClientId,
            Name = "Ghost",
            TargetType = TargetType.IisLocal,
            TargetFramework = "net8.0",
        });
    }
    catch (InvalidOperationException)
    {
        updateThrew = true;
    }
    Check("updating an unknown component fails loudly", updateThrew);

    listenerCts.Cancel();
    listener.Stop();
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

/// <summary>In-memory IIS controller: three applications across two sites,
/// with an optional "no IIS management rights" mode for fallback tests.</summary>
internal sealed class FakeIisController : IIisController
{
    public List<string> StoppedPools { get; } = new();
    public List<string> StartedPools { get; } = new();
    public bool ThrowOnPoolOps { get; set; }

    private static readonly IisApplicationInfo[] Apps =
    {
        new("ClientA", "/", "clienta-pool", @"C:\sites\ClientA"),
        new("ClientA", "/cms", "cms-pool", @"C:\sites\ClientA\cms"),
        new("ClientB", "/", "clientb-pool", @"C:\sites\ClientB"),
    };

    public IReadOnlyList<IisSiteInfo> EnumerateSites() => new[]
    {
        new IisSiteInfo("ClientA", "clienta-pool", @"C:\sites\ClientA", true),
        new IisSiteInfo("ClientB", "clientb-pool", @"C:\sites\ClientB", true),
    };

    public IReadOnlyList<IisApplicationInfo> EnumerateApplications(string? siteName = null) =>
        string.IsNullOrEmpty(siteName)
            ? Apps
            : Apps.Where(a => string.Equals(a.SiteName, siteName, StringComparison.OrdinalIgnoreCase)).ToArray();

    public IisAppPoolInfo? GetAppPool(string appPoolName) => null;

    public void StopSite(string siteName) { }

    public void StartSite(string siteName) { }

    public void StopAppPool(string appPoolName)
    {
        if (ThrowOnPoolOps) throw new UnauthorizedAccessException("Access is denied (simulated non-admin account).");
        StoppedPools.Add(appPoolName);
    }

    public void StartAppPool(string appPoolName)
    {
        if (ThrowOnPoolOps) throw new UnauthorizedAccessException("Access is denied (simulated non-admin account).");
        StartedPools.Add(appPoolName);
    }

    public void RecycleAppPool(string appPoolName) { }
}

/// <summary>Simple collecting IProgress for assertions.</summary>
internal sealed class CollectingProgress(List<string> sink) : IProgress<string>
{
    public void Report(string value) => sink.Add(value);
}

/// <summary>Scripted HttpMessageHandler: records every request (method,
/// URI, headers, raw body) and returns queued responses in order.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<byte[]> Bodies { get; } = new();
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory) => _responses.Enqueue(factory);

    public void EnqueueResponse(HttpStatusCode statusCode, string content = "") =>
        Enqueue(_ => new HttpResponseMessage(statusCode) { Content = new StringContent(content) });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            using var ms = new MemoryStream();
            await request.Content.CopyToAsync(ms, cancellationToken);
            Bodies.Add(ms.ToArray());
        }
        else
        {
            Bodies.Add(Array.Empty<byte>());
        }
        return _responses.Count > 0
            ? _responses.Dequeue()(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
