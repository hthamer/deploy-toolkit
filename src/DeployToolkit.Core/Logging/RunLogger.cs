using System.Text.Json;

namespace DeployToolkit.Core.Logging;

/// <summary>
/// One structured log entry (plan §8.6). Serialized to the log file as a
/// single JSON line — the "structured (JSON-lines) log per run" the plan
/// requires, and replayable for the live log pane in the Deployer UI.
/// </summary>
public sealed record RunLogEntry(DateTimeOffset TimestampUtc, string Level, string Message);

/// <summary>
/// JSON-lines run logger (plan §8.6): one log file per deployment run under
/// <c>{logRoot}/{client}/{component}/{yyyyMMdd-HHmmss}-{id}.log</c>, one JSON
/// object per line. Also raises <see cref="EntryLogged"/> synchronously so a
/// WinForms UI can mirror entries into a live log pane without re-reading
/// the file.
///
/// Zero dependencies (plain StreamWriter + System.Text.Json), thread-safe
/// for concurrent Log calls from UI/worker threads.
/// </summary>
public sealed class RunLogger : IDisposable
{
    private const int LogIdLength = 8;

    private readonly StreamWriter? _writer;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Full path of the log file backing this run. Safe to store in
    /// the registry's DeploymentRuns.LogPath column.</summary>
    public string LogFilePath { get; }

    /// <summary>Raised for every entry after it is written to disk —
    /// subscribe from the UI for the live log pane.</summary>
    public event Action<RunLogEntry>? EntryLogged;

    public RunLogger(string logRoot, string client, string component)
    {
        var directory = Path.Combine(
            logRoot,
            SanitizeSegment(client),
            SanitizeSegment(component));
        Directory.CreateDirectory(directory);

        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..LogIdLength]}.log";
        LogFilePath = Path.Combine(directory, fileName);

        var stream = new FileStream(LogFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public void Info(string message) => Log("INFO", message);

    public void Warn(string message) => Log("WARN", message);

    public void Error(string message) => Log("ERROR", message);

    /// <summary>Writes one JSON line and raises <see cref="EntryLogged"/>.
    /// Never throws for message content — formatting surprises become part
    /// of the message, not an exception mid-deployment.</summary>
    public void Log(string level, string message)
    {
        var entry = new RunLogEntry(DateTimeOffset.UtcNow, level, message ?? string.Empty);
        var line = JsonSerializer.Serialize(new
        {
            ts = entry.TimestampUtc,
            level = entry.Level,
            msg = entry.Message,
        });

        lock (_gate)
        {
            if (_disposed) return;
            _writer?.WriteLine(line);
        }

        EntryLogged?.Invoke(entry);
    }

    /// <summary>Reads back a previously written log file (e.g. to show an
    /// audit trail in the UI or re-attach to a run's history).</summary>
    public static IReadOnlyList<RunLogEntry> ReadLog(string logFilePath)
    {
        var entries = new List<RunLogEntry>();
        foreach (var line in File.ReadLines(logFilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<LogLineDto>(line);
                if (dto is not null)
                    entries.Add(new RunLogEntry(dto.ts, dto.level ?? "INFO", dto.msg ?? string.Empty));
            }
            catch (JsonException)
            {
                // A line we can't parse is kept verbatim as an unstructured
                // entry rather than dropping audit information on the floor.
                entries.Add(new RunLogEntry(DateTimeOffset.MinValue, "RAW", line));
            }
        }
        return entries;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
        }
    }

    /// <summary>Client/component names come from user input and end up in a
    /// path — strip anything that could escape the log directory or break
    /// the file system.</summary>
    private static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = segment.Trim().Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var cleaned = new string(chars);
        return cleaned.Equals("..", StringComparison.Ordinal) ? "_" : cleaned;
    }

    private sealed record LogLineDto(DateTimeOffset ts, string? level, string? msg);
}
