using System.Diagnostics;
using System.Text;

namespace DeployToolkit.Core.Database;

/// <summary>
/// Describes one EF Core migration discovered under a database project's
/// <c>Migrations</c> folder. The <see cref="Name"/> is the migration's
/// folder name (<c>20260901120000_InitialCreate</c>) — the same identifier
/// <c>dotnet ef migrations script</c> accepts as the <c>--from</c> / <c>--to</c>
/// arguments.
/// </summary>
public sealed record EfMigration(string Name, string FolderPath)
{
    /// <summary>The short name after the timestamp prefix (e.g.
    /// <c>InitialCreate</c> for <c>20260901120000_InitialCreate</c>) — used
    /// only for display; the full name is what the CLI needs.</summary>
    public string DisplayName => Name.IndexOf('_') is var i and >= 0 ? Name[(i + 1)..] : Name;
}

/// <summary>
/// Result of <see cref="MigrationScriptGenerator.GenerateScriptAsync"/>.
/// </summary>
public sealed record MigrationScriptResult(
    bool Success,
    string ScriptText,
    int ExitCode,
    string? ErrorSummary);

/// <summary>
/// Generates a SQL migration script from an EF Core database project by
/// shelling out to <c>dotnet ef migrations script</c>. User request:
/// "For the database schema and data also apply the same, since it's in
/// different project, then add option for the user to select the desired
/// project, then it will check the available migrations (usually it will be
/// in Migrations folder), and if there is new migration it will generate its
/// script and append it automatically (also I must have the ability to modify
/// it manually) and I should add more and delete."
///
/// <b>Workflow</b>:
///  <list type="number">
///   <item><see cref="DiscoverMigrations"/> scans <c>&lt;dbProjectFolder&gt;/Migrations</c>
///    for subdirectories matching the EF Core migration pattern
///    <c>YYYYMMDDHHMMSS_Name</c>.</item>
///   <item><see cref="GenerateScriptAsync"/> runs
///    <c>dotnet ef migrations script --project &lt;dbProjectFolder&gt; --output &lt;tempSqlFile&gt; [--from &lt;mig&gt;] [--to &lt;mig&gt;]</c>
///    and returns the generated SQL (the UI attaches it to the package as an
///    editable .sql script — the user can modify, add, or delete).</item>
///  </list>
///
/// Process plumbing mirrors <see cref="DeployToolkit.Core.Publishing.DotNetPublisher"/>:
/// merged stdout/stderr streamed line-by-line, timeout + cancellation by
/// tree-killing the whole process tree. <c>dotnet ef</c> requires the
/// <c>dotnet-ef</c> tool to be installed (<c>dotnet tool install --global
/// dotnet-ef</c>); a clear error is surfaced when it's not.
/// </summary>
public static class MigrationScriptGenerator
{
    /// <summary>
    /// Discovers EF Core migrations under <c>&lt;dbProjectFolder&gt;/Migrations</c>.
    /// EF Core migrations are FILES (not subdirectories): each migration is a
    /// pair <c>&lt;timestamp&gt;_&lt;Name&gt;.cs</c> +
    /// <c>&lt;timestamp&gt;_&lt;Name&gt;.Designer.cs</c>, plus a single
    /// <c>&lt;DbContext&gt;ModelSnapshot.cs</c>. This method scans the .cs
    /// FILES in the Migrations folder, picks the migration files (timestamp +
    /// underscore + name, excluding <c>.Designer.cs</c> and
    /// <c>ModelSnapshot.cs</c>), and returns each as a
    /// <see cref="EfMigration"/> with the full migration name
    /// (<c>20260901120000_InitialCreate</c>), newest-first by timestamp.
    /// Returns an empty list when the Migrations folder is absent or holds no
    /// migration-shaped files.
    /// </summary>
    public static IReadOnlyList<EfMigration> DiscoverMigrations(string dbProjectFolder)
    {
        if (string.IsNullOrWhiteSpace(dbProjectFolder) || !Directory.Exists(dbProjectFolder))
            return Array.Empty<EfMigration>();

        var migrationsDir = Path.Combine(dbProjectFolder, "Migrations");
        if (!Directory.Exists(migrationsDir))
            return Array.Empty<EfMigration>();

        var migrations = new List<(long Timestamp, string Name, string Path)>();
        // EF Core migrations are FILES named <timestamp>_<Name>.cs — scan the
        // .cs files directly (NOT subdirectories; the earlier directory-based
        // scan found nothing because EF Core never creates migration subfolders).
        foreach (var file in Directory.EnumerateFiles(migrationsDir, "*.cs"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            // Exclude the generated designer + model-snapshot files — they're
            // not migrations.
            if (fileName.EndsWith(".Designer", StringComparison.OrdinalIgnoreCase))
                continue;
            if (fileName.EndsWith("ModelSnapshot", StringComparison.OrdinalIgnoreCase))
                continue;

            var underscore = fileName.IndexOf('_');
            if (underscore <= 0)
                continue; // not a migration file (migrations are <timestamp>_<name>)

            var tsStr = fileName[..underscore];
            // EF timestamps are yyyyMMddHHmmss (14 digits). Be lenient: accept
            // 12-16 digits so older EF formats (yyyyMMddHHmm) still work.
            if (tsStr.Length < 12 || tsStr.Length > 16 || !tsStr.All(char.IsDigit))
                continue;

            if (!long.TryParse(tsStr, out var ts))
                continue;

            // The full migration name = the file name without the extension
            // (e.g. "20260901120000_InitialCreate"). This is what
            // `dotnet ef migrations script` accepts as --from/--to.
            migrations.Add((ts, fileName, file));
        }

        return migrations
            .OrderByDescending(m => m.Timestamp)
            .Select(m => new EfMigration(m.Name, m.Path))
            .ToList();
    }

    /// <summary>
    /// Builds the <c>dotnet ef migrations script</c> command line. The
    /// <c>--output</c> flag is used (not stdout capture) because
    /// <c>dotnet ef</c> writes progress/info to stdout and the script to the
    /// output file — capturing stdout would interleave them. The caller reads
    /// the output file after the process exits.
    /// <para>
    /// When <paramref name="idempotent"/> is true, adds <c>--idempotent</c> so
    /// the generated script guards each migration with <c>IF NOT EXISTS</c>
    /// checks against <c>__EFMigrationsHistory</c> — safe to re-run on a DB
    /// that already has some of the migrations applied (handles the
    /// "migrations in the middle added later" case without erroring).
    /// </para>
    /// </summary>
    public static string BuildArguments(string dbProjectFolder, string outputFile, string? fromMigration, string? toMigration, bool idempotent = false)
    {
        if (string.IsNullOrWhiteSpace(dbProjectFolder))
            throw new ArgumentException("dbProjectFolder is required.", nameof(dbProjectFolder));
        if (string.IsNullOrWhiteSpace(outputFile))
            throw new ArgumentException("outputFile is required.", nameof(outputFile));

        var sb = new StringBuilder("ef migrations script");
        if (!string.IsNullOrWhiteSpace(fromMigration))
            sb.Append(' ').Append(fromMigration);
        if (!string.IsNullOrWhiteSpace(toMigration))
            sb.Append(' ').Append(toMigration);
        sb.Append(" --project ").Append(Quote(dbProjectFolder));
        sb.Append(" --output ").Append(Quote(outputFile));
        if (idempotent)
            sb.Append(" --idempotent");
        sb.Append(" --no-build"); // the publish step already built the project; skip the redundant rebuild
        return sb.ToString();
    }

    /// <summary>
    /// Runs <c>dotnet ef migrations script</c> against <paramref name="dbProjectFolder"/>
    /// and returns the generated SQL. <paramref name="fromMigration"/> and
    /// <paramref name="toMigration"/> are the EF migration identifiers (the
    /// folder names); when both are null, EF generates the script from the
    /// first migration to the latest (the full schema). When <c>from</c> is
    /// the last deployed migration and <c>to</c> is the latest, EF generates
    /// only the delta — the common "new migration since the last release" case.
    /// The generated SQL is read from <c>--output</c> (EF writes the script
    /// there, not stdout).
    /// </summary>
    public static async Task<MigrationScriptResult> GenerateScriptAsync(
        string dbProjectFolder,
        string? fromMigration = null,
        string? toMigration = null,
        Action<string>? onOutputLine = null,
        int timeoutMinutes = 5,
        bool idempotent = false,
        CancellationToken cancellationToken = default)
    {
        var dotnet = DeployToolkit.Core.Publishing.DotNetPublisher.ResolveDotNetExecutable()
            ?? throw new InvalidOperationException(
                "Could not locate the dotnet executable. Install the .NET SDK " +
                "(and the EF Core tools: 'dotnet tool install --global dotnet-ef').");

        var outputFile = Path.Combine(Path.GetTempPath(), "DeployToolkit",
            "ef-migrations", $"{Guid.NewGuid():N}.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

        var arguments = BuildArguments(dbProjectFolder, outputFile, fromMigration, toMigration, idempotent);

        var psi = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = dbProjectFolder,
        };

        using var process = new Process { StartInfo = psi };
        var output = new List<string>();
        var outputLock = new object();

        void OnLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (outputLock) output.Add(line);
            onOutputLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);

        if (!process.Start())
            return new MigrationScriptResult(false, string.Empty, -1,
                "Failed to start the dotnet process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        await using var registration = timeoutCts.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            catch (System.ComponentModel.Win32Exception) { /* access race */ }
        });

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* killed by timeout/cancel — report below */ }
        process.WaitForExit();

        var timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
        var exitCode = timedOut ? -1 : process.ExitCode;

        string? errorSummary = null;
        if (exitCode != 0)
        {
            var tail = output.Skip(Math.Max(0, output.Count - 40));
            errorSummary = string.Join(Environment.NewLine, tail);
        }

        // Read the generated script from --output (EF writes the SQL there,
        // not stdout — stdout holds progress/info lines).
        var scriptText = string.Empty;
        try
        {
            if (File.Exists(outputFile))
                scriptText = await File.ReadAllTextAsync(outputFile, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read failure is non-fatal when the process failed; fatal-ish
            // otherwise — surface it in the error summary.
            if (exitCode == 0)
                errorSummary = $"Migration script generated but could not be read: {ex.Message}";
        }

        // Clean up the temp file (best-effort).
        try { if (File.Exists(outputFile)) File.Delete(outputFile); }
        catch { /* temp cleanup is best-effort */ }

        return new MigrationScriptResult(
            Success: !timedOut && exitCode == 0 && scriptText.Length > 0,
            ScriptText: scriptText,
            ExitCode: exitCode,
            ErrorSummary: errorSummary);
    }

    private static string Quote(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}
