namespace DeployToolkit.Core.EfCore;

/// <summary>
/// One key/value row of the REST API's runtime settings, stored in the SAME
/// registry database the Packager app uses. Per the toolkit requirement,
/// credential- and rotation-related settings live in the DATABASE — never in
/// appsettings.json or any other configuration file — so operators can tune
/// the running API with a plain UPDATE while it serves traffic:
///
///   UPDATE ApiSettings SET Value = '60'
///     WHERE Key = 'Auth.Rotation.IntervalMinutes';
///
/// The API reads these rows on every rotation pass (and while idle, at a
/// short poll granularity), so edits take effect within moments — no
/// restart, no redeploy, no config drift between instances.
///
/// Keys currently used by <c>DeployToolkit.Api</c> (see its
/// <c>ApiSettingKeys</c>): <c>Auth.Rotation.Enabled</c>,
/// <c>Auth.Rotation.IntervalMinutes</c>, <c>Auth.Rotation.PasswordLength</c>,
/// <c>Auth.Rotation.Usernames</c>, <c>Auth.Rotation.LogPasswords</c>,
/// <c>Auth.Rotation.LastRunUtc</c>, <c>Auth.Rotation.NextRunUtc</c>.
/// Unknown keys are ignored by the API — callers may store their own.
/// </summary>
public sealed class ApiSetting
{
    /// <summary>Setting key (hierarchical dotted convention, e.g.
    /// <c>Auth.Rotation.IntervalMinutes</c>). Primary key.</summary>
    public required string Key { get; init; }

    /// <summary>Setting value, stored as an invariant-culture string
    /// (booleans "true"/"false", numbers without separators, UTC timestamps
    /// in ISO-8601 round-trip "O" format).</summary>
    public required string Value { get; set; }

    /// <summary>When the row was created or last written (UTC) — cheap audit
    /// of who changed the schedule when (correlate with SQL audit if needed).</summary>
    public required DateTimeOffset UpdatedUtc { get; set; }
}
