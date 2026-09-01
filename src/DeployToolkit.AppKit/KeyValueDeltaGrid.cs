using System.Text.Json;
using DeployToolkit.Core.Config;

namespace DeployToolkit.AppKit;

/// <summary>
/// Editable key/value delta editor (plan §10 step 5: "simple key/value grid
/// for this release's config changes — not raw JSON editing"). Two columns
/// (Key, Value) plus Add/Remove buttons.
///
/// Value parsing rule (see <see cref="ParseValue"/>): the trimmed value text
/// is attempted as JSON — when it parses, the resulting object is used
/// (numbers → long/double, true/false → bool, <c>null</c> → key removal,
/// <c>{…}</c>/<c>[…]</c> → JsonElement); when it does not parse, the text is
/// treated as a plain string. Rows with a null/empty/whitespace key are
/// ignored by <see cref="GetDelta"/>. Duplicate keys: last row wins.
/// </summary>
public sealed class KeyValueDeltaGrid : UserControl
{
    private readonly DataGridView _grid;
    private readonly Button _addButton;
    private readonly Button _removeButton;

    public KeyValueDeltaGrid()
    {
        _grid = new DataGridView { Dock = DockStyle.Fill };
        AppTheme.StyleGrid(_grid, readOnly: false);
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Key",
            HeaderText = "Key",
            DataPropertyName = "Key",
            FillWeight = 40,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Value",
            HeaderText = "Value",
            DataPropertyName = "Value",
            FillWeight = 60,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.DefaultCellStyle.SelectionBackColor = _grid.DefaultCellStyle.BackColor;
        _grid.DefaultCellStyle.SelectionForeColor = _grid.DefaultCellStyle.ForeColor;

        var hint = new Label
        {
            Text = "Values may be plain text or JSON (true, 42, 1.5, null, {…}). null removes the key.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 22,
            ForeColor = Color.DimGray,
            Padding = new Padding(2, 4, 2, 2),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 2, 0, 4),
            Height = 34,
            WrapContents = false,
        };
        _addButton = new Button { Text = "Add" };
        _removeButton = new Button { Text = "Remove" };
        AppTheme.StyleButton(_addButton);
        AppTheme.StyleButton(_removeButton);
        _addButton.Click += (_, _) => _grid.Rows.Add(string.Empty, string.Empty);
        _removeButton.Click += (_, _) =>
        {
            if (_grid.CurrentRow is { IsNewRow: false } row)
                _grid.Rows.Remove(row);
        };
        buttons.Controls.Add(_addButton);
        buttons.Controls.Add(_removeButton);

        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(hint);
    }

    /// <summary>Number of rows that would contribute to
    /// <see cref="GetDelta"/> (non-empty key).</summary>
    public int Count
    {
        get
        {
            var count = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                if (!string.IsNullOrWhiteSpace(row.Cells[0].Value as string)) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Collects the grid contents into the delta dictionary consumed by
    /// <see cref="AppSettingsMerger"/> (and stored in the manifest's
    /// AppSettingsDelta). Keys are trimmed; null/empty/whitespace-keyed rows
    /// are skipped; values go through the JSON-or-string rule documented on
    /// the type. A delta value of <c>null</c> (JSON literal "null") removes
    /// that key from the target appsettings.
    /// </summary>
    public IReadOnlyDictionary<string, object?> GetDelta()
    {
        var delta = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;

            var key = (row.Cells[0].Value as string)?.Trim();
            if (string.IsNullOrEmpty(key)) continue;

            var raw = (row.Cells[1].Value as string)?.Trim();
            delta[key] = ParseValue(raw); // duplicates: last row wins
        }
        return delta;
    }

    /// <summary>Replaces the grid contents with <paramref name="delta"/>.
    /// String values are shown as plain text; everything else is shown as its
    /// JSON representation (null → "null").</summary>
    public void LoadDelta(IReadOnlyDictionary<string, object?> delta)
    {
        _grid.Rows.Clear();
        foreach (var (key, value) in delta)
        {
            var text = value switch
            {
                null => "null",
                string s => s,
                _ => JsonSerializer.Serialize(value),
            };
            _grid.Rows.Add(key, text);
        }
    }

    /// <summary>
    /// Thin wrapper over <see cref="AppSettingsMerger.Preview"/> for the
    /// before/after confirmation UI: computes which keys
    /// <see cref="GetDelta"/> would change on
    /// <paramref name="existingJson"/> (null/empty = fresh file) without
    /// writing anything.
    /// </summary>
    public IReadOnlyList<AppSettingsChange> PreviewAgainst(string? existingJson)
        => AppSettingsMerger.Preview(existingJson ?? "{}", GetDelta());

    /// <summary>JSON-or-string value rule (documented on the type). Public so
    /// the Packager's StepDelta can parse auto-seeded appsettings values
    /// (rendered as JSON by <c>AppSettingsKeyReader</c>) into the typed form
    /// <see cref="GetDelta"/> produces — same rule, one place.</summary>
    public static object? ParseValue(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty; // empty text = empty string (JSON "" would also work; both yield "")

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(raw);
            return FromJsonElement(element);
        }
        catch (JsonException)
        {
            return raw; // not JSON → plain string
        }
    }

    private static object? FromJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        _ => element.Clone(), // object/array — keep as JsonElement (STJ round-trips it everywhere)
    };
}
