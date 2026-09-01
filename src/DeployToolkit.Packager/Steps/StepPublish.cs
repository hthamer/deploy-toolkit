using DeployToolkit.AppKit;
using DeployToolkit.Core.Publishing;
using DeployToolkit.Core.Registry;

namespace DeployToolkit.Packager.Steps;

/// <summary>
/// Step 2 (plan §10 step 3): discover the .csproj under the chosen folder,
/// pick a version, and run <c>dotnet publish</c> through
/// <see cref="DotNetPublisher"/> with streaming output into a
/// <see cref="LogPane"/>.
///
/// The publish settings are EDITABLE here — Visual Studio-style:
/// configuration, target framework, deployment mode, target runtime (RID)
/// and extra publish options. The framework auto-detects from the selected
/// project (a stale component value must never publish a net48 site with
/// -f net10.0), and the component's authoritative fields
/// (TargetFramework / IsSelfContained) are OVERWRITTEN with whatever the
/// user settles on, so the registry stays truthful for future runs.
/// RID + extra options remain client-level defaults (seeded from
/// <see cref="Client.PublishConfiguration"/>); they shape the session's
/// publish but are not written back to the component.
/// </summary>
internal sealed class StepPublish : WizardStep
{
    private readonly ComboBox _projectBox;
    private readonly Label _projectError;
    private readonly TextBox _versionBox;
    private readonly ComboBox _configurationBox;
    private readonly ComboBox _frameworkBox;
    private readonly ComboBox _deployModeBox;
    private readonly ComboBox _runtimeBox;
    private readonly TextBox _optionsBox;
    private readonly Label _frameworkHint;
    private readonly Label _settingsSummary;
    private readonly Label _statusLabel;
    private readonly Button _runButton;
    private readonly Button _cancelButton;
    private readonly LogPane _log;

    // ---- Publish options (Visual Studio publish-wizard parity) ----
    // Framework-specific structured options, shown/hidden based on the
    // detected project kind. .NET Framework Web Applications get the
    // precompile + App_Data checkboxes; modern .NET gets Single file +
    // ReadyToRun. See DetectProjectKind(). These are NOT `readonly` because
    // they are assigned via BuildPublishOptionsControls() (C# only allows
    // `readonly` assignments in the constructor body / a field initializer,
    // not in a method called from the constructor).
    private CheckBox _precompileBox = null!;
    private Button _configurePrecompileBtn = null!;
    private CheckBox _excludeAppDataBox = null!;
    private CheckBox _singleFileBox = null!;
    private CheckBox _readyToRunBox = null!;
    private Label _publishOptionsHint = null!;

    /// <summary>The precompile sub-options (VS "Precompile Options" dialog
    /// values). Session-level state — seeded with the VS defaults and edited
    /// via the Configure… button. Not persisted to the registry (keeps the
    /// change focused; precompile is a per-release decision for web apps).</summary>
    private WebPrecompileOptions _precompileOptions = WebPrecompileOptions.Default;

    /// <summary>Cached detection of whether the SELECTED project is a classic
    /// .NET Framework Web Application (needs MSBuild, not dotnet publish).
    /// Recomputed in DetectProjectKind() whenever the project changes.</summary>
    private bool _isWebApp;

    private Client? _client;
    private string? _loadedClientId;
    private string? _discoveredFolder;
    private CancellationTokenSource? _publishCts;
    private bool _publishing;

    /// <summary>The component the settings were last seeded from — detects
    /// an externally changed component (e.g. "Change component…" on the
    /// folder step) so the framework defaults re-apply.</summary>
    private DeploymentComponent? _seededComponent;

    /// <summary>Suppresses the settings change handlers while the controls
    /// are being filled programmatically (detection, client seeding) —
    /// otherwise every fill would persist a "user edit" that never happened.</summary>
    private bool _suppressSettingsEvents;

    /// <summary>True while the framework text holds the USER's own override
    /// — it must survive re-entry (re-detection would otherwise clobber a
    /// manually typed TFM that the project does not declare). Cleared when
    /// the project or the component changes.</summary>
    private bool _frameworkChosenByUser;

    /// <summary>Coalesces rapid setting edits (typing in the framework box)
    /// into one component update shortly after the last keystroke.</summary>
    private readonly System.Windows.Forms.Timer _componentSaveTimer;

    private const string PortableRuntime = "(project default)";

    public StepPublish(PackagerWizardForm wizard, PackageDraft draft)
        : base(wizard, draft)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ---- project discovery ----
        layout.Controls.Add(AppTheme.MakeSectionLabel("Project (.csproj)"));

        _projectBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
        };
        _projectBox.SelectedIndexChanged += (_, _) =>
        {
            ApplyFrameworkDefaults();
            ResetPublishState();
        };
        layout.Controls.Add(_projectBox);

        _projectError = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 22,
            Dock = DockStyle.Fill,
            ForeColor = Color.Firebrick,
        };
        layout.Controls.Add(_projectError);

        // ---- version ----
        layout.Controls.Add(AppTheme.MakeSectionLabel("Version"));
        _versionBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "e.g. 1.4.2 or 2026.08.31" };
        _versionBox.TextChanged += (_, _) =>
        {
            Draft.Version = NormalizeVersion(_versionBox.Text);
            ResetPublishState(); // the output folder is derived from the version
        };
        layout.Controls.Add(_versionBox);

        var versionHint = new Label
        {
            Text = "Required — trimmed, no spaces (e.g. 1.4.2 or 2026.08.31).",
            AutoSize = false,
            Height = 22,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
        };
        layout.Controls.Add(versionHint);

        // ---- editable publish settings (Visual Studio-style) ----
        var settingsTable = MakeFieldLayout(labelWidth: 150);
        var settingsRow = 0;

        _configurationBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _configurationBox.Items.AddRange(["Release", "Debug"]);
        _configurationBox.SelectedIndex = 0;
        _configurationBox.SelectedIndexChanged += (_, _) => UpdateSettingsSummary();
        AddField(settingsTable, ref settingsRow, "Configuration:", _configurationBox);

        _frameworkBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 220 };
        _frameworkBox.TextChanged += (_, _) => OnFrameworkEdited();
        _frameworkBox.SelectedIndexChanged += (_, _) => OnFrameworkEdited();
        AddField(settingsTable, ref settingsRow, "Target framework:", _frameworkBox);

        _frameworkHint = new Label
        {
            Text = "Auto-detected from the selected project — type to override.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 0, 8, 6),
        };
        settingsTable.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, settingsRow);
        settingsTable.Controls.Add(_frameworkHint, 1, settingsRow);
        settingsRow++;

        _deployModeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        _deployModeBox.Items.AddRange(["Framework-dependent", "Self-contained"]);
        _deployModeBox.SelectedIndexChanged += (_, _) => OnDeployModeChanged();
        AddField(settingsTable, ref settingsRow, "Deployment mode:", _deployModeBox);

        _runtimeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 220 };
        _runtimeBox.Items.AddRange([PortableRuntime, "win-x64", "win-x86", "win-arm64", "win-arm"]);
        _runtimeBox.SelectedIndexChanged += (_, _) => UpdateSettingsSummary();
        _runtimeBox.TextChanged += (_, _) => UpdateSettingsSummary();
        AddField(settingsTable, ref settingsRow, "Target runtime:", _runtimeBox);

        _optionsBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Extra arguments (e.g. /p:Foo=bar --nologo or -p:PublishTrimmed=false)" };
        _optionsBox.TextChanged += (_, _) => UpdateSettingsSummary();
        AddField(settingsTable, ref settingsRow, "Additional options:", _optionsBox);

        // ---- Publish options (Visual Studio publish-wizard parity) ----
        // Structured, framework-specific checkboxes — the same options VS
        // shows in its publish wizard. The free-text "Additional options"
        // above stays for power-user verbatim args; these checkboxes are the
        // first-class UI the user reported missing.
        BuildPublishOptionsControls();
        var publishOptionsStack = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoScroll = false,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 2, 12, 6),
        };
        var precompileRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
        };
        precompileRow.Controls.Add(_precompileBox);
        precompileRow.Controls.Add(_configurePrecompileBtn);
        publishOptionsStack.Controls.Add(precompileRow);
        publishOptionsStack.Controls.Add(_excludeAppDataBox);
        publishOptionsStack.Controls.Add(_singleFileBox);
        publishOptionsStack.Controls.Add(_readyToRunBox);
        publishOptionsStack.Controls.Add(_publishOptionsHint);
        // Added manually (not via AddField) so the stack keeps AutoSize —
        // AddField forces Dock=Fill, which can collapse an AutoSize
        // FlowLayoutPanel inside an AutoSize TableLayoutPanel row.
        settingsTable.Controls.Add(new Label
        {
            Text = "Publish options:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 6, 8, 2),
        }, 0, settingsRow);
        settingsTable.Controls.Add(publishOptionsStack, 1, settingsRow);
        settingsRow++;

        _settingsSummary = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Height = 58,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 8.25f),
            ForeColor = Color.Black,
        };
        var settingsGroup = new GroupBox
        {
            Text = "Publish settings (component + client defaults applied; your edits are saved back to the component)",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8),
            Controls = { settingsTable, _settingsSummary },
        };
        settingsTable.Dock = DockStyle.Top;
        _settingsSummary.Dock = DockStyle.Bottom;
        layout.Controls.Add(settingsGroup);

        // ---- run/cancel + status ----
        var actionRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _runButton = new Button { Text = "Run publish" };
        AppTheme.StyleButton(_runButton);
        _runButton.Click += (_, _) => _ = RunPublishAsync();
        _cancelButton = new Button { Text = "Cancel", Enabled = false };
        AppTheme.StyleButton(_cancelButton);
        _cancelButton.Click += (_, _) => _publishCts?.Cancel();
        _statusLabel = new Label
        {
            Text = string.Empty,
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
        };
        actionRow.Controls.Add(_runButton, 0, 0);
        actionRow.Controls.Add(_cancelButton, 1, 0);
        actionRow.Controls.Add(_statusLabel, 2, 0);
        layout.Controls.Add(actionRow);

        _log = new LogPane { Dock = DockStyle.Fill };
        var logGroup = new GroupBox
        {
            Text = "Publish output",
            Dock = DockStyle.Fill,
            Controls = { _log },
        };
        layout.Controls.Add(logGroup);

        Controls.Add(layout);

        // Coalesced component persistence: one save shortly after the last
        // edit instead of a registry write per keystroke.
        _componentSaveTimer = new System.Windows.Forms.Timer { Interval = 600 };
        _componentSaveTimer.Tick += (_, _) =>
        {
            _componentSaveTimer.Stop();
            SaveComponentPublishSettings();
        };

        // The wizard is an MDI child the shell (or the user) can close while a
        // publish runs: cancel the token so DotNetPublisher KILLS the dotnet
        // process tree instead of orphaning it, and let the continuation exit
        // quietly (nothing left to update on a disposed form).
        Wizard.FormClosed += (_, _) =>
        {
            _publishCts?.Cancel();
            _componentSaveTimer.Stop();
        };
    }

    public override string Title => "2. Publish";

    public override string Hint =>
        "Pick the .csproj and version, adjust the publish settings if needed (framework is auto-detected from the project " +
        "and saved back to the component), then run dotnet publish. Next unlocks only after a successful publish.";

    public override bool CanProceed => Draft.PublishSuccess;

    public override async void OnEnter()
    {
        // Discover csproj + fetch the client's publish default (registry IO)
        // under Guard; keep a successful publish intact when just navigating.
        // Project discovery is a RECURSIVE filesystem walk — a huge folder
        // would freeze the message pump if it ran on the UI thread (user-
        // reported freeze class), so it runs via Task.Run.
        var folderChanged = _discoveredFolder != Draft.FolderPath;
        await Guard.RunAsync(Wizard, "Preparing publish settings…", async cancellationToken =>
        {
            if (folderChanged && Draft.FolderPath is { } folder)
            {
                var projects = await Task.Run(() => DiscoverProjects(folder), cancellationToken);
                _discoveredFolder = folder;
                _suppressSettingsEvents = true;
                try
                {
                    _projectBox.Items.Clear();
                    foreach (var project in projects)
                        _projectBox.Items.Add(project);
                    if (projects.Count == 1)
                        _projectBox.SelectedIndex = 0;
                }
                finally { _suppressSettingsEvents = false; }
            }

            _client = Draft.Component is null
                ? null
                : await Wizard.Registry.GetClientAsync(Draft.Component.ClientId);
        });

        if (IsDisposed || Wizard.IsDisposed)
            return;

        // A component swapped on the folder step re-seeds everything,
        // including the framework default; an edit the user already made
        // stays (it was persisted into the component, so it IS the default).
        if (!ReferenceEquals(_seededComponent, Draft.Component))
        {
            _seededComponent = Draft.Component;
            _frameworkChosenByUser = false;
        }

        SeedSettingsFromDefaults();
        ApplyFrameworkDefaults();

        _versionBox.Text = Draft.Version ?? string.Empty;
        UpdateProjectError();
        UpdateSettingsSummary();
        Wizard.OnDraftChanged();
    }

    public override void OnLeave()
    {
        // Any pending framework/mode edit becomes the component's new truth
        // when the user moves on — no save left dangling on the timer.
        _componentSaveTimer.Stop();
        SaveComponentPublishSettings();
    }

    // ---------------------------------------------------------------
    // Settings seeding + persistence

    /// <summary>Seeds the session-level controls from the stored defaults:
    /// deployment mode from the component, RID + extra options from the
    /// client's publish configuration. Configuration stays Release unless
    /// the user says otherwise. Runs on every entry (cheap, and keeps the
    /// controls honest if the component was changed elsewhere); edits the
    /// user already made this session survive because the save path writes
    /// them INTO the component before they could be clobbered.</summary>
    private void SeedSettingsFromDefaults()
    {
        _suppressSettingsEvents = true;
        try
        {
            _deployModeBox.SelectedIndex = Draft.Component is { IsSelfContained: true } ? 1 : 0;

            var publishDefault = _client?.PublishConfiguration;
            if (!string.IsNullOrWhiteSpace(publishDefault?.TargetRuntime))
                _runtimeBox.Text = publishDefault.TargetRuntime!;
            else if (_loadedClientId is null || _loadedClientId != _client?.ClientId)
                _runtimeBox.Text = PortableRuntime; // first seed for this client — reset a stale RID

            if (!string.IsNullOrWhiteSpace(publishDefault?.AdditionalPublishOptions) && _optionsBox.Text.Length == 0)
                _optionsBox.Text = publishDefault.AdditionalPublishOptions!;

            if (_configurationBox.SelectedIndex < 0)
                _configurationBox.SelectedIndex = 0;

            _loadedClientId = _client?.ClientId;
        }
        finally { _suppressSettingsEvents = false; }
    }

    /// <summary>Reads the target framework(s) the SELECTED project declares
    /// and defaults the framework box to the best match: the component's
    /// value when the project actually targets it, otherwise the project's
    /// own first framework (the fix for "component said net10.0, the website
    /// is net48"). With no selection or no declared framework, falls back to
    /// the component's value.</summary>
    private void ApplyFrameworkDefaults()
    {
        if (_suppressSettingsEvents)
            return;

        // Detect the project kind FIRST — it drives which publish options
        // are visible and which publisher (dotnet vs msbuild) the run will
        // use. Independent of the framework text below.
        DetectProjectKind();

        var componentFramework = Draft.Component?.TargetFramework;
        var detected = new List<string>();

        if (_projectBox.SelectedItem is string project && project.Length > 0 && File.Exists(project))
            detected.AddRange(ProjectTargetFrameworkReader.ReadTargetFrameworks(project));

        _suppressSettingsEvents = true;
        try
        {
            _frameworkBox.Items.Clear();
            foreach (var tfm in detected)
                _frameworkBox.Items.Add(tfm);
            if (componentFramework is not null && !detected.Contains(componentFramework))
                _frameworkBox.Items.Add(componentFramework);

            // Keep the user's own override; otherwise default to the best
            // match: the component's value when the project actually targets
            // it, otherwise the project's own first framework (the fix for
            // "component said net10.0, the website is net48").
            if (!_frameworkChosenByUser)
            {
                var selected = componentFramework;
                if (detected.Count > 0)
                    selected = (componentFramework is not null && detected.Contains(componentFramework))
                        ? componentFramework
                        : detected[0];
                _frameworkBox.Text = selected ?? string.Empty;
            }
        }
        finally { _suppressSettingsEvents = false; }

        EnforceFrameworkGuards();
        UpdateSettingsSummary();
    }

    private void OnFrameworkEdited()
    {
        if (_suppressSettingsEvents)
            return;

        _frameworkChosenByUser = true; // the user's override now owns the box
        EnforceFrameworkGuards();
        UpdateSettingsSummary();
        ScheduleComponentSave();
    }

    private void OnDeployModeChanged()
    {
        if (_suppressSettingsEvents)
            return;

        UpdateSettingsSummary();
        ScheduleComponentSave();
    }

    /// <summary>Self-contained is not a thing for .NET Framework targets —
    /// when the framework is net4x the mode control is forced back to
    /// framework-dependent and disabled, with the reason in the hint.</summary>
    private void EnforceFrameworkGuards()
    {
        var isNetFramework = IsNetFrameworkTfm(_frameworkBox.Text);

        _suppressSettingsEvents = true;
        try
        {
            if (isNetFramework && _deployModeBox.SelectedIndex != 0)
                _deployModeBox.SelectedIndex = 0;
        }
        finally { _suppressSettingsEvents = false; }

        _deployModeBox.Enabled = !isNetFramework;
        _frameworkHint.Text = isNetFramework
            ? "Auto-detected from the selected project. Self-contained is not available for .NET Framework targets."
            : "Auto-detected from the selected project — type to override.";
    }

    // ---------------------------------------------------------------
    // Publish options (Visual Studio publish-wizard parity)

    /// <summary>Constructs the structured publish-options controls (kept
    /// here so the constructor body stays readable). Visibility is toggled
    /// later by <see cref="DetectProjectKind"/> based on the selected
    /// project — only the options that apply to the project's toolchain are
    /// shown, the rest are hidden AND unchecked so they never leak into the
    /// generated command line.</summary>
    private void BuildPublishOptionsControls()
    {
        // .NET Framework Web Application options (published via MSBuild + WPP).
        _precompileBox = new CheckBox
        {
            Text = "Precompile during publishing",
            AutoSize = true,
        };
        _precompileBox.CheckedChanged += (_, _) =>
        {
            _configurePrecompileBtn.Enabled = _precompileBox.Checked;
            UpdateSettingsSummary();
        };

        _configurePrecompileBtn = new Button
        {
            Text = "Configure…",
            Enabled = false,
            AutoSize = true,
        };
        AppTheme.StyleButton(_configurePrecompileBtn);
        _configurePrecompileBtn.Click += (_, _) => OpenPrecompileOptions();

        _excludeAppDataBox = new CheckBox
        {
            Text = "Exclude files from the App_Data folder",
            AutoSize = true,
        };
        _excludeAppDataBox.CheckedChanged += (_, _) => UpdateSettingsSummary();

        // Modern .NET options (published via dotnet publish).
        _singleFileBox = new CheckBox
        {
            Text = "Produce Single file",
            AutoSize = true,
        };
        _singleFileBox.CheckedChanged += (_, _) => UpdateSettingsSummary();

        _readyToRunBox = new CheckBox
        {
            Text = "Enable ReadyToRun compilation",
            AutoSize = true,
        };
        _readyToRunBox.CheckedChanged += (_, _) => UpdateSettingsSummary();

        _publishOptionsHint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = string.Empty,
        };
    }

    /// <summary>Detects whether the SELECTED project is a classic .NET
    /// Framework Web Application (the kind that imports
    /// Microsoft.WebApplication.targets and needs the full Visual Studio
    /// MSBuild, not <c>dotnet publish</c>) and toggles the framework-specific
    /// publish options accordingly:
    /// <list type="bullet">
    ///  <item>.NET Framework Web App → show Precompile (+ Configure…) and
    ///   Exclude App_Data; hide Single file / ReadyToRun; disable the target
    ///   runtime (web apps don't use a RID — VS shows "(project default)"
    ///   and makes it read-only too).</item>
    ///  <item>Modern .NET → show Produce Single file / Enable ReadyToRun;
    ///   hide the .NET Framework web options; enable the runtime box.</item>
    /// </list>
    /// Hidden options are also UNCHECKED so a stale tick on a now-hidden
    /// checkbox can never leak into the generated command line.</summary>
    private void DetectProjectKind()
    {
        var isWebApp = _projectBox.SelectedItem is string project
            && project.Length > 0
            && File.Exists(project)
            && WebProjectDetector.IsNetFrameworkWebApp(project);
        _isWebApp = isWebApp;

        // .NET Framework Web Application options — visible only for web apps.
        _precompileBox.Visible = isWebApp;
        _configurePrecompileBtn.Visible = isWebApp;
        _excludeAppDataBox.Visible = isWebApp;

        // Modern .NET options — visible only for non-web (dotnet publish) projects.
        _singleFileBox.Visible = !isWebApp;
        _readyToRunBox.Visible = !isWebApp;

        // Hidden = unchecked, so a stale tick never leaks into the command line.
        if (!isWebApp)
        {
            _precompileBox.Checked = false;
            _excludeAppDataBox.Checked = false;
        }
        else
        {
            _singleFileBox.Checked = false;
            _readyToRunBox.Checked = false;
        }

        // Web apps don't use a runtime identifier — VS shows "(project
        // default)" read-only. Mirror that so the user isn't tempted to pick
        // a RID that msbuild would reject as an unknown switch.
        if (isWebApp)
        {
            _suppressSettingsEvents = true;
            try { _runtimeBox.Text = PortableRuntime; }
            finally { _suppressSettingsEvents = false; }
        }
        _runtimeBox.Enabled = !isWebApp;

        _publishOptionsHint.Text = isWebApp
            ? "Published with Visual Studio MSBuild (Web Publishing Pipeline). These options map to /p:PrecompileBeforePublish and /p:ExcludeApp_Data."
            : "Published with dotnet publish. These options map to -p:PublishSingleFile and -p:PublishReadyToRun.";
    }

    /// <summary>Opens the VS-style Precompile Options dialog seeded with the
    /// current sub-options; stores the result back when the user OKs. The
    /// Configure button is only enabled while Precompile is checked.</summary>
    private void OpenPrecompileOptions()
    {
        using var dialog = new PrecompileOptionsDialog(_precompileOptions);
        if (dialog.ShowDialog(Wizard) == DialogResult.OK && dialog.Result is { } result)
        {
            _precompileOptions = result;
            UpdateSettingsSummary();
        }
    }

    private void ScheduleComponentSave()
    {
        if (Draft.Component is null)
            return;
        _componentSaveTimer.Stop();
        _componentSaveTimer.Start();
    }

    /// <summary>Persists the framework/deployment-mode edits INTO the
    /// component (init-only model → rebuilt instance, same ComponentId), so
    /// the registry never keeps a framework the user already corrected.</summary>
    private void SaveComponentPublishSettings()
    {
        var component = Draft.Component;
        if (component is null || IsDisposed || Wizard.IsDisposed)
            return;

        var framework = _frameworkBox.Text.Trim();
        if (framework.Length == 0)
            return; // nothing to store — the box never seeds an empty framework

        var selfContained = !IsNetFrameworkTfm(framework) && _deployModeBox.SelectedIndex == 1;

        if (string.Equals(framework, component.TargetFramework, StringComparison.OrdinalIgnoreCase) &&
            selfContained == component.IsSelfContained)
            return; // unchanged — no registry churn

        var updated = new DeploymentComponent
        {
            ComponentId = component.ComponentId,
            ClientId = component.ClientId,
            Name = component.Name,
            TargetType = component.TargetType,
            TargetFramework = framework,
            IsSelfContained = selfContained,
            IisSiteName = component.IisSiteName,
            IisAppPath = component.IisAppPath,
            AzureAppServiceName = component.AzureAppServiceName,
            AzureResourceGroup = component.AzureResourceGroup,
            PleskHost = component.PleskHost,
            PleskSiteId = component.PleskSiteId,
            HealthCheckUrl = component.HealthCheckUrl,
            DbConnectionRef = component.DbConnectionRef,
        };

        Draft.Component = updated; // the wizard keeps working with the new values immediately
        UpdateSettingsSummary();
        Wizard.OnDraftChanged();

        _ = Guard.RunAsync(Wizard, "Saving component publish settings…", async _ =>
            await Wizard.Registry.UpdateComponentAsync(updated));
    }

    // ---------------------------------------------------------------
    // Publish execution (own streaming surface — not the generic Guard:
    // failures render into the red status label + log pane instead of a
    // modal error, so partial build output stays visible).

    private async Task RunPublishAsync()
    {
        if (_publishing)
            return;

        var component = Draft.Component;
        if (component is null || _projectBox.SelectedItem is not string project || project.Length == 0)
        {
            AppTheme.Error(Wizard, "Pick a .csproj first (no project was found under the folder).");
            return;
        }

        var version = NormalizeVersion(_versionBox.Text);
        if (version is null)
        {
            AppTheme.Error(Wizard, "Version is required (e.g. 1.4.2 or 2026.08.31 — no spaces).");
            return;
        }

        // Re-confirm the project kind right before publishing — guarantees the
        // msbuild/dotnet routing matches the actually-selected csproj even if
        // detection was somehow skipped between selection and Run.
        _isWebApp = WebProjectDetector.IsNetFrameworkWebApp(project);

        // A pending settings edit becomes the component's truth right now —
        // the publish below must use exactly what will be stored.
        _componentSaveTimer.Stop();
        SaveComponentPublishSettings();
        component = Draft.Component;

        var safeName = MakeSafeFileName(component.Name);
        var outputDir = Path.Combine(Path.GetTempPath(), "DeployToolkit", "publish", $"{safeName}-{version}");
        var settings = BuildPublishSettings(project, outputDir);

        // Re-running publish resets the downstream state.
        Draft.PublishSuccess = false;
        Draft.PublishOutputRoot = null;
        Draft.Version = version;
        Wizard.OnDraftChanged();

        _publishing = true;
        _publishCts = new CancellationTokenSource();
        _runButton.Enabled = false;
        _cancelButton.Enabled = true;
        _log.ClearAll();
        _statusLabel.ForeColor = Color.DimGray;
        _statusLabel.Text = "Publishing…";

        try
        {
            if (Directory.Exists(outputDir))
                await Task.Run(() => Directory.Delete(outputDir, recursive: true), _publishCts.Token);

            // Route to the right publisher: .NET Framework Web Applications
            // (classic csproj importing Microsoft.WebApplication.targets) need
            // the full Visual Studio MSBuild + WPP targets — `dotnet publish`
            // fails there with MSB4019 (missing Microsoft.WebApplication.targets)
            // and "Nothing to do. None of the projects specified contain
            // packages to restore" (packages.config is invisible to
            // `dotnet restore`). Everything else goes through `dotnet publish`.
            var result = _isWebApp
                ? await MsBuildPublisher.PublishAsync(
                    settings,
                    line => _log.AppendLine(line),
                    timeoutMinutes: 15,
                    _publishCts.Token)
                : await DotNetPublisher.PublishAsync(
                    settings,
                    line => _log.AppendLine(line),
                    timeoutMinutes: 15,
                    _publishCts.Token);

            if (IsDisposed || Wizard.IsDisposed)
                return; // wizard closed mid-publish — nothing left to report to

            if (result.Success)
            {
                Draft.PublishOutputRoot = outputDir;
                Draft.PublishSuccess = true;
                _statusLabel.ForeColor = Color.ForestGreen;
                var fileCount = await Task.Run(() => CountFiles(outputDir), _publishCts.Token);
                if (IsDisposed || Wizard.IsDisposed)
                    return;
                _statusLabel.Text = $"Publish OK — {fileCount} files in {outputDir}";
            }
            else
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = _publishCts.IsCancellationRequested
                    ? "Publish cancelled."
                    : $"Publish failed ({(result.TimedOut ? "timed out" : $"exit code {result.ExitCode}")}): {result.ErrorSummary}";
            }
        }
        catch (OperationCanceledException)
        {
            if (IsDisposed || Wizard.IsDisposed)
                return;
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Publish cancelled.";
        }
        catch (Exception ex)
        {
            if (IsDisposed || Wizard.IsDisposed)
                return;
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = DescribeException(ex);
        }
        finally
        {
            _publishing = false;
            _publishCts?.Dispose();
            _publishCts = null;
            if (!IsDisposed && !Wizard.IsDisposed)
            {
                _runButton.Enabled = true;
                _cancelButton.Enabled = false;
                Wizard.OnDraftChanged();
            }
        }
    }

    /// <summary>Builds the publish settings from the EDITABLE controls (the
    /// component's stored values only matter as the defaults those controls
    /// were seeded with). The RID is folded into the additional arguments as
    /// <c>-r &lt;rid&gt;</c> — the same convention
    /// <see cref="PublishConfiguration.ToPublishSettings"/> uses — but ONLY
    /// for non-web projects: a .NET Framework Web Application is published
    /// with MSBuild, which does not understand <c>-r</c> (and web apps don't
    /// use a runtime identifier anyway). The structured publish-option
    /// checkboxes (<see cref="ProduceSingleFile"/> / <see cref="ReadyToRun"/>
    /// for modern .NET; <see cref="Precompile"/> / <see cref="ExcludeAppData"/>
    /// for .NET Framework web apps) are populated only when their checkbox
    /// is visible AND checked, so a stale tick on a hidden option never leaks
    /// into the command line.</summary>
    private PublishSettings BuildPublishSettings(string projectPath, string outputDirectory)
    {
        var framework = _frameworkBox.Text.Trim();
        var options = _optionsBox.Text.Trim();
        var runtime = _runtimeBox.Text.Trim();

        var additional = string.Empty;
        // Web apps are published via MSBuild (no -r) — skip RID folding so a
        // leftover runtime value can't produce a bogus switch msbuild rejects.
        if (!_isWebApp &&
            runtime.Length > 0 && !string.Equals(runtime, PortableRuntime, StringComparison.OrdinalIgnoreCase))
        {
            if (runtime.Contains(' '))
                throw new ArgumentException("Target runtime must be a single runtime identifier (e.g. win-x64) or empty.");
            additional = $"-r {runtime}";
        }

        if (options.Length > 0)
            additional = additional.Length == 0 ? options : $"{additional} {options}";

        // Structured publish options: only set when the relevant checkbox is
        // visible (i.e., applicable to the detected project kind) AND checked.
        bool? produceSingleFile = (_singleFileBox.Visible && _singleFileBox.Checked) ? true : null;
        bool? readyToRun = (_readyToRunBox.Visible && _readyToRunBox.Checked) ? true : null;
        bool? precompile = (_precompileBox.Visible && _precompileBox.Checked) ? true : null;
        WebPrecompileOptions? precompileOptions = (_precompileBox.Visible && _precompileBox.Checked)
            ? _precompileOptions
            : null;
        bool? excludeAppData = (_excludeAppDataBox.Visible && _excludeAppDataBox.Checked) ? true : null;

        return new PublishSettings(
            ProjectPath: projectPath,
            TargetFramework: framework.Length == 0 ? null : framework,
            SelfContained: !IsNetFrameworkTfm(framework) && _deployModeBox.SelectedIndex == 1,
            Configuration: _configurationBox.Text,
            OutputDirectory: outputDirectory,
            AdditionalArguments: additional.Length == 0 ? null : additional,
            ProduceSingleFile: produceSingleFile,
            ReadyToRun: readyToRun,
            Precompile: precompile,
            PrecompileOptions: precompileOptions,
            ExcludeAppData: excludeAppData);
    }

    private void UpdateSettingsSummary()
    {
        if (Draft.Component is null || _projectBox.SelectedItem is not string project || project.Length == 0)
        {
            _settingsSummary.Text = string.Empty;
            return;
        }

        var outputDir = Path.Combine(
            Path.GetTempPath(), "DeployToolkit", "publish",
            $"{MakeSafeFileName(Draft.Component.Name)}-{NormalizeVersion(_versionBox.Text)}");

        // Show the actual command the run will use: `msbuild` for .NET
        // Framework Web Applications, `dotnet publish` for everything else.
        // This is the line the user reported as confusing (it showed
        // `dotnet publish` for a net48 WebForms site, which is what failed).
        var tool = _isWebApp ? "msbuild" : "dotnet";
        string commandLine;
        try
        {
            var settings = BuildPublishSettings(project, outputDir);
            commandLine = _isWebApp
                ? $"{tool} {MsBuildPublisher.BuildArguments(settings)}"
                : $"{tool} {DotNetPublisher.BuildArguments(settings)}";
        }
        catch (ArgumentException ex)
        {
            commandLine = ex.Message;
        }

        _settingsSummary.Text =
            $"Project: {project}\n" +
            $"{commandLine}\n" +
            $"Configuration: {_configurationBox.Text}    Framework: {(string.IsNullOrWhiteSpace(_frameworkBox.Text) ? "(project default)" : _frameworkBox.Text.Trim())}    " +
            $"Self-contained: {(_deployModeBox.SelectedIndex == 1 ? "yes" : "no")}";
    }

    private void UpdateProjectError()
    {
        if (_projectBox.Items.Count == 0 && Draft.FolderPath is not null)
        {
            _projectError.Text = $"No .csproj found under '{Draft.FolderPath}' — this step cannot proceed.";
        }
        else
        {
            _projectError.Text = string.Empty;
        }
    }

    private void ResetPublishState()
    {
        if (!Draft.PublishSuccess && Draft.PublishOutputRoot is null)
        {
            Wizard.OnDraftChanged();
            return;
        }

        Draft.PublishSuccess = false;
        Draft.PublishOutputRoot = null;
        Wizard.OnDraftChanged();
    }

    // ---------------------------------------------------------------
    // Helpers

    /// <summary>True for the .NET Framework TFM spellings (net20 … net48,
    /// including patch forms like net462) — self-contained publishing does
    /// not exist for them. Modern .NET (net5, net8.0, net10.0) and other
    /// families (netstandard2.0, netcoreapp3.1) are not.</summary>
    private static bool IsNetFrameworkTfm(string? tfm)
    {
        var trimmed = tfm?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return false;

        var digits = trimmed[3..];
        return digits.Length >= 2 && digits.All(char.IsDigit) && digits[0] is '2' or '3' or '4';
    }

    private static string? NormalizeVersion(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string DescribeException(Exception ex) =>
        ex is ArgumentException or InvalidOperationException
            ? ex.Message
            : $"{ex.GetType().Name}: {ex.Message}";

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "component" : safe.Trim();
    }

    private static int CountFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();

    /// <summary>Recursively finds .csproj files under the folder, skipping
    /// build/dependency directories (bin, obj, .git).</summary>
    private static List<string> DiscoverProjects(string rootFolder)
    {
        var results = new List<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git" };

        void Walk(string directory)
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(directory, "*.csproj"));
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    if (skip.Contains(Path.GetFileName(sub)))
                        continue;
                    Walk(sub);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // unreadable subtree — skip it, the remaining tree still counts
            }
        }

        Walk(rootFolder);
        return results;
    }
}
