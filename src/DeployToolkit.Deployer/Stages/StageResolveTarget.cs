using DeployToolkit.AppKit;
using DeployToolkit.Core.IisControl;
using DeployToolkit.Core.Registry;
using DeployToolkit.Core.Targets;
using DeployToolkit.Core.Windows;

namespace DeployToolkit.Deployer.Stages;

/// <summary>
/// Plan §11 step 2: settle which concrete target this package deploys to.
/// The target type comes from the registry component when known; otherwise
/// (offline mode — manifests carry no target-type field) the user picks it.
/// For IIS the <see cref="IisTargetResolver"/> resolves the site/application
/// from the machine-local mapping store or the component config, verifying
/// against live IIS data; when it cannot, the live application list becomes
/// the picker and the choice is saved as the machine-local mapping for next
/// time (plan §6). Azure/Plesk targets only need the stored resource/host
/// confirmed here — their connection details are entered in pre-flight.
/// </summary>
internal sealed class StageResolveTarget : StagePanel
{
    private readonly TextBox _detailsBox;
    private readonly Label _messageLabel;

    public StageResolveTarget(MainForm shell)
        : base(shell)
    {
        var layout = MakeVerticalLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(AppTheme.MakeSectionLabel("Deployment target"));

        // The wizard drives navigation — resolution is user-initiated from
        // here, not auto-fired on entering the step (which used to stack the
        // target-type and IIS picker dialogs on top of each other).
        var selectRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 4, 0, 4),
            WrapContents = false,
        };
        var selectButton = new Button { Text = "Select target…" };
        AppTheme.StyleButton(selectButton);
        selectButton.Click += (_, _) => StartResolve();
        selectRow.Controls.Add(selectButton);
        layout.Controls.Add(selectRow);

        _detailsBox = MakeReadOnlySummaryBox(200);

        _messageLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 32,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };

        layout.Controls.Add(_detailsBox);
        layout.Controls.Add(_messageLabel);

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

        if (Context.TargetType is { } known)
        {
            _messageLabel.Text = known == TargetType.IisLocal && Context.IisTarget is null
                ? "Click 'Select target…' to pick the IIS application."
                : "Target resolved — click Next to continue, or 'Select target…' to change it.";
            RenderDetails();
        }
        else
        {
            _detailsBox.Text = string.Empty;
            _messageLabel.Text = "Click 'Select target…' to choose where this package deploys.";
        }
    }

    /// <summary>Runs the resolution (guarded — invoked from the bottom-bar
    /// "Resolve Target" button and the auto-advance after load). The work is
    /// synchronous (MWA is a synchronous API; the picker is modal), so it is
    /// wrapped in a completed task for <see cref="Guard.RunAsync"/>.</summary>
    internal void StartResolve() =>
        Guard.RunAsync(Shell, "Resolving target…", () =>
        {
            ResolveNow();
            return Task.CompletedTask;
        });

    private void ResolveNow()
    {
        if (Context is not { } context)
        {
            AppTheme.Error(this, "Load a package first.");
            return;
        }

        // --- 1. settle the target type (component record, else ask).
        var targetType = context.TargetType;
        if (targetType is null)
        {
            using var typeDialog = new TargetTypeDialog(context.Manifest.Component);
            if (typeDialog.ShowDialog(Shell) != DialogResult.OK || typeDialog.ResultType is not { } chosen)
            {
                _messageLabel.ForeColor = Color.DimGray;
                _messageLabel.Text = "Target type not chosen — resolve the target before pre-flight.";
                return;
            }

            targetType = chosen;
        }

        context.TargetType = targetType;
        context.IisController = null;
        context.IisTarget = null;

        if (targetType != TargetType.IisLocal)
        {
            RenderDetails();
            _messageLabel.ForeColor = Color.ForestGreen;
            _messageLabel.Text = $"Target type {targetType} — enter its connection details in pre-flight.";
            Shell.AppendLog($"Target type: {targetType} (executor path — no IIS resolution needed).");
            Shell.OnTargetResolved();
            return;
        }

        // --- 2. IIS: build the real controller (Windows + IIS only), then
        // resolve the site/app from mapping → config → live picker.
        IIisController controller;
        try
        {
            controller = new MicrosoftWebAdministrationController();
            // Touch the config store once here so a missing-IIS machine
            // surfaces a friendly error now, not mid-deploy.
            _ = controller.EnumerateSites();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            context.TargetType = null; // unresolved — let the user pick another type or fix IIS
            _messageLabel.ForeColor = Color.Firebrick;
            _messageLabel.Text = "IIS control requires the Deployer to run on the target server with IIS 7+ " +
                                 $"installed, and the account needs IIS configuration access. ({Describe(ex)})";
            return;
        }

        var resolver = new IisTargetResolver(controller, new IisTargetMappingStore(DeployerPaths.IisTargetsPath));
        var resolution = resolver.Resolve(EffectiveComponent(context, targetType.Value));

        if (!resolution.Resolved)
        {
            using var picker = new IisTargetPickerDialog(resolution.Candidates, resolution.Message ?? "Pick the application to deploy into.");
            if (picker.ShowDialog(Shell) != DialogResult.OK || picker.ResultTarget is not { } picked)
            {
                _messageLabel.ForeColor = Color.DimGray;
                _messageLabel.Text = "No IIS application chosen — resolve the target before pre-flight.";
                return;
            }

            resolver.SaveMapping(context.Manifest.ComponentId, picked); // machine-local mapping for next time (plan §6)
            resolution = IisTargetResolution.Found(picked);
        }

        context.IisController = controller;
        context.IisTarget = resolution.Target;
        RenderDetails();

        _messageLabel.ForeColor = Color.ForestGreen;
        _messageLabel.Text = "IIS target resolved (mapping saved for next time on this machine).";
        Shell.AppendLog($"IIS target resolved: site '{resolution.Target!.SiteName}', app '{resolution.Target.AppPath}', " +
                        $"physical '{resolution.Target.PhysicalPath}', pool '{resolution.Target.AppPoolName ?? "(none)"}'.");
        Shell.OnTargetResolved();
    }

    /// <summary>The resolver needs a <see cref="DeploymentComponent"/>; in
    /// offline mode the registry record is absent, so a minimal stand-in is
    /// synthesized from the manifest (the resolver only reads ComponentId,
    /// IisSiteName and IisAppPath).</summary>
    private static DeploymentComponent EffectiveComponent(DeploymentContext context, TargetType targetType) =>
        context.Component ?? new DeploymentComponent
        {
            ComponentId = context.Manifest.ComponentId,
            ClientId = string.Empty,
            Name = context.Manifest.Component,
            TargetType = targetType,
            TargetFramework = context.Manifest.TargetFramework,
        };

    private void RenderDetails()
    {
        if (Context is not { } context)
            return;

        switch (context.TargetType)
        {
            case TargetType.IisLocal when context.IisTarget is { } target:
                _detailsBox.Text =
                    $"Site:          {target.SiteName}\n" +
                    $"App path:      {target.AppPath}\n" +
                    $"Physical path: {target.PhysicalPath}\n" +
                    $"App pool:      {target.AppPoolName ?? "(none — app_offline.htm fallback will be used)"}";
                break;

            case TargetType.AzureAppService:
                _detailsBox.Text =
                    "Target:        Azure App Service (Kudu zip deploy — runs without RDP)\n" +
                    $"App name:      {context.Component?.AzureAppServiceName ?? "(enter in pre-flight)"}\n" +
                    $"Resource group: {context.Component?.AzureResourceGroup ?? "(enter in pre-flight)"}";
                break;

            case TargetType.Plesk:
                _detailsBox.Text =
                    "Target:        Plesk shared hosting (SFTP upload)\n" +
                    $"Host:          {context.Component?.PleskHost ?? "(enter in pre-flight)"}\n" +
                    $"Site id:       {context.Component?.PleskSiteId ?? "(enter in pre-flight)"}";
                break;

            default:
                _detailsBox.Text = string.Empty;
                break;
        }
    }

    private static string Describe(Exception ex) =>
        ex is PlatformNotSupportedException or InvalidOperationException
            ? ex.Message
            : $"{ex.GetType().Name}: {ex.Message}";
}
