using DeployToolkit.Core.Git;

namespace DeployToolkit.AppKit;

/// <summary>
/// Modal username / password-or-PAT prompt used as the LAST link of the git
/// credential chain: shown only when the remote URL, the options, and the
/// Windows Credential Manager (Git Credential Manager / Visual Studio
/// entries) all failed to authenticate the fetch. Optionally persists the
/// entered credential via <see cref="WindowsCredentialManagerSource.TryRemember"/>
/// so the next sync succeeds without prompting.
/// </summary>
public sealed class GitCredentialsDialog : Form
{
    /// <summary>The entered credential (null unless OK was pressed).</summary>
    public GitCredential? ResultCredential { get; private set; }

    /// <summary>True when the user asked to remember the credential.</summary>
    public bool RememberCredential { get; private set; }

    private readonly TextBox _usernameBox;
    private readonly TextBox _passwordBox;
    private readonly CheckBox _rememberBox;

    public GitCredentialsDialog(GitCredentialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Text = "Git credentials";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AppTheme.Apply(this);
        Size = new Size(480, 260);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
        };

        var host = request.Host.Length > 0 ? request.Host : request.Url;
        layout.Controls.Add(new Label
        {
            Text = $"'{host}' rejected the fetch (401) and no stored credential worked.\n" +
                   "Enter a username and password / personal access token:",
            AutoSize = true,
            Margin = new Padding(2, 2, 2, 8),
        });

        _usernameBox = new TextBox
        {
            Text = request.UsernameFromUrl ?? Environment.UserName,
            PlaceholderText = "Username (any value for a PAT)",
            Dock = DockStyle.Top,
        };
        layout.Controls.Add(MakeLabeled("Username:", _usernameBox));

        _passwordBox = new TextBox
        {
            UseSystemPasswordChar = true,
            PlaceholderText = "Password or personal access token",
            Dock = DockStyle.Top,
        };
        layout.Controls.Add(MakeLabeled("Password / token:", _passwordBox));

        _rememberBox = new CheckBox
        {
            Text = $"Remember (store in Windows Credential Manager as git:{request.Scheme}://{request.Host})",
            AutoSize = true,
            Margin = new Padding(2, 8, 2, 2),
        };
        layout.Controls.Add(_rememberBox);

        var ok = new Button { Text = "OK" };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        AppTheme.StyleButton(ok);
        AppTheme.StyleButton(cancel);
        ok.Click += OnOkClick;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 48,
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = cancel;
        AcceptButton = ok;
    }

    private static GroupBox MakeLabeled(string label, Control control)
    {
        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            Margin = new Padding(0),
        };
        inner.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(2, 2, 2, 2) });
        inner.Controls.Add(control);
        return new GroupBox
        {
            Text = string.Empty,
            Dock = DockStyle.Top,
            AutoSize = true,
            Controls = { inner },
        };
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        if (_passwordBox.Text.Trim().Length == 0)
        {
            AppTheme.Error(this, "A password or personal access token is required.", "Git credentials");
            _passwordBox.Focus();
            return;
        }

        ResultCredential = new GitCredential(_usernameBox.Text.Trim(), _passwordBox.Text.Trim());
        RememberCredential = _rememberBox.Checked;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>
/// Builds the <see cref="GitSyncOptions.CredentialPrompt"/> delegate for a
/// WinForms owner: marshals the dialog onto the UI thread (the prompt fires
/// on the synchronize background thread), and honors the user's "remember"
/// choice by writing the Windows Credential Manager entry.
/// </summary>
public static class GitCredentialUi
{
    public static Func<GitCredentialRequest, GitCredential?> CreatePrompt(IWin32Window owner)
    {
        return request =>
        {
            GitCredential? result = null;
            var remember = false;

            void Prompt()
            {
                using var dialog = new GitCredentialsDialog(request);
                if (dialog.ShowDialog(owner) == DialogResult.OK)
                {
                    result = dialog.ResultCredential;
                    remember = dialog.RememberCredential;
                }
            }

            if (owner is Control control && control.InvokeRequired)
                control.Invoke((MethodInvoker)Prompt);
            else
                Prompt();

            if (result is not null && remember)
                WindowsCredentialManagerSource.TryRemember(request, result);

            return result;
        };
    }
}
