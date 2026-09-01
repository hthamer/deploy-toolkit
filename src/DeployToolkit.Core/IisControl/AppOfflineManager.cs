namespace DeployToolkit.Core.IisControl;

/// <summary>
/// The app_offline.htm fallback (plan §7 step 4 / §8.3): dropping a file
/// named <c>app_offline.htm</c> into an ASP.NET application's root makes
/// IIS take the app out of the app pool immediately and serve that file for
/// every request — no IIS management rights required, pure file I/O. This
/// is the "accounts without IIS management rights" path, and also the
/// cleanest way to stop a Plesk-hosted IIS app.
/// </summary>
public static class AppOfflineManager
{
    public const string FileName = "app_offline.htm";

    /// <summary>Simplified Chinese-free, dependency-free maintenance page —
    /// must be self-contained since the app itself is down.</summary>
    public const string DefaultContent =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Maintenance</title></head>" +
        "<body style=\"font-family:Segoe UI,sans-serif;text-align:center;padding-top:10vh\">" +
        "<h1>Temporary maintenance</h1>" +
        "<p>The application is being updated and will be back online in a few minutes.</p>" +
        "</body></html>";

    public static bool IsDropped(string siteRoot) => File.Exists(Path.Combine(siteRoot, FileName));

    /// <summary>Drops app_offline.htm (overwrites any existing one) and
    /// returns the file path written.</summary>
    public static string Drop(string siteRoot, string? content = null)
    {
        Directory.CreateDirectory(siteRoot);
        var path = Path.Combine(siteRoot, FileName);
        File.WriteAllText(path, content ?? DefaultContent);
        return path;
    }

    /// <summary>Removes app_offline.htm if present; returns whether it was
    /// actually there. Best-effort by design — the site must come back up
    /// even if file permissions have changed mid-deploy.</summary>
    public static bool Remove(string siteRoot)
    {
        var path = Path.Combine(siteRoot, FileName);
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
