using DeployToolkit.AppKit;
using DeployToolkit.Core.Targets;

namespace DeployToolkit.Deployer;

/// <summary>
/// Asks the user which target type this package deploys to. Only needed
/// when the registry component record is unavailable (offline mode): the
/// manifest (plan §3) carries the component id, version and files but NO
/// target-type field, so <see cref="TargetType"/> has to come from the
/// operator. Currently only IIS is offered (Azure/Plesk are hidden — the
/// user said "for now make it only IIS"; they'll be re-enabled later).
/// </summary>
public sealed class TargetTypeDialog : Form
{
    private readonly RadioButton _iisRadio;

    /// <summary>The chosen target type; null when the dialog was cancelled.</summary>
    public TargetType? ResultType { get; private set; }

    public TargetTypeDialog(string componentName)
    {
        Text = "Deployment target type";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(480, 200);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = $"The registry has no record for component '{componentName}' on this machine, " +
                   "and packages do not carry a target-type field. Where is this package deployed?",
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(2, 2, 2, 10),
        });

        // Only IIS is offered for now (user: "for now make it only IIS").
        // Azure/Plesk are hidden — they'll be re-enabled later.
        _iisRadio = new RadioButton { Text = "IIS (local server — this machine, over RDP)", AutoSize = true, Checked = true };
        layout.Controls.Add(_iisRadio);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 48,
        };
        var okButton = new Button { Text = "OK" };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        AppTheme.StyleButton(okButton);
        AppTheme.StyleButton(cancelButton);
        okButton.Click += (_, _) => OnOk();
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = cancelButton;
        AcceptButton = okButton;
    }

    private void OnOk()
    {
        ResultType = TargetType.IisLocal;
        DialogResult = DialogResult.OK;
    }
}
