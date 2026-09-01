using DeployToolkit.AppKit;
using DeployToolkit.Core.IisControl;

namespace DeployToolkit.Deployer;

/// <summary>
/// The plan §6 site/app picker: shown when <see cref="IisTargetResolver"/>
/// cannot resolve a component to a concrete IIS application (no mapping on
/// this machine, no site configured in the registry, or the configured site
/// no longer exists). Lists every live IIS application as picker candidates;
/// the chosen row is saved as the machine-local mapping for next time.
/// </summary>
public sealed class IisTargetPickerDialog : Form
{
    private readonly DataGridView _grid;
    private IisResolvedTarget? _chosen;

    /// <summary>The target built from the selected row; null when cancelled.</summary>
    public IisResolvedTarget? ResultTarget => _chosen;

    public IisTargetPickerDialog(IReadOnlyList<IisApplicationInfo> candidates, string message)
    {
        Text = "Pick the IIS application to deploy into";
        StartPosition = FormStartPosition.CenterParent;
        AppTheme.Apply(this);
        Size = new Size(860, 520);
        MinimumSize = new Size(700, 420);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10, 10, 10, 4),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = message + " Double-click a row to choose.",
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = Color.DarkOrange,
            Margin = new Padding(2, 2, 2, 8),
        });

        _grid = new DataGridView { Dock = DockStyle.Fill };
        AppTheme.StyleGrid(_grid);
        _grid.Columns.Add("site", "Site");
        _grid.Columns.Add("path", "App path");
        _grid.Columns.Add("pool", "App pool");
        _grid.Columns.Add("physical", "Physical path");
        _grid.Columns["site"].FillWeight = 20;
        _grid.Columns["path"].FillWeight = 15;
        _grid.Columns["pool"].FillWeight = 20;
        _grid.Columns["physical"].FillWeight = 45;

        foreach (var app in candidates)
            _grid.Rows.Add(app.SiteName, app.Path, app.AppPoolName ?? "(none)", app.PhysicalPath);

        layout.Controls.Add(_grid, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
            Height = 48,
        };
        var okButton = new Button { Text = "Choose", Enabled = false };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        AppTheme.StyleButton(okButton);
        AppTheme.StyleButton(cancelButton);
        _grid.SelectionChanged += (_, _) => okButton.Enabled = _grid.CurrentRow is not null;
        _grid.CellDoubleClick += (_, _) => { if (_grid.CurrentRow is not null) Confirm(okButton); };
        okButton.Click += (_, _) => Confirm(okButton);
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = cancelButton;
    }

    private void Confirm(Button sender)
    {
        if (_grid.CurrentRow is null)
            return;

        // Physical path and app pool are read from the live IIS row the
        // resolver enumerated — never from stale stored state.
        _chosen = new IisResolvedTarget(
            SiteName: _grid.CurrentRow.Cells["site"].Value?.ToString() ?? string.Empty,
            AppPath: _grid.CurrentRow.Cells["path"].Value?.ToString() ?? "/",
            PhysicalPath: _grid.CurrentRow.Cells["physical"].Value?.ToString() ?? string.Empty,
            AppPoolName: NullIfEmpty(_grid.CurrentRow.Cells["pool"].Value?.ToString()));

        DialogResult = DialogResult.OK;
    }

    private static string? NullIfEmpty(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
