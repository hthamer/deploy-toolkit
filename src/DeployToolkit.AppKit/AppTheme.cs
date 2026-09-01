using DeployToolkit.Core.Git;

namespace DeployToolkit.AppKit;

/// <summary>
/// Shared look-and-feel for the DeployToolkit WinForms shells: one font
/// (Segoe UI 9pt), one neutral light background, one grid style, one set of
/// MessageBox captions. All UI is written in plain C# (no resx/designer) and
/// laid out with TableLayoutPanel/FlowLayoutPanel/Dock.
/// </summary>
public static class AppTheme
{
    /// <summary>Consistent caption for every themed MessageBox.</summary>
    public const string Caption = "DeployToolkit";

    /// <summary>The shared UI font family.</summary>
    public const string FontFamily = "Segoe UI";

    /// <summary>
    /// Applies the app look to <paramref name="form"/>: Segoe UI 9pt, a light
    /// neutral background, and a sensible minimum size so layouts can't be
    /// crushed to unusability. Secondary windows (every dialog, wizard,
    /// viewer and screen) stay OFF the Windows taskbar by default — only
    /// the shell's primary windows pass <c>primaryWindow: true</c>.
    /// </summary>
    public static void Apply(Form form, bool primaryWindow = false)
    {
        form.Font = new Font(FontFamily, 9f);
        form.BackColor = Color.WhiteSmoke;
        form.MinimumSize = new Size(440, 320);
        form.ShowInTaskbar = primaryWindow;
    }

    /// <summary>
    /// Applies the standard read-only data-grid style: no auto-generated
    /// columns (columns are declared explicitly by the caller), full-row
    /// selection, no row-header column, alternating row colors, bold headers,
    /// fill-weight based column sizing, read-only unless the caller says
    /// otherwise. Call this BEFORE assigning DataSource.
    /// </summary>
    public static void StyleGrid(DataGridView grid, bool readOnly = true)
    {
        grid.AutoGenerateColumns = false;
        grid.ReadOnly = readOnly;
        grid.AllowUserToAddRows = !readOnly;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.GridColor = Color.Gainsboro;
        grid.EnableHeadersVisualStyles = false;

        grid.ColumnHeadersDefaultCellStyle.Font = new Font(FontFamily, 9f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.Gainsboro;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.Gainsboro;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;

        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 228, 247);
        grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(243, 246, 249);
    }

    /// <summary>Standard button styling (currently a no-op hook so every
    /// button in the app goes through one place — visual tweaks land here).</summary>
    public static void StyleButton(Button button)
    {
        button.UseVisualStyleBackColor = true;
        button.AutoSize = true;
        button.MinimumSize = new Size(88, 28);
    }

    /// <summary>A bold section label (form-group headers such as "Deployment
    /// configuration").</summary>
    public static Label MakeSectionLabel(string text)
        => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font(FontFamily, 9f, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 4),
        };

    /// <summary>Consistent yes/no confirmation dialog (caption "DeployToolkit").
    /// Returns <see cref="DialogResult.Yes"/> when the user confirmed.</summary>
    public static DialogResult Confirm(IWin32Window? owner, string text, string? caption = null)
        => MessageBox.Show(owner, text, caption ?? Caption, MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

    /// <summary>Consistent error dialog (caption "DeployToolkit").</summary>
    public static void Error(IWin32Window? owner, string message, string? caption = null)
        => MessageBox.Show(owner, message, caption ?? Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>Consistent error dialog for an exception: shows the exception
    /// message plus (for clarity) the exception type when it is not one of the
    /// expected user-facing types.</summary>
    public static void Error(IWin32Window? owner, Exception exception, string? caption = null)
    {
        var message = exception switch
        {
            ArgumentException or InvalidOperationException or DivergedBranchException
                => exception.Message,
            _ => $"{exception.GetType().Name}: {exception.Message}",
        };
        Error(owner, message, caption);
    }
}
