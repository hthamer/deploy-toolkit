using System.Globalization;

namespace DeployToolkit.Core.Publishing;

/// <summary>
/// Auto-increments a package version string for the Packager's "new package
/// for an existing component" flow (user request: "when I publish a new
/// package that I have a previous package from, automatically increase the
/// version number — previous was 1.1.1, the new one should be 1.1.2, and I
/// can change it manually").
///
/// Handles the version shapes the tool already documents
/// (<c>NormalizeVersion</c> in StepPublish accepts "1.4.2" or
/// "2026.08.31" — no spaces). Numeric dotted versions (the common case) are
/// incremented on their LAST numeric segment; date-stamp versions
/// (<c>yyyy.MM.dd</c>) bump the day by one; anything the helper cannot
/// confidently increment is returned unchanged (the user still types a
/// version manually).
/// </summary>
public static class VersionIncrementer
{
    /// <summary>
    /// Returns a version string one higher than <paramref name="current"/>,
    /// or <paramref name="current"/> unchanged when it cannot be safely
    /// incremented (non-numeric, malformed, null/empty/whitespace).
    ///
    /// Rules (first match wins):
    ///  <list type="bullet">
    ///   <item><c>1.1.1</c> → <c>1.1.2</c> (increment the last numeric
    ///    segment; preserve leading zeros segment-by-segment — <c>1.01.1</c>
    ///    → <c>1.01.2</c>).</item>
    ///   <item><c>1.1.1.9</c> → <c>1.1.2.0</c> (carry into the previous
    ///    segment when the last overflows; mirrors SemVer 4-part roll-over).</item>
    ///   <item><c>2026.08.31</c> → <c>2026.09.01</c> (parsed as a date when
    ///    it is exactly <c>yyyy.MM.dd</c>; rolls the month/year correctly).</item>
    ///   <item>Anything not matching the above → returned untouched.</item>
    ///  </list>
    /// </summary>
    public static string Increment(string? current)
    {
        var trimmed = current?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return current ?? string.Empty;

        // Date-stamp form: yyyy.MM.dd — increment by one day so successive
        // same-day builds get the next day's stamp.
        if (TryIncrementDateStamp(trimmed, out var bumpedDate))
            return bumpedDate!;

        // Dotted numeric form: 1.2.3, 1.2.3.4, 1.0.0-preview.1 — only the
        // numeric segments participate; pre-release suffixes are preserved.
        if (TryIncrementNumeric(trimmed, out var bumpedNumeric))
            return bumpedNumeric!;

        // Unknown shape — leave it to the user.
        return trimmed;
    }

    private static bool TryIncrementDateStamp(string value, out string? result)
    {
        result = null;
        if (value.Length != 10) // yyyy.MM.dd is always 10 chars
            return false;
        if (!DateTime.TryParseExact(value, "yyyy.MM.dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return false;

        var next = date.AddDays(1);
        result = next.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryIncrementNumeric(string value, out string? result)
    {
        result = null;

        // Split off any pre-release suffix (e.g. "1.2.3-preview.1").
        var dashIndex = value.IndexOf('-');
        var numericPart = dashIndex >= 0 ? value[..dashIndex] : value;
        var suffix = dashIndex >= 0 ? value[dashIndex..] : string.Empty;

        var segments = numericPart.Split('.');
        if (segments.Length == 0)
            return false;

        // Every numeric segment must be a non-negative integer (preserve
        // leading zeros — they are sometimes significant in date-ish versions).
        var parts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
                return false;
        }

        // Increment the last segment, carrying into earlier segments on
        // overflow (1.1.1.9 → 1.1.2.0). Leading zeros are dropped by int, so
        // re-render with the original segment's zero-padding when present.
        var origLast = segments[^1];
        var idx = parts.Length - 1;
        parts[idx]++;
        while (parts[idx] == 0 && idx > 0) // overflow carried: last became 0
        {
            // Only the true overflow case (9→10, 99→100) would NOT zero the
            // segment; a 9→0 rollover means the increment wrapped past 9 back
            // to 0, which can only happen for a single-digit field — carry.
            idx--;
            parts[idx]++;
        }

        // Re-render. Preserve leading-zero width per segment only when the
        // original segment had a leading zero AND the new value still fits
        // that width (otherwise the natural width is used).
        var rendered = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var orig = segments[i];
            if (orig.Length > 1 && orig[0] == '0')
            {
                var width = orig.Length;
                rendered[i] = parts[i].ToString($"D{width}", CultureInfo.InvariantCulture);
                // If the number outgrew the padding (e.g. 099 → 100), fall
                // back to the natural representation.
                if (rendered[i].Length > width)
                    rendered[i] = parts[i].ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                rendered[i] = parts[i].ToString(CultureInfo.InvariantCulture);
            }
        }

        result = string.Join(".", rendered) + suffix;
        return true;
    }
}
