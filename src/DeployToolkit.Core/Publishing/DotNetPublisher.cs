using System.Diagnostics;
using System.Text;

namespace DeployToolkit.Core.Publishing;

/// <summary>
/// Settings for one publish invocation (plan §5: "After pull, the
/// Packager runs dotnet publish itself... using the framework /
/// self-contained settings stored on the component"). Shell-outs are fine
/// on YOUR build machine — the "no scripts" constraint applies to target
/// servers only (plan §1).
///
/// The same record carries the structured publish options Visual Studio's
/// publish wizard exposes, so the UI can present them as proper checkboxes
/// instead of forcing the user to type raw <c>-p:PublishSingleFile=true</c>
/// into a free-text box. Each publisher only reads the options that make
/// sense for its toolchain:
///  • <see cref="DotNetPublisher"/> (modern .NET) reads
///    <see cref="ProduceSingleFile"/> + <see cref="ReadyToRun"/>;
///  • <see cref="MsBuildPublisher"/> (.NET Framework Web Applications)
///    reads <see cref="Precompile"/> + <see cref="PrecompileOptions"/> +
///    <see cref="ExcludeAppData"/>.
/// Options irrelevant to the selected toolchain are left null and ignored.
/// </summary>
public sealed record PublishSettings(
    string ProjectPath,
    string? TargetFramework,
    bool SelfContained,
    string Configuration = "Release",
    string? OutputDirectory = null,
    string? AdditionalArguments = null,
    // ---- Modern .NET (dotnet publish) structured options ----
    // VS publish wizard: "Produce Single file" → -p:PublishSingleFile=true
    bool? ProduceSingleFile = null,
    // VS publish wizard: "Enable ReadyToRun compilation" → -p:PublishReadyToRun=true
    bool? ReadyToRun = null,
    // ---- .NET Framework Web Applications (msbuild + WPP) options ----
    // VS publish wizard: "Precompile during publishing" (+ Configure…).
    bool? Precompile = null,
    // The precompile sub-options from the VS "Precompile Options" dialog.
    WebPrecompileOptions? PrecompileOptions = null,
    // VS publish wizard: "Exclude files from the App_Data folder".
    bool? ExcludeAppData = null)
{
    /// <summary>Validates the shape of the settings (existence of the
    /// project path is checked at publish time so the error message can
    /// include the resolved path).</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
            throw new ArgumentException("ProjectPath must be set (csproj file or the folder containing it).");
        if (!string.IsNullOrWhiteSpace(Configuration) && Configuration!.Contains(' '))
            throw new ArgumentException("Configuration must not contain spaces.");
    }
}

public sealed record PublishResult(
    bool Success,
    int ExitCode,
    bool TimedOut,
    string OutputDirectory,
    IReadOnlyList<string> OutputLines,
    string? ErrorSummary);

/// <summary>
/// Runs <c>dotnet publish</c> as a child process on the Packager machine,
/// streaming merged stdout/stderr line-by-line (for the UI progress pane)
/// and returning a structured result. Handles timeout and cancellation by
/// killing the whole process tree — a hung publish must never wedge the UI.
/// </summary>
public static class DotNetPublisher
{
    public static string BuildArguments(PublishSettings settings)
    {
        settings.Validate();
        var sb = new StringBuilder("publish ");
        sb.Append(Quote(settings.ProjectPath));

        if (!string.IsNullOrWhiteSpace(settings.Configuration))
            sb.Append(" -c ").Append(settings.Configuration);

        // `--self-contained` requires a target framework to be meaningful —
        // only pass it when the project default TFM is being overridden, or
        // when going self-contained (which forces the check anyway).
        if (!string.IsNullOrWhiteSpace(settings.TargetFramework))
            sb.Append(" -f ").Append(settings.TargetFramework);
        if (settings.SelfContained || !string.IsNullOrWhiteSpace(settings.TargetFramework))
            sb.Append(" --self-contained ").Append(settings.SelfContained ? "true" : "false");

        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
            sb.Append(" -o ").Append(Quote(settings.OutputDirectory!));

        // Structured publish options (Visual Studio publish wizard parity).
        // Appended before the caller's verbatim AdditionalArguments so a
        // power user can still override/extend them from the free-text box.
        if (settings.ProduceSingleFile == true)
            sb.Append(" -p:PublishSingleFile=true");
        if (settings.ReadyToRun == true)
            sb.Append(" -p:PublishReadyToRun=true");

        if (!string.IsNullOrWhiteSpace(settings.AdditionalArguments))
            sb.Append(' ').Append(settings.AdditionalArguments);

        return sb.ToString();
    }

    /// <summary>Locates dotnet.exe / dotnet: DOTNET_ROOT first (the sandbox
    /// and typical custom installs), then PATH via a direct probe of the
    /// well-known Windows install locations, then plain "dotnet" and trust
    /// the OS PATH lookup.</summary>
    public static string? ResolveDotNetExecutable()
    {
        var exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, exeName);
            if (File.Exists(candidate)) return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate)) return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var dir in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
                     })
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        else
        {
            foreach (var candidate in new[] { "/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet", "/usr/local/share/dotnet/dotnet" })
                if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static async Task<PublishResult> PublishAsync(
        PublishSettings settings,
        Action<string>? onOutputLine = null,
        int timeoutMinutes = 10,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        var dotnet = ResolveDotNetExecutable()
            ?? throw new InvalidOperationException(
                "Could not locate the dotnet executable. Install the .NET SDK or set DOTNET_ROOT.");

        var psi = new ProcessStartInfo
        {
            FileName = dotnet,
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
                Array.Empty<string>(), "Failed to start the dotnet process.");

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
                // Tree kill: dotnet spawns MSBuild/node children that must
                // not outlive the attempt.
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

    private static string Quote(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}
