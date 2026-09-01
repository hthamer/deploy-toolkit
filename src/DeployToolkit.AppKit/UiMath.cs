namespace DeployToolkit.AppKit;

/// <summary>
/// WinForms layout math, deliberately free of System.Windows.Forms types so
/// the headless AppKit self-test (plain net8.0) can verify it.
///
/// WHY THIS EXISTS: <c>SplitContainer.Panel1MinSize</c> /
/// <c>Panel2MinSize</c> / <c>SplitterDistance</c> setters re-clamp the
/// splitter distance against the CURRENT container size immediately, and the
/// WinForms runtime throws
/// <c>InvalidOperationException("SplitterDistance must be between
/// Panel1MinSize and Width - Panel2MinSize.")</c> whenever the container is
/// narrower/tighter than Panel1MinSize + Panel2MinSize + SplitterWidth. A
/// freshly constructed SplitContainer is only 150px wide, so assigning min
/// sizes before layout is a guaranteed crash (the ClientsScreen open bug).
/// Screens must defer split configuration until the container has real size
/// and route every assignment through the helpers below.
/// </summary>
public static class UiMath
{
    /// <summary>
    /// True when <paramref name="desiredDistance"/> is legal for the given
    /// container size and still legal AFTER the panel min sizes are raised
    /// (the min-size setters re-clamp the live distance). Use as the gate
    /// BEFORE touching the SplitContainer; when false, simply skip and retry
    /// on the next size change — never force the assignment.
    /// </summary>
    public static bool CanApplySplit(
        int containerSize, int panel1MinSize, int panel2MinSize, int splitterWidth, int desiredDistance)
    {
        var max = containerSize - Math.Max(panel2MinSize, 0) - Math.Max(splitterWidth, 0);
        return desiredDistance >= Math.Max(panel1MinSize, 0) && desiredDistance <= max;
    }

    /// <summary>
    /// Clamps <paramref name="desiredDistance"/> into the legal
    /// SplitterDistance range
    /// ([Panel1MinSize, containerSize − Panel2MinSize − SplitterWidth]), or
    /// null when the container is too small to host the split at all — in
    /// which case the caller must skip the assignment entirely.
    /// </summary>
    public static int? SafeSplitterDistance(
        int containerSize, int panel1MinSize, int panel2MinSize, int splitterWidth, int desiredDistance)
    {
        var min = Math.Max(panel1MinSize, 0);
        var max = containerSize - Math.Max(panel2MinSize, 0) - Math.Max(splitterWidth, 0);
        if (max < min)
            return null;
        return Math.Clamp(desiredDistance, min, max);
    }
}
