using System.Security.Cryptography;
using System.Text.Json;
using DeployToolkit.Core.EfCore;
using Microsoft.EntityFrameworkCore;

namespace DeployToolkit.Api.Auth;

/// <summary>
/// Cryptographically random password generator for the rotation service.
/// Draws from an unambiguous alphabet (no O/0, l/1/I lookalikes) using
/// <see cref="RandomNumberGenerator"/> — 24 characters ≈ 130 bits of
/// entropy, comfortably above any brute-force relevant to a 45-minute
/// password lifetime.
/// </summary>
public static class RandomPasswordGenerator
{
    /// <summary>Above the common "upper + lower + digit + symbol" complexity
    /// bar while excluding characters that are hard to read aloud over the
    /// phone or from a console window.</summary>
    public const string Alphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZ" +
        "abcdefghijkmnopqrstuvwxyz" +
        "23456789" +
        "!@#$%^&*-_=+";

    public const int MinimumLength = 16;
    public const int MaximumLength = 128;

    public static string Generate(int length)
    {
        var clamped = Math.Clamp(length, MinimumLength, MaximumLength);
        return new string(RandomNumberGenerator.GetItems<char>(Alphabet, clamped));
    }
}

/// <summary>Options bound from <c>Auth:PasswordRotation</c>
/// (appsettings.json / environment variables).</summary>
public sealed class PasswordRotationOptions
{
    /// <summary>Master switch. When false the service logs one line and
    /// exits; passwords then only change when edited by hand / seed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often every active API user's password is replaced.
    /// Default 45 minutes (user requirement). Values below one minute are
    /// clamped — the floor exists so a config typo can't turn the registry
    /// into a password churn loop.</summary>
    public double IntervalMinutes { get; set; } = 45;

    /// <summary>Delay before the FIRST rotation after startup. The initial
    /// rotation immediately supersedes any password that came from
    /// configuration/seed (a plaintext in appsettings.json should not stay
    /// valid forever). 0 = rotate as soon as the host is up.</summary>
    public int InitialDelaySeconds { get; set; } = 10;

    /// <summary>Length of generated passwords (see
    /// <see cref="RandomPasswordGenerator"/> for the clamping range).</summary>
    public int PasswordLength { get; set; } = 24;

    /// <summary>Optional comma-separated allow-list of usernames to rotate.
    /// Empty = every ACTIVE ApiUser row gets a new password.</summary>
    public string Usernames { get; set; } = string.Empty;

    /// <summary>Write the CURRENT password set to
    /// <c>App_Data/current-api-password.json</c> (overwritten every cycle —
    /// no password history accumulates on disk). This is how operators and
    /// the WinForms clients learn the new password after each rotation.</summary>
    public bool WriteStateFile { get; set; } = true;

    /// <summary>Also log the new passwords at Information level (console /
    /// event log visibility). Turn OFF in hardening reviews — the state
    /// file alone is then the distribution channel.</summary>
    public bool LogPasswords { get; set; } = true;
}

/// <summary>Thread-safe snapshot of what the rotation service last did, so
/// the authenticate endpoint can answer "when does this password stop
/// working?". Single writer (the rotation loop), lock-free reads are fine
/// for DateTimeOffset? reference-sized values.</summary>
public interface IPasswordRotationState
{
    DateTimeOffset? LastRotationUtc { get; }
    DateTimeOffset? NextRotationUtc { get; }
    void Update(DateTimeOffset lastRotationUtc, DateTimeOffset nextRotationUtc);
}

public sealed class PasswordRotationState : IPasswordRotationState
{
    public DateTimeOffset? LastRotationUtc { get; private set; }
    public DateTimeOffset? NextRotationUtc { get; private set; }

    public void Update(DateTimeOffset lastRotationUtc, DateTimeOffset nextRotationUtc)
    {
        LastRotationUtc = lastRotationUtc;
        NextRotationUtc = nextRotationUtc;
    }
}

/// <summary>
/// Background service that replaces the API users' passwords on a schedule
/// (default: every 45 minutes, first pass shortly after startup).
///
/// Why this exists (user requirement): the phase-1 API has no token flow —
/// callers authenticate by presenting a username and password that must
/// match the credentials saved in the registry database. Rotating those
/// credentials on a timer caps the blast radius of any leaked password.
///
/// Per cycle, for every active ApiUser (optionally filtered by
/// <see cref="PasswordRotationOptions.Usernames"/>):
///   1. generate a fresh crypto-random password,
///   2. store its PBKDF2-SHA256 hash (same hasher as logins) and stamp
///      <c>PasswordChangedUtc</c>,
///   3. surface the plaintext ONLY through the configured distribution
///      channels: the state file (overwritten each cycle) and/or the log —
///      the new password has to reach the operator/clients somehow, and the
///      state file is the machine-readable channel while
///      <see cref="PasswordRotationOptions.LogPasswords"/> covers console
///      visibility. Both channels are documented security trade-offs (see
///      README): anyone who can read them can log in until the next cycle.
///
/// A failed cycle (e.g. DB blip) is logged and retried at the NEXT tick —
/// the previously stored password stays valid until a rotation succeeds,
/// which keeps the API available at the cost of a delayed rotation.
/// </summary>
public sealed class PasswordRotationService : BackgroundService
{
    /// <summary>Floor for <see cref="PasswordRotationOptions.IntervalMinutes"/>
    /// — 0.5 minutes. Guards against a misconfig churning hashes nonstop.</summary>
    private const double MinimumIntervalMinutes = 0.5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPasswordRotationState _state;
    private readonly IHostEnvironment _environment;
    private readonly PasswordRotationOptions _options;
    private readonly ILogger<PasswordRotationService> _logger;

    public PasswordRotationService(
        IServiceScopeFactory scopeFactory,
        IPasswordRotationState state,
        IHostEnvironment environment,
        Microsoft.Extensions.Options.IOptions<PasswordRotationOptions> options,
        ILogger<PasswordRotationService> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _environment = environment;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Password rotation is DISABLED (Auth:PasswordRotation:Enabled=false); " +
                "API passwords change only when edited by hand.");
            return;
        }

        var interval = TimeSpan.FromMinutes(
            Math.Max(MinimumIntervalMinutes, _options.IntervalMinutes));
        var initialDelay = TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds));

        _logger.LogInformation(
            "Password rotation ENABLED: first rotation in {InitialDelay} (unless disabled), " +
            "then every {Interval}. New passwords surface in {StateFile} and{LogSuffix} " +
            "per Auth:PasswordRotation options.",
            initialDelay, interval,
            StateFilePath,
            _options.LogPasswords ? " the application log" : " NOT in the log");

        try
        {
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, stoppingToken);

            // Initial pass: replaces anything seeded/configured, then the
            // steady 45-minute cadence takes over.
            await RotateOnceAsync(interval, stoppingToken);

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RotateOnceAsync(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — nothing to do.
        }
    }

    /// <summary>Where the current password set is published (overwritten on
    /// every successful rotation — never a history).</summary>
    public string StateFilePath =>
        Path.Combine(_environment.ContentRootPath, "App_Data", "current-api-password.json");

    private async Task RotateOnceAsync(TimeSpan interval, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RegistryDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

            var now = DateTimeOffset.UtcNow;
            var users = await db.ApiUsers
                .Where(u => u.IsActive)
                .ToListAsync(stoppingToken);

            var filter = ParseUsernameFilter(_options.Usernames);
            if (filter is { Count: > 0 })
                users = users.Where(u => filter.Contains(u.Username.ToLowerInvariant())).ToList();

            if (users.Count == 0)
            {
                _logger.LogWarning(
                    "Password rotation found no active API users to rotate — " +
                    "seed a user or clear the Auth:PasswordRotation:Usernames filter.");
                return;
            }

            var rotated = new List<(string Username, string Password)>(users.Count);
            foreach (var user in users)
            {
                var newPassword = RandomPasswordGenerator.Generate(_options.PasswordLength);
                user.PasswordHash = hasher.Hash(newPassword);
                user.PasswordChangedUtc = now;
                rotated.Add((user.Username, newPassword));
            }

            await db.SaveChangesAsync(stoppingToken);
            _state.Update(lastRotationUtc: now, nextRotationUtc: now.Add(interval));

            _logger.LogInformation(
                "Password rotation completed for {Count} API user(s) at {AtUtc:O}; " +
                "next rotation {NextUtc:O}.",
                rotated.Count, now, now.Add(interval));

            if (_options.LogPasswords)
            {
                foreach (var (username, password) in rotated)
                    _logger.LogInformation(
                        "NEW password for API user '{Username}': {Password}",
                        username, password);
            }

            if (_options.WriteStateFile)
                WriteStateFile(now, now.Add(interval), rotated);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep the host alive: the old password stays valid until the
            // next tick retries the rotation.
            _logger.LogError(ex, "Password rotation cycle failed; will retry at the next tick.");
        }
    }

    private void WriteStateFile(
        DateTimeOffset generatedAtUtc, DateTimeOffset nextRotationUtc,
        List<(string Username, string Password)> rotated)
    {
        var path = StateFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var payload = new
        {
            generatedAtUtc,
            nextRotationUtc,
            users = rotated.Select(r => new { username = r.Username, password = r.Password }),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }

    private static HashSet<string>? ParseUsernameFilter(string raw)
    {
        var items = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => u.ToLowerInvariant())
            .ToArray();
        return items.Length == 0 ? null : new HashSet<string>(items);
    }
}
