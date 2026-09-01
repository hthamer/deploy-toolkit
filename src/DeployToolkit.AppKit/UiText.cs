namespace DeployToolkit.AppKit;

/// <summary>Pure formatting helpers shared by the UI layer. Lives in the
/// pure (net8.0) asset so headless self-tests can pin its behavior.</summary>
public static class UiText
{
    /// <summary>Compact elapsed-clock rendering for busy dialogs and logs:
    /// "42 s" under a minute, "3:07 min" under an hour, "1:02:03 h" beyond.
    /// The unit suffix makes it unambiguous that the operation is STILL
    /// counting — the whole point of showing elapsed time in a busy state
    /// is to distinguish "slow" from "frozen".</summary>
    public static string Elapsed(TimeSpan elapsed)
    {
        var total = (long)Math.Max(0, elapsed.TotalSeconds);
        if (total < 60)
            return $"{total} s";
        if (total < 3600)
            return $"{total / 60}:{total % 60:D2} min";
        return $"{total / 3600}:{total % 3600 / 60:D2}:{total % 60:D2} h";
    }
}
