using DeployToolkit.AppKit;
using DeployToolkit.Core.Config;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 4 (plan §10 step 5): this release's appsettings key/value delta,
/// edited through AppKit's <see cref="KeyValueDeltaGrid"/> (not raw JSON).
/// The Deployer merges these keys into appsettings.json on the target after
/// showing a before/after diff; Azure targets receive them via the
/// Configuration API instead.
///
/// <b>Auto-seed (user request)</b>: on entry, the grid is pre-populated with
/// EVERY key found in the published <c>appsettings.json</c> (flattened to
/// dotted keys by <see cref="AppSettingsKeyReader"/>), so the user sees the
/// full current configuration and edits only the values that change in this
/// release. Keys the user already added in this session are preserved
/// (manual edits win). The published <c>appsettings.json</c> is never shipped
/// in the package (see <c>SensitiveFileFilter</c>) — only the delta edited
/// here is merged into the target's copy by the Deployer.
/// </summary>
internal sealed class StepDelta : WizardStep
{
    private readonly KeyValueDeltaGrid _deltaGrid;
    private readonly Label _seedLabel;

    public StepDelta(PackagerWizardForm wizard, PackageDraft draft)
        : base(wizard, draft)
    {
        _deltaGrid = new KeyValueDeltaGrid { Dock = DockStyle.Fill };

        var note = new Label
        {
            Text = "Values are stored as JSON when they parse (numbers, true/false, null removes the key, {…}/[…]); " +
                   "otherwise as strings. These keys are merged into appsettings.json by the Deployer after showing a diff. " +
                   "The grid is pre-filled with every key from the published appsettings.json — edit the values that change " +
                   "this release (add/remove keys as needed).",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 56,
            ForeColor = Color.DimGray,
            Padding = new Padding(2, 2, 2, 6),
        };

        _seedLabel = new Label
        {
            Text = string.Empty,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 20,
            ForeColor = Color.DimGray,
            Padding = new Padding(2, 0, 2, 2),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(note);
        layout.Controls.Add(_seedLabel);
        layout.Controls.Add(_deltaGrid);

        Controls.Add(layout);
    }

    public override string Title => "4. App settings delta";

    public override string Hint =>
        "Pre-filled with every key from the published appsettings.json — edit the values that change in this release.";

    public override bool CanProceed => true;

    public override void OnEnter()
    {
        // Auto-seed: flatten the PUBLISHED appsettings.json into dotted keys
        // and pre-fill the grid. Keys the user already added this session are
        // preserved (manual edits win) — re-entry after a Back navigation must
        // not wipe the user's work. Keys the published file has but the user
        // removed are NOT re-added (their removal is an explicit edit).
        var seeded = SeedFromPublishedAppSettings();
        var existing = Draft.AppSettingsDelta;
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Published keys first (so the grid order matches the file), then any
        // session-only keys the user added that aren't in the published file.
        foreach (var (key, valueText) in seeded)
        {
            if (existing.ContainsKey(key))
                merged[key] = existing[key]; // user already edited this key — keep their value
            else
                merged[key] = KeyValueDeltaGrid.ParseValue(valueText);
        }
        foreach (var (key, value) in existing)
        {
            if (!merged.ContainsKey(key))
                merged[key] = value; // session-only key (user added) — preserve
        }

        _deltaGrid.LoadDelta(merged);
        _seedLabel.Text = seeded.Count > 0
            ? $"Pre-filled {seeded.Count} key(s) from the published appsettings.json."
            : "No appsettings.json found in the publish output — add keys manually.";
    }

    public override void OnLeave() => Commit();

    /// <summary>Wires the grid contents into the draft (also called right
    /// before a build so the very last edit is always captured).</summary>
    public void Commit() => Draft.AppSettingsDelta = _deltaGrid.GetDelta();

    /// <summary>Reads the published <c>appsettings.json</c> from the publish
    /// output root (if present) and flattens it to dotted keys. Returns an
    /// empty dictionary when the file is absent or unreadable — the caller
    /// falls through to an empty seed.</summary>
    private Dictionary<string, string> SeedFromPublishedAppSettings()
    {
        if (string.IsNullOrEmpty(Draft.PublishOutputRoot))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var path = Path.Combine(Draft.PublishOutputRoot, "appsettings.json");
        var keys = AppSettingsKeyReader.ReadKeysFromFile(path);
        return new Dictionary<string, string>(keys, StringComparer.Ordinal);
    }
}

