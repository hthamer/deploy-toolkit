using System.Diagnostics;
using System.Text;

namespace DeployToolkit.Core.Publishing;

/// <summary>
/// Runs the full Visual Studio <c>MSBuild.exe</c> (the one shipped with a
/// Visual Studio install — <b>not</b> the .NET SDK's MSBuild) to publish
/// classic .NET Framework <b>Web Application</b> projects: the non-SDK
/// <c>.csproj</c> flavor that imports <c>Microsoft.WebApplication.targets</c>
/// (detected by <see cref="WebProjectDetector"/>).
///
/// Those projects cannot be published with <c>dotnet publish</c>:
///  • the .NET SDK MSBuild does not ship the Visual Studio Web Applications
///    targets, so <c>dotnet publish</c> fails with
///    <c>error MSB4019: ... Microsoft.WebApplication.targets was not found</c>;
///  • <c>dotnet restore</c> cannot see <c>packages.config</c>, so it prints
///    <c>Nothing to do. None of the projects specified contain packages to
///    restore.</c> and the build then dies on the missing target import.
///
/// The publish uses the Web Publishing Pipeline (WPP) targets — the same
/// recipe Visual Studio's <c>Folder</c> publish profile generates — via
/// <c>DeployOnBuild=true</c> + <c>WebPublishMethod=FileSystem</c>.
/// Precompilation and App_Data exclusion map onto the WPP properties the VS
/// publish wizard sets (see <see cref="WebPrecompileOptions"/>).
///
/// Process plumbing mirrors <see cref="DotNetPublisher"/>: merged stdout/stderr
/// streamed line-by-line, timeout + cancellation by tree-killing the whole
/// MSBuild process tree (a hung publish must never wedge the UI).
/// </summary>
public static class MsBuildPublisher
{
    /// <summary>
    /// Builds the MSBuild command line for a .NET Framework Web Application
    /// file-system publish. Property/switch order is stable so the UI
    /// preview and the self-tests can compare against a fixed string.
    /// </summary>
    public static string BuildArguments(PublishSettings settings)
    {
        settings.Validate();
        var sb = new StringBuilder();
        sb.Append(Quote(settings.ProjectPath));

        // Run the Restore target first (MSBuild 15+ `/restore` switch) so a
        // classic packages.config project has its packages resolved before
        // the build — VS does this implicitly via NuGet on build. Harmless
        // for projects with no packages to restore.
        sb.Append(" /restore");

        if (!string.IsNullOrWhiteSpace(settings.Configuration))
            sb.Append(" /p:Configuration=").Append(settings.Configuration);

        // Web Publishing Pipeline: trigger the publish target on build and
        // route it to a filesystem output (no IIS / Zip / MSDeploy). This is
        // the same set of properties VS writes into a "Folder" publish
        // profile, so the output matches what the VS publish wizard would
        // produce.
        sb.Append(" /p:DeployOnBuild=true");
        sb.Append(" /p:WebPublishMethod=FileSystem");
        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
            sb.Append(" /p:PublishUrl=").Append(Quote(settings.OutputDirectory!));
        sb.Append(" /p:DeleteExistingFiles=False");

        // Precompilation (VS: "Precompile during publishing" + Configure).
        if (settings.Precompile == true)
        {
            sb.Append(" /p:PrecompileBeforePublish=true");
            var opts = settings.PrecompileOptions ?? WebPrecompileOptions.Default;
            // aspnet_compiler -u / -fixednames / -d mappings:
            sb.Append(" /p:EnableUpdateable=").Append(opts.Updatable ? "true" : "false");
            sb.Append(" /p:UseFixedNames=").Append(opts.UseFixedNames ? "true" : "false");
            sb.Append(" /p:DebugSymbols=").Append(opts.EmitDebugInfo ? "true" : "false");
        }

        // VS: "Exclude files from the App_Data folder".
        if (settings.ExcludeAppData == true)
            sb.Append(" /p:ExcludeApp_Data=true");

        // Caller-supplied verbatim extra args (e.g. /p:Foo=bar /nologo). Passed
        // through unmodified — no shell, no quoting magic (plan §1).
        if (!string.IsNullOrWhiteSpace(settings.AdditionalArguments))
            sb.Append(' ').Append(settings.AdditionalArguments);

        return sb.ToString();
    }

    /// <summary>
    /// Locates a Visual Studio MSBuild (the only MSBuild that can publish
    /// .NET Framework Web Application projects, because the Web Publishing
    /// Pipeline + Web Applications targets ship with VS, not the .NET SDK
    /// and not the .NET Framework). Order:
    ///  <list type="number">
    ///   <item><c>vswhere.exe</c> (canonical probe for any VS 2017+ install,
    ///    including Build Tools and Preview).</item>
    ///   <item>the well-known VS install paths (when vswhere is absent).</item>
    ///  </list>
    /// Returns <c>null</c> when no Visual Studio is installed — DO NOT fall
    /// back to the .NET Framework MSBuild here: it can host a build but
    /// <b>cannot</b> run the Web Publishing Pipeline (<c>$(VSToolsPath)</c>
    /// is empty, so the <c>Microsoft.WebApplication.targets</c> import is
    /// skipped via its <c>Condition="'$(VSToolsPath)' != ''"</c>), which
    /// means <c>DeployOnBuild=true</c> silently does nothing and msbuild
    /// exits 0 without producing any publish output — the tool would then
    /// crash later in <c>CountFiles</c> with <c>DirectoryNotFoundException</c>
    /// on the missing output folder. Failing here with a clear message is
    /// correct; a silent "success" is not.
    /// </summary>
    public static string? ResolveVsMsBuildExecutable()
    {
        // 1. vswhere → MSBuild\Current\Bin\MSBuild.exe (VS 2019 / 2022+).
        var vsWhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vsWhere))
        {
            try
            {
                var vsRoot = RunCapture(vsWhere,
                    "-latest -property installationPath -prerelease");
                if (!string.IsNullOrWhiteSpace(vsRoot) && Directory.Exists(vsRoot))
                {
                    var candidate = Path.Combine(vsRoot, "MSBuild", "Current", "Bin", "MSBuild.exe");
                    if (File.Exists(candidate)) return candidate;
                    // VS 2017 layout (no "Current" folder).
                    candidate = Path.Combine(vsRoot, "MSBuild", "15.0", "Bin", "MSBuild.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // vswhere present but broken — fall through to the hard-coded paths.
            }
        }

        // 2. Hard-coded well-known VS install paths (when vswhere is absent).
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var root in new[]
                     {
                         Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Enterprise"),
                         Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Professional"),
                         Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Community"),
                         Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Preview"),
                         Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "BuildTools"),
                         Path.Combine(programFilesX86, "Microsoft Visual Studio", "2019", "Enterprise"),
                         Path.Combine(programFilesX86, "Microsoft Visual Studio", "2019", "Professional"),
                         Path.Combine(programFilesX86, "Microsoft Visual Studio", "2019", "Community"),
                         Path.Combine(programFilesX86, "Microsoft Visual Studio", "2019", "BuildTools"),
                         Path.Combine(programFilesX86, "Microsoft Visual Studio", "2017", "Enterprise"),
                         Path.Combine(programFilesX86, "Microsoft Visual Studio", "2017", "Professional"),
                         Path.Combine(programFilesX86, "Microsoft Visual Studio", "2017", "BuildTools"),
                     })
            {
                var candidate = Path.Combine(root, "MSBuild", "Current", "Bin", "MSBuild.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null; // no Visual Studio found — caller must surface a clear error
    }

    /// <summary>
    /// Locates any MSBuild on the machine: the Visual Studio MSBuild first
    /// (see <see cref="ResolveVsMsBuildExecutable"/>), then the .NET
    /// Framework MSBuild as a last resort. <b>Only use this for
    /// display/summary purposes</b> — for actually publishing .NET Framework
    /// Web Applications, use <see cref="ResolveVsMsBuildExecutable"/> via
    /// <see cref="PublishAsync"/>. The Framework MSBuild fallback is here so
    /// a UI preview can still show <c>msbuild …</c> on machines without VS,
    /// but publishing from it would silently produce no output (see the
    /// warning on <see cref="ResolveVsMsBuildExecutable"/>).
    /// </summary>
    public static string? ResolveMsBuildExecutable()
    {
        var vsMsBuild = ResolveVsMsBuildExecutable();
        if (vsMsBuild is not null)
            return vsMsBuild;

        // Last resort — the .NET Framework MSBuild. Display/summary only;
        // never use this to actually publish a web app (see above).
        if (OperatingSystem.IsWindows())
        {
            var fwMsBuild = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "Framework64", "v4.0.30319", "MSBuild.exe");
            if (File.Exists(fwMsBuild)) return fwMsBuild;
        }

        return null;
    }

    /// <summary>
    /// Runs MSBuild with the given settings, streaming merged stdout/stderr
    /// line-by-line into <paramref name="onOutputLine"/> (the UI progress
    /// pane). Handles timeout and cancellation by tree-killing the whole
    /// MSBuild process tree — a hung publish must never wedge the UI.
    /// </summary>
    public static async Task<PublishResult> PublishAsync(
        PublishSettings settings,
        Action<string>? onOutputLine = null,
        int timeoutMinutes = 10,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        var msbuild = ResolveVsMsBuildExecutable()
            ?? throw new InvalidOperationException(
                "Could not locate the Visual Studio MSBuild. .NET Framework Web Application " +
                "projects require the Web Publishing Pipeline + Microsoft.WebApplication.targets, " +
                "which ship with Visual Studio (install the 'ASP.NET and web development tools' " +
                "workload). The .NET Framework MSBuild cannot publish web apps — it exits " +
                "successfully without producing any output because $(VSToolsPath) is empty and " +
                "the Web Applications targets import is skipped. Install Visual Studio 2017+ " +
                "(or the standalone Build Tools with the web workload) and retry.");

        var psi = new ProcessStartInfo
        {
            FileName = msbuild,
            Arguments = BuildArguments(settings),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Directory.Exists(settings.ProjectPath)
                ? settings.ProjectPath
                : Path.GetDirectoryName(Path.GetFullPath(settings.ProjectPath))!,
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
            return new PublishResult(false, -1, false, settings.OutputDirectory ?? string.Empty,
                Array.Empty<string>(), "Failed to start the MSBuild process.");

        // Without Begin*ReadLine the redirected pipes are never drained:
        // events never fire AND a chatty MSBuild would deadlock on a full
        // pipe buffer.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        await using var registration = timeoutCts.Token.Register(() =>
        {
            try
            {
                // Tree kill: MSBuild spawns the aspnet_compiler / node / csc
                // children that must not outlive the attempt.
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* already exited */ }
            catch (System.ComponentModel.Win32Exception) { /* access race on exit */ }
        });

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The tree was killed by the registration above (timeout or
            // caller cancellation) — fall through and report, never throw
            // past the caller's UI thread.
        }
        // Drain async readers so the last lines are captured.
        process.WaitForExit();

        var timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
        var lines = output.ToList();
        var exitCode = timedOut ? -1 : process.ExitCode;

        string? errorSummary = null;
        if (exitCode != 0)
        {
            var tail = lines.Skip(Math.Max(0, lines.Count - 40));
            errorSummary = string.Join(Environment.NewLine, tail);
        }

        return new PublishResult(
            Success: !timedOut && exitCode == 0,
            ExitCode: exitCode,
            TimedOut: timedOut,
            OutputDirectory: settings.OutputDirectory ?? string.Empty,
            OutputLines: lines,
            ErrorSummary: errorSummary);
    }

    /// <summary>Captures the stdout of a short-lived helper process
    /// (vswhere) — trimmed. Returns null if the process could not be
    /// started.</summary>
    private static string? RunCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return null;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout.Trim();
    }

    private static string Quote(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}
