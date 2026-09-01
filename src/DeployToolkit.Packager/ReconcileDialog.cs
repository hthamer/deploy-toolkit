using DeployToolkit.AppKit;
using DeployToolkit.Core.Deployment;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Packager;

/// <summary>
/// "Reconcile Offline Results…" (plan §9/§10): picks a folder of
/// <c>*.offline-result.json</c> files written by the Deployer in offline
/// mode and replays them into the central registry via
/// <see cref="OfflineReconciler"/> — Success runs flip their package to
/// Deployed, Failed/RolledBack runs are recorded as history, and anything
/// already reconciled is skipped (idempotent). The report (files found,
/// reconciled, skipped, failed + reasons) is shown after the run.
/// </summary>
public sealed class ReconcileDialog : Form
{
    private readonly IRegistryStore _store;

    private readonly TextBox _folderBox;
    private readonly Button _reconcileButton;
    private readonly Label _summaryLabel;
    private readonly ListBox _detailsList;
    private readonly LogPane _log;

    private ReconcileDialog(IRegistryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        Text = "Reconcile offline deployment results";
        AppTheme.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 560);
        MinimizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));

        layout.Controls.Add(AppTheme.MakeSectionLabel("Offline results folder"));

        var folderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _folderBox = new TextBox { Dock = DockStyle.Fill };
        var browseButton = new Button { Text = "Browse…" };
        AppTheme.StyleButton(browseButton);
        browseButton.Click += (_, _) => PickFolder();
        folderRow.Controls.Add(_folderBox, 0, 0);
        folderRow.Controls.Add(browseButton, 1, 0);
        layout.Controls.Add(folderRow);

        var hint = new Label
        {
            Text = "The Deployer writes one *.offline-result.json per package when the registry is unreachable. " +
                   "Reconciling replays those results into the registry (already-reconciled files are skipped).",
            AutoSize = false,
            Height = 40,
            ForeColor = Color.DimGray,
            Dock = DockStyle.Fill,
        };
        layout.Controls.Add(hint);

        _reconcileButton = new Button { Text = "Reconcile" };
        AppTheme.StyleButton(_reconcileButton);
        _reconcileButton.Click += (_, _) => Guard.FireAndForget(this, "Reconciling offline results…", ReconcileAsync);
        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 4, 0, 4),
        };
        buttonRow.Controls.Add(_reconcileButton);
        layout.Controls.Add(buttonRow);

        _summaryLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 46,
            Dock = DockStyle.Fill,
            Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold),
        };
        layout.Controls.Add(_summaryLabel);

        _detailsList = new ListBox
        {
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
            Font = new Font("Consolas", 9f),
        };
        var detailsGroup = new GroupBox
        {
            Text = "Failed / skipped details",
            Dock = DockStyle.Fill,
            Controls = { _detailsList },
        };
        layout.Controls.Add(detailsGroup);

        _log = new LogPane { Dock = DockStyle.Fill };
        var logGroup = new GroupBox
        {
            Text = "Progress",
            Dock = DockStyle.Fill,
            Controls = { _log },
        };
        layout.Controls.Add(logGroup);

        var closeButton = new Button { Text = "Close", DialogResult = DialogResult.Cancel };
        AppTheme.StyleButton(closeButton);
        // DialogResult auto-closes only modal forms — close explicitly; this
        // screen is now hosted as a modeless in-app (MDI) child.
        closeButton.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 48,
        };
        buttons.Controls.Add(closeButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        CancelButton = closeButton;

        _folderBox.Text = DefaultResultsFolder();
    }

    /// <summary>Creates the reconcile screen for embedding — the Packager
    /// shell hosts it as an MDI child (sets <c>MdiParent</c>, calls
    /// <c>Show()</c>).</summary>
    public static ReconcileDialog Create(IRegistryStore store) => new(store);

    private void PickFolder()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Folder holding the Deployer's *.offline-result.json files",
            ShowNewFolderButton = false,
        };
        if (Directory.Exists(_folderBox.Text))
            picker.SelectedPath = _folderBox.Text;
        if (picker.ShowDialog(this) == DialogResult.OK)
            _folderBox.Text = picker.SelectedPath;
    }

    private async Task ReconcileAsync()
    {
        var folder = _folderBox.Text.Trim();
        if (folder.Length == 0)
        {
            AppTheme.Error(this, "Pick the folder that holds the offline result files first.");
            return;
        }

        _summaryLabel.Text = string.Empty;
        _summaryLabel.ForeColor = Color.Black;
        _detailsList.Items.Clear();
        _log.ClearAll();

        // Count the candidate files up front so "no result files here" gets a
        // friendly message instead of an empty report.
        var resultFiles = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*" + OfflineResultWriter.FileSuffix)
            : Array.Empty<string>();

        if (resultFiles.Length == 0)
        {
            _summaryLabel.ForeColor = Color.DimGray;
            _summaryLabel.Text = "No offline result files found — nothing to reconcile.";
            _log.AppendLine($"No *{OfflineResultWriter.FileSuffix} files in: {folder}");
            return;
        }

        _log.AppendLine($"Found {resultFiles.Length} offline result file(s) in {folder}.");

        var progress = new Progress<string>(line => _log.AppendLine(line));
        var report = await new OfflineReconciler(_store).ReconcileAsync(folder, progress);

        _detailsList.Items.AddRange(report.Errors.ToArray());

        _summaryLabel.ForeColor = report.Errors.Count > 0 ? Color.Firebrick : Color.ForestGreen;
        _summaryLabel.Text =
            $"Files found: {resultFiles.Length}   Reconciled: {report.Reconciled}   " +
            $"Skipped (already reconciled): {report.Skipped}   Failed: {report.Errors.Count}";
    }

    /// <summary>Documents\DeployToolkit\OfflineResults — the folder the
    /// Deployer's offline results are expected to land in.</summary>
    internal static string DefaultResultsFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.CurrentDirectory;
        return Path.Combine(documents, "DeployToolkit", "OfflineResults");
    }
}
