using System.Net;
using System.Text;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Targets;
using DeployToolkit.Core.Targets.Plesk;

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

var workRoot = Path.Combine(Path.GetTempPath(), "DeployToolkitPleskSelfTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workRoot);

try
{
    // ---------------------------------------------------------------
    Console.WriteLine("== PleskDeployOptions validation ==");
    var validOptions = new PleskDeployOptions("/httpdocs");
    Check("valid absolute remote root constructs", validOptions.RemoteRootPath == "/httpdocs");

    var rejectedMissingSlash = false;
    try { _ = new PleskDeployOptions("httpdocs"); }
    catch (ArgumentException) { rejectedMissingSlash = true; }
    Check("RemoteRootPath without leading '/' is rejected at construction", rejectedMissingSlash);

    var rejectedEmpty = false;
    try { _ = new PleskDeployOptions(""); }
    catch (ArgumentException) { rejectedEmpty = true; }
    Check("empty RemoteRootPath is rejected at construction", rejectedEmpty);

    var rejectedAfterWith = false;
    try
    {
        var mutated = validOptions with { RemoteRootPath = "relative/path" };
        mutated.Validate();
    }
    catch (ArgumentException) { rejectedAfterWith = true; }
    Check("with-mutated RemoteRootPath is caught by Validate()", rejectedAfterWith);

    var missingApiSettings = false;
    try { new PleskDeployOptions("/httpdocs", PleskRestartMode.XmlApi).Validate(); }
    catch (InvalidOperationException) { missingApiSettings = true; }
    Check("RestartMode.XmlApi without API settings fails Validate()", missingApiSettings);

    var xmlApiOptions = new PleskDeployOptions(
        "/httpdocs", PleskRestartMode.XmlApi,
        XmlApiBaseUrl: "https://plesk.example.com:8443",
        XmlApiLogin: "admin",
        XmlApiPassword: "secret",
        SiteId: "site-42");
    var xmlApiValidateOk = true;
    try { xmlApiOptions.Validate(); }
    catch { xmlApiValidateOk = false; }
    Check("RestartMode.XmlApi with full settings passes Validate()", xmlApiValidateOk);

    // ---------------------------------------------------------------
    Console.WriteLine("== PleskExecutor: path mapping (RestartMode.None) ==");
    var publishRoot = Path.Combine(workRoot, "publish");
    Directory.CreateDirectory(Path.Combine(publishRoot, "bin"));
    Directory.CreateDirectory(Path.Combine(publishRoot, "wwwroot", "js"));
    File.WriteAllText(Path.Combine(publishRoot, "bin", "App.dll"), "app-dll-bytes");
    File.WriteAllText(Path.Combine(publishRoot, "wwwroot", "js", "site.js"), "console.log('hi');");
    File.WriteAllText(Path.Combine(publishRoot, "appsettings.json"), "{}");

    var manifest = new ComponentManifest
    {
        ComponentId = "comp-plesk",
        Client = "ClientA",
        Component = "CMS",
        Version = "2.0.0",
        CreatedUtc = DateTimeOffset.UtcNow,
        TargetFramework = "net8.0",
        Files = new List<ManifestFile>
        {
            new("bin/App.dll", "sha256:aaa", 13),
            new("wwwroot/js/site.js", "sha256:bbb", 18),
            new("appsettings.json", "sha256:ccc", 2),
        },
    };

    var mappingUploader = new RecordingUploader();
    var progress = new CollectingProgress();
    var mappingExecutor = new PleskExecutor(mappingUploader, new PleskDeployOptions("/httpdocs"));
    Check("executor TargetType is Plesk", mappingExecutor.TargetType == TargetType.Plesk);

    var mappingResult = await mappingExecutor.DeployAsync(manifest, publishRoot, progress, CancellationToken.None);
    Check("deploy with RestartMode.None reports Success", mappingResult.Success);
    Check("result reports HealthCheckPassed=false (orchestrator's job, not the executor)", !mappingResult.HealthCheckPassed);

    var uploads = mappingUploader.OpsOf("upload").ToList();
    Check("upload count == manifest file count (3)", uploads.Count == 3);
    Check("remote path for bin/App.dll is exactly /httpdocs/bin/App.dll", uploads.Contains("/httpdocs/bin/App.dll"));
    Check("remote path for wwwroot/js/site.js is exactly /httpdocs/wwwroot/js/site.js", uploads.Contains("/httpdocs/wwwroot/js/site.js"));
    Check("remote path for appsettings.json is exactly /httpdocs/appsettings.json", uploads.Contains("/httpdocs/appsettings.json"));
    Check("all remote paths use POSIX forward slashes", uploads.All(p => !p.Contains('\\')));
    Check("every upload had an existing local file", mappingUploader.Calls.Where(c => c.Op == "upload").All(c => c.LocalFileExisted));

    var mkdirs = mappingUploader.OpsOf("mkdir").ToList();
    Check("each directory created exactly once (3 calls, 3 distinct dirs)", mkdirs.Count == 3 && mkdirs.Distinct().Count() == 3);
    Check("created dirs are /httpdocs, /httpdocs/bin, /httpdocs/wwwroot/js",
        mkdirs.OrderBy(p => p, StringComparer.Ordinal)
            .SequenceEqual(new[] { "/httpdocs", "/httpdocs/bin", "/httpdocs/wwwroot/js" }));
    Check("local path for bin/App.dll resolved with platform separators to the real file",
        mappingUploader.Calls.First(c => c.Op == "upload" && c.RemotePath == "/httpdocs/bin/App.dll").LocalPath
            == Path.Combine(publishRoot, "bin", "App.dll"));
    Check("progress reported per uploaded file (3 'uploaded' messages)",
        progress.Messages.Count(m => m.Contains("uploaded", StringComparison.OrdinalIgnoreCase)) == 3);

    // ---------------------------------------------------------------
    Console.WriteLine("== PleskExecutor: deleted files ==");
    var deleteUploader = new RecordingUploader();
    deleteUploader.RemoteFiles.Add("/httpdocs/old.dll");
    var deleteManifest = new ComponentManifest
    {
        ComponentId = "comp-plesk",
        Client = "ClientA",
        Component = "CMS",
        Version = "2.0.1",
        CreatedUtc = DateTimeOffset.UtcNow,
        TargetFramework = "net8.0",
        Files = Array.Empty<ManifestFile>(),
        DeletedFiles = new[] { "old.dll", "never-existed.dll" },
    };
    var deleteResult = await new PleskExecutor(deleteUploader, new PleskDeployOptions("/httpdocs"))
        .DeployAsync(deleteManifest, publishRoot, null, CancellationToken.None);

    Check("delete-only deploy succeeds", deleteResult.Success);
    Check("existing deleted file removed at exactly /httpdocs/old.dll",
        deleteUploader.OpsOf("delete").SequenceEqual(new[] { "/httpdocs/old.dll" }));
    var existsIdx = deleteUploader.Calls.FindIndex(c => c.Op == "exists" && c.RemotePath == "/httpdocs/old.dll");
    var deleteIdx = deleteUploader.Calls.FindIndex(c => c.Op == "delete" && c.RemotePath == "/httpdocs/old.dll");
    Check("delete is guarded by FileExistsAsync (exists before delete)", existsIdx >= 0 && deleteIdx > existsIdx);
    Check("missing remote file was probed but NOT deleted",
        !deleteUploader.OpsOf("delete").Contains("/httpdocs/never-existed.dll")
        && deleteUploader.OpsOf("exists").Contains("/httpdocs/never-existed.dll"));

    // ---------------------------------------------------------------
    Console.WriteLine("== PleskAppOfflineHelper (direct) ==");
    var helperUploader = new RecordingUploader();
    await PleskAppOfflineHelper.DropAsync(helperUploader, "/httpdocs", CancellationToken.None);
    Check("DropAsync uploads exactly {root}/app_offline.htm",
        helperUploader.OpsOf("upload").SequenceEqual(new[] { "/httpdocs/app_offline.htm" }));
    await PleskAppOfflineHelper.RemoveAsync(helperUploader, "/httpdocs", CancellationToken.None);
    Check("RemoveAsync deletes the file it just dropped",
        helperUploader.OpsOf("delete").SequenceEqual(new[] { "/httpdocs/app_offline.htm" }));
    await PleskAppOfflineHelper.RemoveAsync(helperUploader, "/httpdocs", CancellationToken.None);
    Check("RemoveAsync on an absent file makes no delete call (idempotent)",
        helperUploader.OpsOf("delete").Count(p => p == "/httpdocs/app_offline.htm") == 1);

    // ---------------------------------------------------------------
    Console.WriteLine("== PleskExecutor: AppOffline restart mode ==");
    var offlineUploader = new RecordingUploader();
    var offlineResult = await new PleskExecutor(offlineUploader, new PleskDeployOptions("/httpdocs", PleskRestartMode.AppOffline))
        .DeployAsync(manifest, publishRoot, null, CancellationToken.None);
    Check("AppOffline deploy succeeds", offlineResult.Success);

    var offlineOps = offlineUploader.Calls.Select(c => $"{c.Op}:{c.RemotePath}").ToList();
    var appOfflineUploadIdx = offlineOps.IndexOf("upload:/httpdocs/app_offline.htm");
    var firstFileUploadIdx = offlineOps.IndexOf("upload:/httpdocs/bin/App.dll");
    Check("first uploaded file is app_offline.htm", appOfflineUploadIdx >= 0 && offlineOps.FindIndex(o => o.StartsWith("upload:")) == appOfflineUploadIdx);
    Check("app_offline.htm dropped before the real files", appOfflineUploadIdx >= 0 && firstFileUploadIdx > appOfflineUploadIdx);
    Check("last operations are exists+delete of app_offline.htm (site back online)",
        offlineUploader.Calls[^1].Op == "delete" && offlineUploader.Calls[^1].RemotePath == "/httpdocs/app_offline.htm"
        && offlineUploader.Calls[^2].Op == "exists" && offlineUploader.Calls[^2].RemotePath == "/httpdocs/app_offline.htm");
    Check("app_offline.htm uploaded exactly once (drop; removal is a delete, not a re-upload)",
        offlineOps.Count(o => o == "upload:/httpdocs/app_offline.htm") == 1);
    Check("all 3 real files still uploaded in AppOffline mode",
        offlineUploader.OpsOf("upload").Count(p => p != "/httpdocs/app_offline.htm") == 3);
    var offlineContent = offlineUploader.Calls
        .First(c => c.Op == "upload" && c.RemotePath == "/httpdocs/app_offline.htm").ContentSnapshot;
    Check("app_offline.htm content is friendly maintenance HTML",
        offlineContent is not null
        && offlineContent.Contains("maintenance", StringComparison.OrdinalIgnoreCase)
        && offlineContent.Contains("<html", StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------
    Console.WriteLine("== PleskExecutor: upload failure under AppOffline cleans up ==");
    var failingUploader = new RecordingUploader();
    failingUploader.ThrowOnUpload.Add("/httpdocs/bin/App.dll");
    DeploymentResult? failedResult = null;
    var executorThrew = false;
    try
    {
        failedResult = await new PleskExecutor(failingUploader, new PleskDeployOptions("/httpdocs", PleskRestartMode.AppOffline))
            .DeployAsync(manifest, publishRoot, null, CancellationToken.None);
    }
    catch { executorThrew = true; }
    Check("upload failure does NOT throw out of the executor", !executorThrew);
    Check("failed deploy reports Success=false", failedResult is { Success: false });
    Check("failure message explains what went wrong",
        failedResult is not null && failedResult.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    Check("app_offline.htm was dropped before the failing upload",
        failingUploader.Calls.Any(c => c.Op == "upload" && c.RemotePath == "/httpdocs/app_offline.htm"));
    Check("app_offline.htm removal STILL happens after the failure",
        failingUploader.Calls.Any(c => c.Op == "delete" && c.RemotePath == "/httpdocs/app_offline.htm"));
    Check("no app_offline.htm left behind on the (simulated) server",
        !failingUploader.RemoteFiles.Contains("/httpdocs/app_offline.htm"));

    // ---------------------------------------------------------------
    Console.WriteLine("== PleskXmlApiClient (fake HTTP endpoint) ==");
    const string okPacket = "<packet><response><status>ok</status></response></packet>";

    var okHandler = new FakePleskApiHandler(HttpStatusCode.OK, okPacket);
    using (var http = new HttpClient(okHandler))
    using (var client = new PleskXmlApiClient(http))
    {
        var okResult = await client.SendRestartRequestAsync(xmlApiOptions, CancellationToken.None);
        Check("2xx with ok status reports Success", okResult.Success);
        Check("request URL is {XmlApiBaseUrl}/enterprise/control/agent.php",
            okHandler.LastRequestUri == "https://plesk.example.com:8443/enterprise/control/agent.php");
        Check("HTTP Basic auth header is base64(login:password)",
            okHandler.LastAuthorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret")));
        Check("request content-type is text/xml",
            okHandler.LastContentType?.StartsWith("text/xml", StringComparison.Ordinal) == true);
        Check("restart packet posted as the request body", okHandler.LastBody?.Contains("<packet") == true);
        Check("{{SITE_ID}} placeholder replaced with SiteId",
            okHandler.LastBody!.Contains("<id>site-42</id>") && !okHandler.LastBody.Contains("{{SITE_ID}}"));
    }

    Check("restart packet template is a public, placeholder-bearing constant",
        PleskXmlApiClient.DefaultRestartPacketTemplate.Contains("{{SITE_ID}}"));

    var errorHandler = new FakePleskApiHandler(HttpStatusCode.OK,
        "<packet><response><status>error</status><errtext>nope</errtext></response></packet>");
    using (var http = new HttpClient(errorHandler))
    using (var client = new PleskXmlApiClient(http))
    {
        var errorResult = await client.SendRestartRequestAsync(xmlApiOptions, CancellationToken.None);
        Check("200 with <status>error</status> in body reports Success=false", !errorResult.Success);
        Check("Plesk error body is included in the result", errorResult.ResponseBody.Contains("nope"));
    }

    var serverErrorHandler = new FakePleskApiHandler(HttpStatusCode.InternalServerError, "upstream broke");
    using (var http = new HttpClient(serverErrorHandler))
    using (var client = new PleskXmlApiClient(http))
    {
        var serverErrorResult = await client.SendRestartRequestAsync(xmlApiOptions, CancellationToken.None);
        Check("HTTP 500 reports Success=false", !serverErrorResult.Success);
        Check("HTTP 500 result carries the status code and body",
            serverErrorResult.HttpStatus == 500 && serverErrorResult.ResponseBody.Contains("upstream broke"));
    }

    var missingConfigCaught = false;
    using (var http = new HttpClient(new FakePleskApiHandler(HttpStatusCode.OK, okPacket)))
    using (var client = new PleskXmlApiClient(http))
    {
        try
        {
            await client.SendRestartRequestAsync(new PleskDeployOptions("/httpdocs", PleskRestartMode.XmlApi), CancellationToken.None);
        }
        catch (InvalidOperationException) { missingConfigCaught = true; }
    }
    Check("XmlApi call without base URL / credentials fails fast with a clear error", missingConfigCaught);

    // ---------------------------------------------------------------
    Console.WriteLine("== PleskExecutor: XmlApi restart integration ==");
    var restartFailUploader = new RecordingUploader();
    using (var restartFailHttp = new HttpClient(new FakePleskApiHandler(HttpStatusCode.InternalServerError, "boom")))
    {
        var restartFailResult = await new PleskExecutor(restartFailUploader, xmlApiOptions, restartFailHttp)
            .DeployAsync(manifest, publishRoot, null, CancellationToken.None);
        Check("XmlApi restart failure reports Success=false", !restartFailResult.Success);
        Check("message says files are deployed but restart failed",
            restartFailResult.Message.Contains("files are deployed", StringComparison.OrdinalIgnoreCase)
            && restartFailResult.Message.Contains("restart failed", StringComparison.OrdinalIgnoreCase));
        Check("all 3 files were uploaded even though the restart failed",
            restartFailUploader.OpsOf("upload").Count() == 3);
    }

    var restartOkUploader = new RecordingUploader();
    var restartOkHandler = new FakePleskApiHandler(HttpStatusCode.OK, okPacket);
    using (var restartOkHttp = new HttpClient(restartOkHandler))
    {
        var restartOkResult = await new PleskExecutor(restartOkUploader, xmlApiOptions, restartOkHttp)
            .DeployAsync(manifest, publishRoot, null, CancellationToken.None);
        Check("XmlApi restart success reports Success", restartOkResult.Success);
        Check("restart request carried the configured site id", restartOkHandler.LastBody!.Contains("site-42"));
        Check("no app_offline.htm traffic in XmlApi mode (no upload/delete of it)",
            restartOkUploader.OpsOf("upload").All(p => p != "/httpdocs/app_offline.htm")
            && restartOkUploader.OpsOf("delete").Count() == 0);
    }

    // ---------------------------------------------------------------
    Console.WriteLine("== SftpFileUploader offline-safe guards (real transfer needs an SSH server) ==");
    var sftpUploader = new SftpFileUploader(new PleskConnectionOptions("127.0.0.1", 22, "deploy", Password: "x"));
    var fileNotFoundThrown = false;
    try
    {
        await sftpUploader.UploadFileAsync(Path.Combine(workRoot, "does-not-exist.dll"), "/httpdocs/x.dll", CancellationToken.None);
    }
    catch (FileNotFoundException) { fileNotFoundThrown = true; }
    Check("missing local file throws FileNotFoundException before any connection attempt", fileNotFoundThrown);

    var disposedCleanly = true;
    try { sftpUploader.Dispose(); }
    catch { disposedCleanly = false; }
    Check("Dispose without ever connecting is safe", disposedCleanly);
    Check("uploader implements IDisposable for connection hygiene", new SftpFileUploader(new PleskConnectionOptions("h", 22, "u")) is IDisposable);
}
finally
{
    try { Directory.Delete(workRoot, recursive: true); } catch { /* best-effort cleanup */ }
}

Console.WriteLine();
Console.WriteLine($"== {passed} passed, {failures.Count} failed ==");
if (failures.Count > 0)
{
    Console.WriteLine("Failures:");
    foreach (var f in failures) Console.WriteLine($"  - {f}");
    Environment.Exit(1);
}

// ===================================================================
// Test doubles (declared after top-level statements)
// ===================================================================

/// <summary>One recorded uploader call: op (upload/mkdir/exists/delete), remote path, and what the local side looked like.</summary>
internal sealed record UploaderCall(string Op, string RemotePath, string? LocalPath, bool LocalFileExisted, string? ContentSnapshot);

/// <summary>
/// Fake IPleskFileUploader: records every call (op, path), tracks which
/// remote files "exist" for FileExistsAsync/DeleteFileAsync semantics, and
/// can be told to throw on specific remote paths to simulate failures.
/// Content is snapshotted at upload time so tests can assert what "left the
/// machine" even after local temp files are gone.
/// </summary>
internal sealed class RecordingUploader : IPleskFileUploader
{
    public List<UploaderCall> Calls { get; } = new();
    public HashSet<string> RemoteFiles { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ThrowOnUpload { get; } = new(StringComparer.Ordinal);

    public IEnumerable<string> OpsOf(string op) => Calls.Where(c => c.Op == op).Select(c => c.RemotePath);

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        var existed = File.Exists(localPath);
        Calls.Add(new UploaderCall("upload", remotePath, localPath, existed, existed ? File.ReadAllText(localPath) : null));
        if (ThrowOnUpload.Contains(remotePath))
        {
            throw new IOException($"simulated upload failure for {remotePath}");
        }
        RemoteFiles.Add(remotePath);
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        Calls.Add(new UploaderCall("mkdir", remotePath, null, false, null));
        return Task.CompletedTask;
    }

    public Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        Calls.Add(new UploaderCall("exists", remotePath, null, false, null));
        return Task.FromResult(RemoteFiles.Contains(remotePath));
    }

    public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        Calls.Add(new UploaderCall("delete", remotePath, null, false, null));
        RemoteFiles.Remove(remotePath);
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

internal sealed class CollectingProgress : IProgress<string>
{
    public List<string> Messages { get; } = new();
    public void Report(string value) => Messages.Add(value);
}

/// <summary>
/// Fake Plesk XML API endpoint: captures the last request (URL, auth header,
/// content type, body) and answers with a canned status/body.
/// </summary>
internal sealed class FakePleskApiHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public FakePleskApiHandler(HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestUri => LastRequest?.RequestUri?.ToString();
    public string? LastAuthorization => LastRequest?.Headers.Authorization?.ToString();
    public string? LastContentType => LastRequest?.Content?.Headers.ContentType?.MediaType;
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, Encoding.UTF8, "text/xml"),
        };
    }
}
