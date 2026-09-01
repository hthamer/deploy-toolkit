using DeployToolkit.AppKit;
using DeployToolkit.Core.Manifest;
using DeployToolkit.Core.Secrets;
using DeployToolkit.Core.Windows;

namespace DeployToolkit.Deployer;

/// <summary>
/// Modal prompt for the target database connection string needed to run a
/// package's DB scripts (plan §11 step 8 "connection entry, preview,
/// explicit confirm"). Two sources:
///  1. the local <see cref="SecretVault"/> when the component's
///     <c>DbConnectionRef</c> is a vault://name reference — unlocked with
///     DPAPI on Windows, or an <see cref="AesGcmSecretProtector"/> passphrase
///     elsewhere;
///  2. a pasted connection string (password-character box; a "show
///     characters" toggle helps spot typos).
///
/// The resolved string lives in <see cref="ConnectionString"/> for the
/// duration of the run only — the Deployer never persists it anywhere
/// (plan §12: secrets are never stored in plain text).
/// </summary>
public sealed class DbScriptsConnectionPrompt : Form
{
    private readonly RadioButton _vaultRadio;
    private readonly RadioButton _manualRadio;
    private readonly TextBox _passphraseBox;
    private readonly Label _passphraseLabel;
    private readonly TextBox _connectionStringBox;
    private readonly CheckBox _showCheckBox;

    private readonly string? _vaultName;
    private readonly string _vaultPath;

    /// <summary>The resolved connection string; null when the dialog was
    /// cancelled or the vault had no usable secret (the caller then offers
    /// to skip the DB scripts step).</summary>
    public string? ConnectionString { get; private set; }

    public DbScriptsConnectionPrompt(
        string componentName,
        IReadOnlyList<DbScriptRef> scripts,
        string? dbConnectionRef,
        string vaultPath)
    {
        _vaultPath = vaultPath;
        _vaultName = SecretVault.TryParseRef(dbConnectionRef, out var name) ? name : null;

        Text = "Database connection for DB scripts";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(620, 460);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(12, 12, 12, 4),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = $"The package for '{componentName}' includes {scripts.Count} DB script(s). " +
                   "They run straight from the package zip against the database below.",
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            Margin = new Padding(2, 2, 2, 8),
        });

        var scriptList = new ListBox
        {
            Height = Math.Min(24 + scripts.Count * 16, 110),
            BorderStyle = BorderStyle.FixedSingle,
        };
        foreach (var script in scripts)
            scriptList.Items.Add($"{script.File}  ({script.Kind})");
        layout.Controls.Add(AppTheme.MakeSectionLabel("Scripts in this package"));
        layout.Controls.Add(scriptList);

        // --- source radios ---
        _vaultRadio = new RadioButton
        {
            Text = _vaultName is null
                ? "Use the local secret vault"
                : $"Use the local secret vault — entry '{_vaultName}' (component's DbConnectionRef)",
            AutoSize = true,
            Enabled = _vaultName is not null,
        };
        _manualRadio = new RadioButton
        {
            Text = "Enter a connection string now (never persisted)",
            AutoSize = true,
        };
        layout.Controls.Add(AppTheme.MakeSectionLabel("Connection source"));
        layout.Controls.Add(_vaultRadio);

        // --- vault passphrase row (AES mode only; DPAPI needs nothing) ---
        _passphraseLabel = new Label
        {
            Text = "Vault passphrase (AES mode — DPAPI is unavailable on this machine):",
            AutoSize = true,
            Margin = new Padding(20, 2, 2, 2),
        };
        _passphraseBox = new TextBox { UseSystemPasswordChar = true, Width = 360, Dock = DockStyle.Fill };
        var passphraseRow = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(20, 0, 0, 0) };
        passphraseRow.Controls.Add(_passphraseLabel);
        passphraseRow.Controls.Add(_passphraseBox);
        layout.Controls.Add(passphraseRow);

        // --- manual entry ---
        layout.Controls.Add(_manualRadio);
        _connectionStringBox = new TextBox
        {
            UseSystemPasswordChar = true,
            Font = new Font("Consolas", 9f),
            Dock = DockStyle.Fill,
        };
        _showCheckBox = new CheckBox
        {
            Text = "Show characters while typing",
            AutoSize = true,
            Margin = new Padding(20, 2, 2, 2),
        };
        _showCheckBox.CheckedChanged += (_, _) =>
            _connectionStringBox.UseSystemPasswordChar = !_showCheckBox.Checked;
        var manualRow = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(20, 0, 0, 0) };
        manualRow.Controls.Add(_connectionStringBox);
        manualRow.Controls.Add(new Label
        {
            Text = "The string is kept in memory for this run only — it is never written to disk or the registry.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 2, 2, 6),
        });
        layout.Controls.Add(manualRow);

        _vaultRadio.CheckedChanged += (_, _) => RefreshMode();
        _manualRadio.CheckedChanged += (_, _) => RefreshMode();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 48,
        };
        var okButton = new Button { Text = "Use this connection" };
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

        // Default: vault when available, manual entry otherwise.
        if (_vaultName is not null)
            _vaultRadio.Checked = true;
        else
            _manualRadio.Checked = true;
        RefreshMode();
    }

    private void RefreshMode()
    {
        _passphraseBox.Enabled = _vaultRadio.Checked && !DpapiSecretProtector.IsSupported;
        _passphraseLabel.Enabled = _passphraseBox.Enabled;
        _connectionStringBox.Enabled = _manualRadio.Checked;
        _showCheckBox.Enabled = _manualRadio.Checked;
    }

    private void OnOk()
    {
        if (_vaultRadio.Checked && _vaultName is not null)
        {
            try
            {
                var connectionString = UnlockVault();
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    AppTheme.Error(this,
                        $"The vault has no secret named '{_vaultName}'. Enter the connection string manually " +
                        "or store the secret in the vault first.");
                    return; // keep the dialog open
                }

                ConnectionString = connectionString;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
            {
                AppTheme.Error(this,
                    $"Could not unlock the vault secret '{_vaultName}': {ex.Message}");
                return; // keep the dialog open
            }
        }
        else
        {
            var text = _connectionStringBox.Text.Trim();
            if (text.Length == 0)
            {
                AppTheme.Error(this, "Enter a connection string or cancel to skip the DB scripts step.");
                return; // keep the dialog open
            }

            ConnectionString = text;
            DialogResult = DialogResult.OK;
        }
    }

    /// <summary>Builds the protector for the vault file: DPAPI when running
    /// as a Windows user (ciphertext bound to this user on this machine),
    /// otherwise the self-describing AES-GCM passphrase mode.</summary>
    private ISecretProtector CreateProtector()
    {
        if (DpapiSecretProtector.IsSupported)
            return new DpapiSecretProtector();

        var passphrase = _passphraseBox.Text;
        if (string.IsNullOrEmpty(passphrase))
            throw new InvalidOperationException("Enter the vault passphrase to unlock the secret.");
        return AesGcmSecretProtector.CreateWithPassphrase(passphrase);
    }

    private string? UnlockVault()
    {
        var vault = new SecretVault(_vaultPath, CreateProtector());
        return vault.GetSecret(_vaultName!);
    }
}
