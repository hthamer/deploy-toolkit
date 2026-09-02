namespace DeployToolkit.Core.Packaging;

/// <summary>
/// Where a built <c>delta.zip</c> is published so the Deployer (running on a
/// different machine) can fetch it without the builder having to copy the
/// file by hand (Option B: shared folder + registry links the package).
/// <para>
/// The Packager uploads the .zip to the store right after
/// <see cref="PackageWriter.Write"/> writes it locally, records the
/// returned <c>location</c> string on the <see cref="Registry.PackageRecord"/>,
/// and the Deployer reads it back by that location. The location is opaque to
/// the registry (a UNC path, a local path, a URL — whatever the store
/// implementation produces) so the registry stays store-agnostic.
/// </para>
/// <para>
/// <b>Credentials</b>: a network share protected by a password whose
/// credentials are saved in Windows Credential Manager needs no explicit
/// auth here — <see cref="FileSystemPackageStore"/> uses normal file IO
/// against the UNC path and Windows resolves the saved credentials
/// automatically. When the credentials aren't saved, the IO throws
/// <see cref="UnauthorizedAccessException"/>; the caller surfaces that as a
/// clear "save the share's credentials in Windows Credential Manager" error.
/// </para>
/// </summary>
public interface IPackageStore
{
    /// <summary>
    /// Uploads (copies) the .zip at <paramref name="localZipPath"/> into the
    /// store under a path derived from <paramref name="clientName"/> +
    /// <paramref name="componentName"/> + <paramref name="version"/>, and
    /// returns the opaque location string the registry will record on the
    /// <see cref="Registry.PackageRecord"/>.
    /// Throws on failure (IO error, unreachable share, auth refused) — the
    /// caller decides whether to treat it as fatal or best-effort.
    /// </summary>
    Task<string> UploadAsync(string localZipPath, string clientName, string componentName, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <paramref name="location"/> points at a .zip that currently
    /// exists in the store. Used by the Deployer's "Pick from registry…"
    /// flow to show which packages are actually fetchable (a stale location
    /// for a deleted .zip is reported, not silently followed).
    /// </summary>
    bool Exists(string location);

    /// <summary>
    /// Copies the .zip at <paramref name="location"/> to
    /// <paramref name="destinationLocalPath"/> (a local temp path the Deployer
    /// then loads with the existing integrity-check flow). Throws on failure.
    /// </summary>
    Task DownloadAsync(string location, string destinationLocalPath, CancellationToken cancellationToken = default);
}
