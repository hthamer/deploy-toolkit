using DeployToolkit.AppKit;
using DeployToolkit.Core.IisControl;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;
using DeployToolkit.Core.Windows;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Plan §11 step 2: settle which concrete target this package deploys to.
/// NO POPUPS — the target type and IIS application are selected via inline
/// dropdowns on the form itself (user request: "place the target as drop
/// down in the same Form, no popup").
///
/// Layout:
///  - "Target type:" dropdown (IIS only for now; Azure/Plesk hidden).
///  - "IIS application:" dropdown (lists all IIS sites+apps from the live
///    IIS on this machine). Auto-selects the saved mapping for this
///    component when found; the user can change it.
///  - A details box showing the resolved physical path + app pool.
///  - A message label for status/errors.
/// </summary>
internal sealed class StageResolveTarget : StagePanel
{
    private readonly ComboBox _targetTypeBox;
    private readonly ComboBox _iisAppBox;
    private readonly TextBox _detailsBox;
    private readonly Label _messageLabel;
    private IReadOnlyList<IisApplicationInfo> _iisApps = Array.Empty<IisApplicationInfo>();
    private IIisController? _controller;
    private bool _suppressAppBoxEvents;

    public StageResolveTarget(MainForm shell) : base(shell)
    {
        var layout = MakeVerticalLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 0: section label
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 1: target type row
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 2: IIS app row
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 3: details
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 4: message

        layout.Controls.Add(AppTheme.MakeSectionLabel("Deployment target"), 0, 0);

        // --- Target type dropdown (inline, no popup) ---
        var typeRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        typeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        typeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        typeRow.Controls.Add(new Label
        {
            Text = "Target type:",
            AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 8, 2),
        }, 0, 0);
        _targetTypeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _targetTypeBox.Items.Add("IIS (local server)");
        _targetTypeBox.SelectedIndex = 0; // IIS is the only option for now
        _targetTypeBox.SelectedIndexChanged += (_, _) => OnTargetTypeChanged();
        typeRow.Controls.Add(_targetTypeBox, 1, 0);
        layout.Controls.Add(typeRow, 0, 1);

        // --- IIS application dropdown (inline, no popup) ---
        var appRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        appRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        appRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        appRow.Controls.Add(new Label
        {
            Text = "IIS application:",
            AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 8, 2),
        }, 0, 0);
        _iisAppBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _iisAppBox.SelectedIndexChanged += (_, _) => OnIisAppChanged();
        appRow.Controls.Add(_iisAppBox, 1, 0);
        layout.Controls.Add(appRow, 0, 2);

        _detailsBox = MakeReadOnlySummaryBox(0);
        layout.Controls.Add(_detailsBox, 0, 3);

        _messageLabel = new Label
        {
            Text = string.Empty, AutoSize = false, Height = 28,
            Dock = DockStyle.Fill, ForeColor = Color.DimGray,
        };
        layout.Controls.Add(_messageLabel, 0, 4);

        Controls.Add(layout);
    }

    public override string Title => "2. Target";

    public override void OnEnter()
    {
        _messageLabel.ForeColor = Color.DimGray;

        if (Context is null)
        {
            _detailsBox.Text = string.Empty;
            _messageLabel.Text = "Load a package first (step 1).";
            return;
        }

        // If the target was already resolved (re-entering the step), show it.
        if (Context.TargetType is not null && Context.IisTarget is not null)
        {
            RenderDetails();
            _messageLabel.ForeColor = Color.ForestGreen;
            _messageLabel.Text = "IIS target resolved — click Next to continue, or change the dropdown above.";
            return;
        }

        // Populate the IIS apps dropdown + auto-select from the saved mapping.
        PopulateIisApps();
    }

    /// <summary>Populates the IIS apps dropdown by enumerating live IIS
    /// applications on this machine. Auto-selects the saved mapping for
    /// this component when found (so the user doesn't have to pick again).
    /// Runs under Guard (MWA is synchronous).</summary>
    private void PopulateIisApps()
    {
        if (Context is not { } context)
            return;

        Guard.RunAsync(Shell, "Enumerating IIS applications…", () =>
        {
            _suppressAppBoxEvents = true;
            try
            {
                // Build the IIS controller + enumerate apps.
                try
                {
                    _controller = new MicrosoftWebAdministrationController();
                    _iisApps = _controller.EnumerateApplications();
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    this.BeginInvoke(() =>
                    {
                        _messageLabel.ForeColor = Color.Firebrick;
                        _messageLabel.Text = "IIS control requires the Deployer to run on the target server with IIS 7+ " +
                                             $"installed and configuration access. ({ex.Message})";
                    });
                    return;
                }

                this.BeginInvoke(() =>
                {
                    _iisAppBox.Items.Clear();
                    _iisAppBox.Items.Add(string.Empty); // "(not selected)" as first entry
                    foreach (var app in _iisApps)
                        _iisAppBox.Items.Add($"{app.SiteName}{app.Path}");

                    // Auto-select from the saved mapping.
                    var mappingStore = new IisTargetMappingStore(DeployerPaths.IisTargetsPath);
                    if (mappingStore.TryGet(context.Manifest.ComponentId, out var mapping))
                    {
                        var displayKey = $"{mapping.SiteName}{mapping.AppPath}";
                        var idx = FindAppIndex(displayKey);
                        if (idx >= 0)
                        {
                            _iisAppBox.SelectedIndex = idx;
                            _messageLabel.ForeColor = Color.ForestGreen;
                            _messageLabel.Text = $"Auto-selected from saved preferences: {displayKey}";
                            ResolveFromDropdown();
                            return;
                        }
                    }

                    // No mapping — check the component's registry config.
                    if (context.Component is { } comp && !string.IsNullOrWhiteSpace(comp.IisSiteName))
                    {
                        var appPath = string.IsNullOrWhiteSpace(comp.IisAppPath) ? "/" : comp.IisAppPath;
                        var displayKey = $"{comp.IisSiteName}{appPath}";
                        var idx = FindAppIndex(displayKey);
                        if (idx >= 0)
                        {
                            _iisAppBox.SelectedIndex = idx;
                            _messageLabel.ForeColor = Color.ForestGreen;
                            _messageLabel.Text = $"Auto-selected from registry: {displayKey}";
                            ResolveFromDropdown();
                            return;
                        }
                    }

                    // No mapping, no config — user must pick.
                    _iisAppBox.SelectedIndex = 0;
                    _messageLabel.ForeColor = Color.DimGray;
                    _messageLabel.Text = _iisApps.Count == 0
                        ? "No IIS applications found — ensure IIS is running on this machine."
                        : "Select an IIS application from the dropdown above.";
                });
            }
            finally
            {
                this.BeginInvoke(() => _suppressAppBoxEvents = false);
            }

            return Task.CompletedTask;
        });
    }

    private int FindAppIndex(string displayKey)
    {
        for (var i = 0; i < _iisApps.Count; i++)
        {
            var app = _iisApps[i];
            if (string.Equals($"{app.SiteName}{app.Path}", displayKey, StringComparison.OrdinalIgnoreCase))
                return i + 1; // +1 because index 0 is the empty entry
        }
        return -1;
    }

    private void OnTargetTypeChanged()
    {
        // IIS is the only option for now — nothing to switch.
    }

    private void OnIisAppChanged()
    {
        if (_suppressAppBoxEvents)
            return;
        ResolveFromDropdown();
    }

    /// <summary>Resolves the IIS target from the dropdown selection and
    /// stores it on the context + saves the mapping.</summary>
    private void ResolveFromDropdown()
    {
        if (Context is not { } context || _controller is null)
            return;

        var idx = _iisAppBox.SelectedIndex;
        if (idx <= 0 || idx > _iisApps.Count)
        {
            context.TargetType = TargetType.IisLocal;
            context.IisController = _controller;
            context.IisTarget = null;
            _detailsBox.Text = string.Empty;
            _messageLabel.ForeColor = Color.DimGray;
            _messageLabel.Text = "Select an IIS application from the dropdown above.";
            return;
        }

        var app = _iisApps[idx - 1];
        var target = new IisResolvedTarget(app.SiteName, app.Path, app.PhysicalPath, app.AppPoolName);

        context.TargetType = TargetType.IisLocal;
        context.IisController = _controller;
        context.IisTarget = target;

        // Save the mapping for next time on this machine.
        try
        {
            var mappingStore = new IisTargetMappingStore(DeployerPaths.IisTargetsPath);
            mappingStore.Save(context.Manifest.ComponentId, new IisTargetMapping(target.SiteName, target.AppPath, target.AppPoolName));
        }
        catch { /* mapping save is best-effort */ }

        RenderDetails();
        _messageLabel.ForeColor = Color.ForestGreen;
        _messageLabel.Text = "IIS target resolved (mapping saved for next time).";
        Shell.AppendLog($"IIS target: site '{target.SiteName}', app '{target.AppPath}', " +
                        $"physical '{target.PhysicalPath}', pool '{target.AppPoolName ?? "(none)"}'.");
        Shell.OnTargetResolved();
    }

    private void RenderDetails()
    {
        if (Context is not { } context)
            return;

        if (context.IisTarget is { } target)
        {
            _detailsBox.Text =
                $"Site:          {target.SiteName}\n" +
                $"App path:      {target.AppPath}\n" +
                $"Physical path: {target.PhysicalPath}\n" +
                $"App pool:      {target.AppPoolName ?? "(none — app_offline.htm fallback will be used)"}";
        }
        else
        {
            _detailsBox.Text = string.Empty;
        }
    }
}
