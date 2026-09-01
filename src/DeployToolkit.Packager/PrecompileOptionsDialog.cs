using DeployToolkit.AppKit;
using DeployToolkit.Core.Publishing;

namespace DeployToolkit.Packager;

/// <summary>
/// Modal dialog mirroring Visual Studio's <b>Precompile Options</b> dialog —
/// the <c>[Configure…]</c> button next to <c>Precompile during publishing</c>
/// in the publish wizard for .NET Framework Web Application projects.
///
/// Exposes the three ASP.NET precompilation switches the Web Publishing
/// Pipeline maps onto <c>aspnet_compiler</c> flags (see
/// <see cref="WebPrecompileOptions"/>). Returns the chosen options on OK,
/// <c>null</c> on Cancel. Built in plain C# (no resx/designer), like every
/// other dialog in the app (see <c>ComponentEditorDialog</c>).
/// </summary>
internal sealed class PrecompileOptionsDialog : Form
{
    private readonly CheckBox _updatableBox;
    private readonly CheckBox _fixedNamesBox;
    private readonly CheckBox _debugInfoBox;

    /// <summary>
    /// The chosen precompile options when the dialog is closed with OK;
    /// otherwise <c>null</c>.
    /// </summary>
    public WebPrecompileOptions? Result { get; private set; }

    public PrecompileOptionsDialog(WebPrecompileOptions current)
    {
        Text = "Precompile Options";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(460, 280);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _updatableBox = new CheckBox
        {
            Text = "Allow precompiled site to be updatable",
            Checked = current.Updatable,
            AutoSize = true,
        };
        _fixedNamesBox = new CheckBox
        {
            Text = "Use fixed naming and single page assemblies",
            Checked = current.UseFixedNames,
            AutoSize = true,
        };
        _debugInfoBox = new CheckBox
        {
            Text = "Emit Debug information",
            Checked = current.EmitDebugInfo,
            AutoSize = true,
        };

        layout.Controls.Add(AppTheme.MakeSectionLabel("Precompilation options"));
        layout.Controls.Add(_updatableBox);
        layout.Controls.Add(_fixedNamesBox);
        layout.Controls.Add(_debugInfoBox);

        var hint = new Label
        {
            Text = "These map to the aspnet_compiler flags consumed by the Web Publishing Pipeline.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Dock = DockStyle.Fill,
        };
        layout.Controls.Add(hint);

        // Right-aligned OK/Cancel button row (RTL flow adds the first
        // control at the right edge, so Cancel goes first → sits rightmost).
        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
        };
        var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        AppTheme.StyleButton(cancelBtn);
        var okBtn = new Button { Text = "OK", DialogResult = DialogResult.OK };
        AppTheme.StyleButton(okBtn);
        buttonRow.Controls.Add(cancelBtn);
        buttonRow.Controls.Add(okBtn);
        layout.Controls.Add(buttonRow);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        okBtn.Click += (_, _) =>
        {
            Result = new WebPrecompileOptions(
                Updatable: _updatableBox.Checked,
                UseFixedNames: _fixedNamesBox.Checked,
                EmitDebugInfo: _debugInfoBox.Checked);
        };

        Controls.Add(layout);
    }
}
