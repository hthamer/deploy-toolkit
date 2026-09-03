using System.Globalization;
using DeployToolkit.Core.EfCore;
using Microsoft.EntityFrameworkCore;

namespace DeployToolkit.Api.Infrastructure;

/// <summary>
/// Canonical <see cref="ApiSetting"/> keys used by <c>DeployToolkit.Api</c>.
/// Per the toolkit requirement, ALL of these live in the registry DATABASE —
/// appsettings.json carries none of them. Operators edit them with plain
/// SQL while the API runs; the rotation service re-reads them every cycle
/// (and at least every ~15s while idle), so changes take effect without a
/// restart.
/// </summary>
public static class ApiSettingKeys
{
    /// <summary>Master switch ("true"/"false"). When false the rotation
    /// service idles and passwords change only when edited by hand.</summary>
    public const string RotationEnabled = "Auth.Rotation.Enabled";

    /// <summary>How often the password is replaced, in MINUTES (default 45,
    /// per requirement; fractional values allowed; floor 0.5 min).</summary>
    public const string RotationIntervalMinutes = "Auth.Rotation.IntervalMinutes";

    /// <summary>Length of generated passwords (16–128, default 24).</summary>
    public const string RotationPasswordLength = "Auth.Rotation.PasswordLength";

    /// <summary>Optional comma-separated allow-list of usernames to rotate.
    /// Empty = every ACTIVE ApiUser row.</summary>
    public const string RotationUsernames = "Auth.Rotation.Usernames";

    /// <summary>"true" also logs new passwords at Information level (console
    /// visibility). Default false — ApiCredentialLogs is the distribution
    /// channel; enable only for local debugging.</summary>
    public const string RotationLogPasswords = "Auth.Rotation.LogPasswords";

    /// <summary>ISO-8601 timestamp of the last completed rotation (written
    /// by the service; also useful as a plain-SELECT health check).</summary>
    public const string RotationLastRunUtc = "Auth.Rotation.LastRunUtc";

    /// <summary>ISO-8601 timestamp when the next rotation is due. Missing or
    /// in the past = due now (which is exactly right when adopting an
    /// existing registry).</summary>
    public const string RotationNextRunUtc = "Auth.Rotation.NextRunUtc";

    /// <summary>Values seeded into <c>ApiSettings</c> when the key is
    /// missing (first run). Schedule timestamps are intentionally NOT
    /// seeded — a missing <see cref="RotationNextRunUtc"/> means "due now".</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults =
        new Dictionary<string, string>
        {
            [RotationEnabled] = "true",
            [RotationIntervalMinutes] = "45",
            [RotationPasswordLength] = "24",
            [RotationUsernames] = string.Empty,
            [RotationLogPasswords] = "false",
        };
}

/// <summary>Strongly-typed snapshot of the rotation-related
/// <c>ApiSettings</c> rows, read fresh by the rotation service on every
/// pass and by the authenticate endpoint (next-rotation timestamp).</summary>
public sealed record RotationSettings(
    bool Enabled,
    double IntervalMinutes,
    int PasswordLength,
    string Usernames,
    bool LogPasswords,
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? NextRunUtc)
{
    /// <summary>Safe standby snapshot used when the settings table itself
    /// cannot be read (DB blip): rotation pauses and retries instead of
    /// acting on garbage.</summary>
    public static RotationSettings Disabled { get; } =
        new(false, 45, 24, string.Empty, false, null, null);
}

/// <summary>
/// Typed accessor over the <c>ApiSettings</c> table (user requirement:
/// rotation settings are saved in the DATABASE, not appsettings.json).
///
/// Registered SCOPED and constructed with the caller's
/// <see cref="RegistryDbContext"/> — so <see cref="SetAsync"/> upserts join
/// the caller's unit of work and commit atomically with it (the rotation
/// service relies on that: hash + credential log + schedule in one
/// SaveChanges).
/// </summary>
public sealed class ApiSettingsStore
{
    private readonly RegistryDbContext _db;

    public ApiSettingsStore(RegistryDbContext db) => _db = db;

    /// <summary>Raw value of one setting, or null when unset.</summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        (await _db.ApiSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    /// <summary>Typed UTC timestamp of one setting, or null when the key is
    /// missing/unparseable ("never scheduled"). Used e.g. by the
    /// authenticate endpoint to report <c>passwordRotatesAtUtc</c>.</summary>
    public async Task<DateTimeOffset?> GetDateTimeUtcAsync(string key, CancellationToken ct = default) =>
        ParseTimestamp(await GetAsync(key, ct) ?? string.Empty);

    /// <summary>Insert-or-update one setting. Uses the same tracked context
    /// as the caller — persist it with your own SaveChangesAsync.</summary>
    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var row = await _db.ApiSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            _db.ApiSettings.Add(new ApiSetting
            {
                Key = key,
                Value = value,
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.Value = value;
            row.UpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Seeds any missing default settings (first run). Existing
    /// rows are never overwritten — operator edits win.</summary>
    public async Task EnsureDefaultsAsync(CancellationToken ct = default)
    {
        var existing = await _db.ApiSettings
            .Select(s => s.Key)
            .ToListAsync(ct);
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);

        var added = false;
        foreach (var (key, value) in ApiSettingKeys.Defaults)
        {
            if (existingSet.Contains(key))
                continue;
            _db.ApiSettings.Add(new ApiSetting
            {
                Key = key,
                Value = value,
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
            added = true;
        }

        if (added)
            await _db.SaveChangesAsync(ct);
    }

    /// <summary>One query → typed snapshot of all rotation settings.
    /// Unparseable/missing values fall back to the documented defaults.</summary>
    public async Task<RotationSettings> GetRotationSettingsAsync(CancellationToken ct = default)
    {
        var map = await _db.ApiSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        string Raw(string key) => map.TryGetValue(key, out var value) ? value : string.Empty;

        return new RotationSettings(
            Enabled: ParseBool(Raw(ApiSettingKeys.RotationEnabled), fallback: true),
            IntervalMinutes: ParseDouble(Raw(ApiSettingKeys.RotationIntervalMinutes), fallback: 45),
            PasswordLength: ParseInt(Raw(ApiSettingKeys.RotationPasswordLength), fallback: 24),
            Usernames: Raw(ApiSettingKeys.RotationUsernames),
            LogPasswords: ParseBool(Raw(ApiSettingKeys.RotationLogPasswords), fallback: false),
            LastRunUtc: ParseTimestamp(Raw(ApiSettingKeys.RotationLastRunUtc)),
            NextRunUtc: ParseTimestamp(Raw(ApiSettingKeys.RotationNextRunUtc)));
    }

    // ---- invariant-culture parsers (shared by the single-query snapshot
    // and any future per-key reads) -------------------------------------

    internal static bool ParseBool(string raw, bool fallback) =>
        string.IsNullOrWhiteSpace(raw) ? fallback
        : bool.TryParse(raw, out var parsed) ? parsed
        : raw.Trim() == "1";

    internal static double ParseDouble(string raw, double fallback) =>
        string.IsNullOrWhiteSpace(raw) ? fallback
        : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    internal static int ParseInt(string raw, int fallback) =>
        string.IsNullOrWhiteSpace(raw) ? fallback
        : int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    internal static DateTimeOffset? ParseTimestamp(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                  DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
}
