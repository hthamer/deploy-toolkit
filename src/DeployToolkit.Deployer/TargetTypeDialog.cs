using DeployToolkit.AppKit;
using DeployToolkit.Core.Targets;

namespace DeployToolkit.Deployer;

/// <summary>
/// Asks the user which target type this package deploys to. Only needed
/// when the registry component record is unavailable (offline mode): the
/// manifest (plan §3) carries the component id, version and files but NO
/// target-type field, so <see cref="TargetType"/> has to come from the
/// operator for Azure/Plesk/IIS dispatch.
/// </summary>
public sealed class TargetTypeDialog : Form
{
    private readonly RadioButton _iisRadio;
    private readonly RadioButton _azureRadio;
    private readonly RadioButton _pleskRadio;

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
        Size = new Size(480, 240);

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

        _iisRadio = new RadioButton { Text = "IIS (local server — this machine, over RDP)", AutoSize = true };
        _azureRadio = new RadioButton { Text = "Azure App Service (deployed directly over HTTPS)", AutoSize = true };
        _pleskRadio = new RadioButton { Text = "Plesk shared hosting (SFTP upload)", AutoSize = true };
        layout.Controls.Add(_iisRadio);
        layout.Controls.Add(_azureRadio);
        layout.Controls.Add(_pleskRadio);
        _iisRadio.Checked = true;

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
        ResultType = _azureRadio.Checked ? TargetType.AzureAppService
            : _pleskRadio.Checked ? TargetType.Plesk
            : TargetType.IisLocal;
        DialogResult = DialogResult.OK;
    }
}
