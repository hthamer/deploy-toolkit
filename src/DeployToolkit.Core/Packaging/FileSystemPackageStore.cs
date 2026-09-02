namespace DeployToolkit.Core.Packaging;

/// <summary>
/// An <see cref="IPackageStore"/> backed by a filesystem folder — typically a
/// network share mounted as a UNC path (<c>\\fileserver\DeployToolkit\Packages</c>)
/// or a drive letter. The user's chosen Option B transport: "a network shared
/// folder, password-protected, credentials saved in Windows."
/// <para>
/// <b>Credentials</b>: no explicit auth code — <see cref="File.Copy"/> /
/// <see cref="File.OpenRead"/> against a UNC path use the saved Windows
/// Credential Manager entry automatically. When the entry is missing the IO
/// throws <see cref="UnauthorizedAccessException"/>; the caller surfaces that
/// as a clear "save the share's credentials in Windows Credential Manager"
/// error rather than silently swallowing it.
/// </para>
/// <para>
/// <b>Layout</b>: <c>&lt;root&gt;/&lt;componentName&gt;/&lt;componentName&gt;-&lt;version&gt;.zip</c>.
/// The componentName is sanitized (invalid path chars → '_') so a component
/// named "CMS / Web" doesn't break the folder structure. The returned
/// location is the full path (UNC or local) the Deployer opens verbatim.
/// </para>
/// </summary>
public sealed class FileSystemPackageStore : IPackageStore
{
    private readonly string _root;

    /// <param name="root">The store root folder (UNC path or local path). The
    /// folder is created on upload if missing. Must be non-empty.</param>
    public FileSystemPackageStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Package store root is required.", nameof(root));
        _root = root;
    }

    /// <summary>The store root folder (as configured).</summary>
    public string Root => _root;

    public async Task<string> UploadAsync(string localZipPath, string componentName, string version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localZipPath))
            throw new ArgumentException("localZipPath is required.", nameof(localZipPath));
        if (string.IsNullOrWhiteSpace(componentName))
            throw new ArgumentException("componentName is required.", nameof(componentName));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("version is required.", nameof(version));
        if (!File.Exists(localZipPath))
            throw new FileNotFoundException($"Source .zip not found: {localZipPath}", localZipPath);

        var safeComponent = MakeSafeFolderName(componentName);
        var dir = Path.Combine(_root, safeComponent);
        Directory.CreateDirectory(dir); // throws if the share is unreachable / auth refused

        var fileName = MakeSafeFileName($"{componentName}-{version}") + ".zip";
        var destination = Path.Combine(dir, fileName);

        // Overwrite any previous upload of the same component+version (a
        // rebuild): File.Copy with overwrite is atomic-enough for a delta.zip;
        // the registry row's manifest is the source of truth for content.
        await Task.Run(() => File.Copy(localZipPath, destination, overwrite: true), cancellationToken);
        return destination;
    }

    public bool Exists(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return false;
        try { return File.Exists(location); }
        catch { return false; } // unreachable share / auth refused → report as missing
    }

    public async Task DownloadAsync(string location, string destinationLocalPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location))
            throw new ArgumentException("location is required.", nameof(location));
        if (string.IsNullOrWhiteSpace(destinationLocalPath))
            throw new ArgumentException("destinationLocalPath is required.", nameof(destinationLocalPath));
        if (!File.Exists(location))
            throw new FileNotFoundException($"Package not found in the store: {location}", location);

        var dir = Path.GetDirectoryName(destinationLocalPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await Task.Run(() => File.Copy(location, destinationLocalPath, overwrite: true), cancellationToken);
    }

    private static string MakeSafeFolderName(string name)
    {
        var invalid = Path.GetInvalidPathChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "component" : safe.Trim();
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "package" : safe.Trim();
    }
}
