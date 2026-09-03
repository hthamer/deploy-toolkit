using DeployToolkit.AppKit;

namespace DeployToolkit.Deployer;

/// <summary>
/// The Deployer's registry-connection dialog — deliberately separate from the
/// Packager's shared <see cref="ConnectionDialog"/>: the Deployer only edits
/// the central API details and the git tag template here. The registry mode /
/// connection string / local root / package store fields are NOT shown; their
/// persisted values are carried through untouched on OK (the Deployer keeps
/// whatever registry configuration is already on disk).
///
/// API credentials policy (user requirement): ONLY <c>ApiBaseUrl</c> is
/// persisted — <c>ApiUsername</c>/<c>ApiPassword</c> are [JsonIgnore] on
/// <see cref="RegistryConnectionSettings"/> and exist for this dialog
/// session only. The Login button calls the API's authenticate endpoint;
/// HTTP 200 shows a green OK, anything else shows the status code plus the
/// API's own response body.
/// </summary>
internal sealed class RegistryApiConnectionDialog : Form
{
    private readonly RegistryConnectionSettings _current;
    private readonly TextBox _apiUrlBox;
    private readonly TextBox _apiUserBox;
    private readonly TextBox _apiPasswordBox;
    private readonly TextBox _gitTagTemplateBox;
    private readonly ProgressBar _loginProgress;
    private readonly Button _loginButton;
    private readonly Label _statusLabel;

    /// <summary>The settings built from the dialog state when closed with OK;
    /// null when cancelled.</summary>
    public RegistryConnectionSettings? ResultSettings { get; private set; }

    public RegistryApiConnectionDialog(RegistryConnectionSettings? current = null)
    {
        _current = current ?? new RegistryConnectionSettings();

        Text = "Registry connection";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(680, 470);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(16, 12, 16, 8), // left/right breathing room inside the dialog
        };
        // Single percent column: children are constrained to the dialog width,
        // so the long AutoSize hint labels WRAP to multiple lines instead of
        // stretching the column past the form edge (which caused the overflow
        // and the scrollbar).
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // API header
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // API url
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // API hint
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // API creds
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // login progress
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Login button
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Git header
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Git template
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Git hint
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // status

        // --- Central API ---
        // Width is left to the docked layout (Dock=Fill inside the percent
        // column) — hardcoding Width alongside Dock only causes overflow.
        _apiUrlBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "https://registry.example.com",
            Margin = new Padding(4, 4, 4, 4),
        };
        var apiHelper = new Label
        {
            Text = $"Login calls POST {{url}}/{RegistryApiClient.AuthenticatePath}. " +
                   "Only the URL is saved — the username and password are never stored.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(4, 2, 4, 6),
        };
        _apiUserBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "username", Margin = new Padding(4, 2, 4, 2) };
        _apiPasswordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Margin = new Padding(4, 2, 4, 2) };
        var apiCredRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        apiCredRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        apiCredRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        apiCredRow.Controls.Add(_apiUserBox, 0, 0);
        apiCredRow.Controls.Add(_apiPasswordBox, 1, 0);

        // Inline progress, shown only while a login request is in flight (no
        // Guard busy overlay — its modal layer would cover the status/error
        // text below and any message dialog spawned from here).
        _loginProgress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            Height = 18,
            Dock = DockStyle.Fill,
            Visible = false,
            Margin = new Padding(4, 8, 4, 2),
        };

        // Direct async handler (no Guard): validation problems and API errors
        // surface in _statusLabel inside the form, never in extra dialogs.
        _loginButton = new Button { Text = "Login", AutoSize = true, Margin = new Padding(4, 2, 4, 4) };
        AppTheme.StyleButton(_loginButton);
        _loginButton.Click += async (_, _) => await LoginAsync();

        layout.Controls.Add(AppTheme.MakeSectionLabel("Central API"));
        layout.Controls.Add(_apiUrlBox);
        layout.Controls.Add(apiHelper);
        layout.Controls.Add(apiCredRow);
        layout.Controls.Add(_loginProgress);
        layout.Controls.Add(_loginButton);

        // --- Git tag template ---
        _gitTagTemplateBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "deploy-{version}-{date}",
            Margin = new Padding(4, 4, 4, 4),
        };
        var tagHint = new Label
        {
            Text = "Placeholders: {version} {date} (yyyyMMdd) {datetime} (yyyyMMdd-HHmmss) {component}. Leave empty to disable auto-tagging.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(4, 2, 4, 6),
        };
        layout.Controls.Add(AppTheme.MakeSectionLabel("Git tag template (auto-tag on deploy)"));
        layout.Controls.Add(_gitTagTemplateBox);
        layout.Controls.Add(tagHint);

        // --- status line (login result) ---
        _statusLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 44,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Margin = new Padding(4, 8, 4, 4),
        };
        layout.Controls.Add(_statusLabel);

        // --- buttons ---
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
        cancelButton.Click += (_, _) => Close();
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = cancelButton;
        AcceptButton = okButton;

        // Load current values. Credentials are never persisted — start empty
        // every time (ApiUsername/ApiPassword are [JsonIgnore]).
        _apiUrlBox.Text = _current.ApiBaseUrl ?? string.Empty;
        _gitTagTemplateBox.Text = _current.GitTagTemplate ?? string.Empty;
    }

    /// <summary>Login button: authenticates against the central API
    /// (POST {url}/api/auth/authenticate). All feedback stays INSIDE the
    /// form — the inline progress bar runs while the request is in flight,
    /// and the status line shows the result: green "Login OK" on HTTP 200,
    /// red with the status code plus the API's response body otherwise.
    /// No busy overlay, no message dialogs.</summary>
    private async Task LoginAsync()
    {
        var url = NullIfEmpty(_apiUrlBox.Text);
        var username = NullIfEmpty(_apiUserBox.Text);
        var password = NullIfEmpty(_apiPasswordBox.Text);

        if (url is null || username is null || password is null)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "API URL, username and password are all required to log in.";
            return;
        }

        _loginButton.Enabled = false;
        _loginProgress.Visible = true;
        _statusLabel.ForeColor = Color.DimGray;
        _statusLabel.Text = "Logging in…";

        try
        {
            var (success, detail) = await RegistryApiClient.AuthenticateAsync(url, username, password)
                .ConfigureAwait(true);

            _statusLabel.ForeColor = success ? Color.ForestGreen : Color.Firebrick;
            _statusLabel.Text = success
                ? $"Login OK — API authenticated.{(detail.Length > 0 ? $" ({detail})" : string.Empty)}"
                : $"Login failed: {detail}";
        }
        finally
        {
            _loginProgress.Visible = false;
            _loginButton.Enabled = true;
        }
    }

    private void OnOk()
    {
        // Carry the registry-mode fields (Mode, ConnectionString, LocalRoot,
        // PackageStoreRootPath) through untouched — this dialog doesn't edit
        // them, and the Deployer keeps connecting with whatever is persisted.
        ResultSettings = new RegistryConnectionSettings
        {
            Mode = _current.Mode,
            ConnectionString = _current.ConnectionString,
            LocalRoot = _current.LocalRoot,
            PackageStoreRootPath = _current.PackageStoreRootPath,
            GitTagTemplate = NullIfEmpty(_gitTagTemplateBox.Text),
            ApiBaseUrl = NullIfEmpty(_apiUrlBox.Text),
            ApiUsername = NullIfEmpty(_apiUserBox.Text),
            ApiPassword = _apiPasswordBox.Text is { Length: > 0 } pwd ? pwd : null,
        };
        DialogResult = DialogResult.OK;
    }

    private static string? NullIfEmpty(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
