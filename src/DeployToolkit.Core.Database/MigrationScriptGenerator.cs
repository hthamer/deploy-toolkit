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
    /// Resolves a project path (which may be a <c>.csproj</c> FILE or a
    /// directory) to its containing directory. The EF-migrations UI dropdown
    /// lists <c>.csproj</c> files (e.g.
    /// <c>F:\...\XonetPlus_V4.Data\XonetPlus_V4.Data.csproj</c>), but
    /// <see cref="DiscoverMigrations"/> and the process working directory need
    /// the PROJECT FOLDER (the directory holding the <c>Migrations</c>
    /// subfolder). Returns the input unchanged when it's already a directory;
    /// returns its <c>Path.GetDirectoryName</c> when it's a <c>.csproj</c> file;
    /// returns the input unchanged when it doesn't exist (the caller's
    /// <c>Directory.Exists</c> check handles the missing case).
    /// </summary>
    public static string ResolveProjectDirectory(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return projectPath ?? string.Empty;

        // A .csproj file → its containing directory is the project folder.
        if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(projectPath))
            return Path.GetDirectoryName(projectPath)!;

        // Already a directory → use as-is.
        if (Directory.Exists(projectPath))
            return projectPath;

        // Doesn't exist as a file or dir — return as-is so the caller's
        // existence check surfaces the clear error.
        return projectPath;
    }

    /// <summary>
    /// Discovers EF Core migrations under <c>&lt;projectDir&gt;/Migrations</c>.
    /// EF Core migrations are FILES (not subdirectories): each migration is a
    /// pair <c>&lt;timestamp&gt;_&lt;Name&gt;.cs</c> +
    /// <c>&lt;timestamp&gt;_&lt;Name&gt;.Designer.cs</c>, plus a single
    /// <c>&lt;DbContext&gt;ModelSnapshot.cs</c>. This method scans the .cs
    /// FILES in the Migrations folder and returns each migration, newest-first
    /// by timestamp (when present) or alphabetically (fallback).
    ///
    /// <paramref name="dbProjectFolder"/> may be a <c>.csproj</c> FILE path
    /// (as selected in the UI dropdown) OR a project directory — the method
    /// resolves it to the containing directory first via
    /// <see cref="ResolveProjectDirectory"/>.
    ///
    /// <b>Matching is deliberately lenient</b> to handle real-world layouts:
    ///  - Standard EF Core: <c>&lt;14-digit-timestamp&gt;_&lt;Name&gt;.cs</c>
    ///    (e.g. <c>20240115103045_InitialCreate.cs</c>).
    ///  - Older EF / custom: any all-digit prefix of any length followed by
    ///    <c>_&lt;Name&gt;.cs</c> (not just 14 digits — some teams use shorter
    ///    or longer timestamps).
    ///  - Fallback (no timestamp prefix at all): any <c>&lt;Name&gt;.cs</c>
    ///    that isn't a generated <c>.Designer.cs</c> or <c>*ModelSnapshot.cs</c>
    ///    and isn't obviously a non-migration (the user can deselect in the UI).
    /// This matches the user's request: "scan the Migrations folder and get
    /// those migrations files then try to generate the script" — don't rely
    /// on a strict timestamp pattern.
    ///
    /// Returns an empty list when the Migrations folder is absent or holds no
    /// .cs files at all.
    /// </summary>
    public static IReadOnlyList<EfMigration> DiscoverMigrations(string dbProjectFolder)
    {
        if (string.IsNullOrWhiteSpace(dbProjectFolder))
            return Array.Empty<EfMigration>();

        // Resolve a .csproj FILE path to its containing directory — the UI
        // dropdown lists .csproj files, but the Migrations folder is a
        // sibling of the .csproj, not inside it.
        var projectDir = ResolveProjectDirectory(dbProjectFolder);
        if (!Directory.Exists(projectDir))
            return Array.Empty<EfMigration>();

        var migrationsDir = Path.Combine(projectDir, "Migrations");
        if (!Directory.Exists(migrationsDir))
            return Array.Empty<EfMigration>();

        var timestamped = new List<(long Timestamp, string Name, string Path)>();
        var fallback = new List<(string Name, string Path)>();

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
            if (underscore > 0)
            {
                var tsStr = fileName[..underscore];
                // Any all-digit prefix (any length ≥ 1) followed by _<Name>.cs
                // is a timestamped migration. EF Core uses 14 digits but older
                // EF / custom setups may differ.
                if (tsStr.Length > 0 && tsStr.All(char.IsDigit) && long.TryParse(tsStr, out var ts))
                {
                    timestamped.Add((ts, fileName, file));
                    continue;
                }
            }

            // Fallback: a .cs file with no numeric timestamp prefix. Could be
            // a custom-named migration (some teams rename them). Keep it so
            // the user sees it in the grid and can decide — better to show a
            // false positive than to hide a real migration.
            fallback.Add((fileName, file));
        }

        // Timestamped migrations newest-first; fallback alphabetical.
        var result = new List<EfMigration>();
        foreach (var m in timestamped.OrderByDescending(m => m.Timestamp))
            result.Add(new EfMigration(m.Name, m.Path));
        foreach (var m in fallback.OrderBy(m => m.Name, StringComparer.Ordinal))
            result.Add(new EfMigration(m.Name, m.Path));
        return result;
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

        // The 'dotnet ef' subcommand requires the dotnet-ef global tool to be
        // installed AND on PATH. Check it proactively so we can give a clear,
        // actionable error instead of the raw 'dotnet-ef does not exist' text
        // that dotnet itself prints when the tool is missing.
        var efCheck = await CheckDotNetEfInstalledAsync(dotnet);
        if (!efCheck.Installed)
        {
            return new MigrationScriptResult(false, string.Empty, -1,
                $"The EF Core tools are not installed (or not on PATH). " +
                $"Install them once with:\n\n  dotnet tool install --global dotnet-ef\n\n" +
                $"Then restart the app so the new PATH is picked up. " +
                $"(Detected dotnet: {dotnet}. Tool path checked: {efCheck.ToolsPath})");
        }

        var outputFile = Path.Combine(Path.GetTempPath(), "DeployToolkit",
            "ef-migrations", $"{Guid.NewGuid():N}.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

        // The --project arg accepts a .csproj file path OR a directory (dotnet
        // ef handles both), so pass the original path. But the process WORKING
        // DIRECTORY must be a directory (a file path there throws), so resolve
        // it. This is the fix for the "selecting a .csproj file → empty
        // migrations" bug: the UI dropdown lists .csproj files, not folders.
        var arguments = BuildArguments(dbProjectFolder, outputFile, fromMigration, toMigration, idempotent);
        var workingDir = ResolveProjectDirectory(dbProjectFolder);
        if (!Directory.Exists(workingDir))
            workingDir = Environment.CurrentDirectory; // fallback — never crash on a bad path

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
            WorkingDirectory = workingDir,
        };

        // Ensure the dotnet global tools directory is on the child process's
        // PATH so `dotnet ef` can find the dotnet-ef tool. The SDK adds
        // %USERPROFILE%\.dotnet\tools to PATH at install time, but the
        // resolved dotnet might be from a custom location (DOTNET_ROOT) whose
        // environment doesn't carry it — so add it explicitly.
        AddGlobalToolsToPath(psi);

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

    /// <summary>The dotnet global tools directory (%USERPROFILE%\.dotnet\tools
    /// on Windows, ~/.dotnet/tools on Linux/macOS). This is where
    /// <c>dotnet-ef</c> lands after <c>dotnet tool install --global dotnet-ef</c>.</summary>
    public static string GetGlobalToolsDirectory()
    {
        // %USERPROFILE%\.dotnet\tools (Windows) / ~/.dotnet/tools (Unix).
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userProfile))
            return string.Empty;
        return Path.Combine(userProfile, ".dotnet", "tools");
    }

    /// <summary>Checks whether <c>dotnet-ef</c> is installed and findable by
    /// running <c>dotnet ef --version</c>. Returns the result + the tools path
    /// checked (for the error message). Never throws.</summary>
    private static async Task<(bool Installed, string ToolsPath)> CheckDotNetEfInstalledAsync(string dotnet)
    {
        var toolsPath = GetGlobalToolsDirectory();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = "ef --version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            AddGlobalToolsToPath(psi);

            using var p = Process.Start(psi);
            if (p is null)
                return (false, toolsPath);
            await p.WaitForExitAsync();
            // dotnet ef --version exits 0 when the tool is present. A non-zero
            // exit (typically exit 1 with the "dotnet-ef does not exist" text)
            // means the tool is missing.
            return (p.ExitCode == 0, toolsPath);
        }
        catch
        {
            return (false, toolsPath);
        }
    }

    /// <summary>Adds the dotnet global tools directory to the child process's
    /// PATH so <c>dotnet ef</c> can find <c>dotnet-ef</c>. The SDK normally
    /// adds %USERPROFILE%\.dotnet\tools at install time, but the resolved
    /// <c>dotnet</c> may be from a custom DOTNET_ROOT whose environment
    /// doesn't carry it — so add it explicitly here.</summary>
    private static void AddGlobalToolsToPath(ProcessStartInfo psi)
    {
        var toolsDir = GetGlobalToolsDirectory();
        if (string.IsNullOrEmpty(toolsDir) || !Directory.Exists(toolsDir))
            return; // nothing to add

        var currentPath = psi.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (currentPath.Contains(toolsDir, StringComparison.OrdinalIgnoreCase))
            return; // already there

        // Prepend so the tools dir wins over any stale entry.
        psi.EnvironmentVariables["PATH"] = $"{toolsDir}{Path.PathSeparator}{currentPath}";
    }
}
