using System.Security.Cryptography;
using DeployToolkit.Api.Infrastructure;
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

/// <summary>
/// Background service that replaces the API users' passwords on a schedule
/// (default: every 45 minutes — per user requirement).
///
/// Database is the single source of truth (user requirement — no
/// appsettings.json, no state file):
///   * SETTINGS — every cycle (and at least every ~15s while idle) the
///     service re-reads the <c>ApiSettings</c> table
///     (<c>Auth.Rotation.*</c> keys, see <see cref="ApiSettingKeys"/>), so
///     a plain <c>UPDATE ApiSettings SET Value = '60' WHERE Key =
///     'Auth.Rotation.IntervalMinutes'</c> retunes the schedule on the
///     running service.
///   * CREDENTIALS — after every rotation the new username/password pair is
///     REGISTERED IN THE DATABASE: the PBKDF2 hash goes to
///     <c>ApiUsers.PasswordHash</c> and the plaintext to
///     <c>ApiCredentialLogs</c> (latest row per username = the current
///     working credential). The former App_Data/current-api-password.json
///     file is gone — operators and clients read the credential with a
///     plain SELECT.
///
/// Per cycle: one fresh crypto-random password is generated and applied to
/// every (optionally filtered) ACTIVE ApiUser, a credential row per user is
/// written, and <c>Auth.Rotation.LastRunUtc</c> /
/// <c>Auth.Rotation.NextRunUtc</c> are advanced — hash + credential log +
/// schedule commit together in ONE SaveChanges (implicit transaction).
///
/// A failed cycle (e.g. DB blip) is logged and retried after a short
/// backoff — the previously stored password stays valid until a rotation
/// succeeds, which keeps the API available at the cost of a delayed
/// rotation. A cycle with zero matching users still advances the schedule
/// (no busy spinning).
/// </summary>
public sealed class PasswordRotationService : BackgroundService
{
    /// <summary>Floor for <c>Auth.Rotation.IntervalMinutes</c> — 0.5
    /// minutes. Guards against a settings typo churning hashes nonstop.</summary>
    private const double MinimumIntervalMinutes = 0.5;

    /// <summary>While waiting for the next due rotation the service re-reads
    /// the settings at least this often — edits to IntervalMinutes /
    /// Enabled / NextRunUtc take effect within one slice, no restart.</summary>
    private static readonly TimeSpan SettingsPollSlice = TimeSpan.FromSeconds(15);

    /// <summary>Idle poll while rotation is disabled via ApiSettings —
    /// re-enabling takes effect within this window.</summary>
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Wait before retrying after a failed cycle.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PasswordRotationService> _logger;

    public PasswordRotationService(
        IServiceScopeFactory scopeFactory,
        ILogger<PasswordRotationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Password rotation service started — settings are read from the ApiSettings " +
            "table (Auth.Rotation.* keys) every cycle; after each rotation the current " +
            "credential is registered in ApiCredentialLogs.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = await ReadSettingsAsync(stoppingToken);

                if (!settings.Enabled)
                {
                    await Task.Delay(DisabledPollInterval, stoppingToken);
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                if (settings.NextRunUtc is { } next && next > now)
                {
                    // Sleep AT MOST one poll slice, then loop back and
                    // re-read the settings — an operator's UPDATE to
                    // IntervalMinutes / Enabled / NextRunUtc takes effect
                    // within one slice, no restart.
                    var remaining = next - now;
                    await Task.Delay(
                        remaining > SettingsPollSlice ? SettingsPollSlice : remaining,
                        stoppingToken);
                    continue;
                }

                // Due (or never scheduled): rotate now.
                var rotated = await RotateOnceAsync(settings, stoppingToken);
                if (!rotated)
                    await Task.Delay(FailureBackoff, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — nothing to do.
        }
    }

    /// <summary>Reads the rotation settings from the database. On failure,
    /// returns the safe <see cref="RotationSettings.Disabled"/> standby so
    /// the loop pauses and retries instead of acting on garbage.</summary>
    private async Task<RotationSettings> ReadSettingsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ApiSettingsStore>();
            return await store.GetRotationSettingsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not read rotation settings from the ApiSettings table; " +
                "pausing briefly before retrying.");
            return RotationSettings.Disabled;
        }
    }

    /// <summary>One rotation cycle. Returns <c>false</c> when the cycle
    /// failed — the old password stays valid and the cycle is retried after
    /// the backoff.</summary>
    private async Task<bool> RotateOnceAsync(RotationSettings settings, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RegistryDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<ApiSettingsStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

            var now = DateTimeOffset.UtcNow;
            var users = await db.ApiUsers
                .Where(u => u.IsActive)
                .ToListAsync(stoppingToken);

            var filter = ParseUsernameFilter(settings.Usernames);
            if (filter is { Count: > 0 })
                users = users.Where(u => filter.Contains(u.Username.ToLowerInvariant())).ToList();

            if (users.Count == 0)
                _logger.LogWarning(
                    "Password rotation found no active API users to rotate — the schedule " +
                    "still advances. Insert an ApiUsers row or change Auth.Rotation.Usernames " +
                    "in ApiSettings.");

            // ONE fresh password per cycle, applied to every rotated user —
            // matches "change the password every 45 minutes" and keeps
            // distribution simple.
            var newPassword = RandomPasswordGenerator.Generate(settings.PasswordLength);
            foreach (var user in users)
            {
                user.PasswordHash = hasher.Hash(newPassword);
                user.PasswordChangedUtc = now;

                // The credential is REGISTERED IN THE DATABASE (user
                // requirement) — latest row per username is what
                // operators/clients read out after each rotation.
                db.ApiCredentialLogs.Add(new ApiCredentialLog
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Username = user.Username,
                    Password = newPassword,
                    Reason = "ScheduledRotation",
                    CreatedUtc = now,
                });
            }

            var interval = TimeSpan.FromMinutes(
                Math.Max(MinimumIntervalMinutes, settings.IntervalMinutes));
            await store.SetAsync(ApiSettingKeys.RotationLastRunUtc, now.ToString("O"), stoppingToken);
            await store.SetAsync(ApiSettingKeys.RotationNextRunUtc, now.Add(interval).ToString("O"), stoppingToken);

            // Single SaveChanges = one implicit transaction: password hashes,
            // credential log rows and the schedule all move together.
            await db.SaveChangesAsync(stoppingToken);

            _logger.LogInformation(
                "Password rotation completed for {Count} API user(s) at {AtUtc:O}; " +
                "next rotation {NextUtc:O}; the new credential is registered in " +
                "ApiCredentialLogs.",
                users.Count, now, now.Add(interval));

            if (settings.LogPasswords)
            {
                foreach (var user in users)
                    _logger.LogInformation(
                        "NEW password for API user '{Username}': {Password}",
                        user.Username, newPassword);
            }

            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep the host alive: the old password stays valid until this
            // cycle is retried after the backoff.
            _logger.LogError(ex,
                "Password rotation cycle failed; the previous password stays valid. " +
                "Retrying after a short backoff.");
            return false;
        }
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
