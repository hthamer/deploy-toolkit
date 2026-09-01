using DeployToolkit.AppKit;
using DeployToolkit.Core.Packaging;
using DeployToolkit.Core.Registry;
using DeployToolkit.Packager.Steps;

namespace DeployToolkit.Packager;

/// <summary>
/// The plan §10 packaging wizard: a left-hand step list (disabled steps
/// grayed) and a right-hand content panel hosting one <see cref="WizardStep"/>
/// at a time, with Back / Next / Finish at the bottom. Steps are linear —
/// a step becomes selectable once every step before it has been completed
/// — and Finish is enabled only after a successful package build.
///
/// Hosted by the shell as an MDI child, the wizard is the one STATEFUL
/// screen: closing it mid-run would silently discard the whole draft, so it
/// implements <see cref="IGuardedCloseScreen"/> — the shell asks before
/// switching it away, and the form itself prompts on the X button / app
/// close. The window title tracks the draft (component, version, step) so
/// the shell's MDI window list stays readable.
/// </summary>
public sealed class PackagerWizardForm : Form, IGuardedCloseScreen
{
    private readonly List<WizardStep> _steps;
    private readonly ListBox _stepList;
    private readonly Panel _contentPanel;
    private readonly Button _backButton;
    private readonly Button _nextButton;
    private readonly Button _finishButton;
    private readonly Label _hintLabel;

    private int _currentIndex;
    private int _maxReachedIndex;
    private bool _closeSuppressed; // shell already collected consent (CloseWithoutPrompt)

    public PackagerWizardForm(IRegistryStore registry, PackageBuilder builder)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Builder = builder ?? throw new ArgumentNullException(nameof(builder));
        Draft = new PackageDraft();

        Text = "New package — DeployToolkit Packager";
        AppTheme.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 680);
        MinimumSize = new Size(900, 600);

        _steps = new List<WizardStep>
        {
            new StepFolder(this, Draft),
            new StepPublish(this, Draft),
            new StepDiff(this, Draft),
            new StepDelta(this, Draft),
            new StepScripts(this, Draft),
            new StepBuild(this, Draft),
            new StepDone(this, Draft),
        };

        _stepList = new ListBox
        {
            Dock = DockStyle.Left,
            Width = 220,
            BorderStyle = BorderStyle.FixedSingle,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 24,
            IntegralHeight = false,
            Font = new Font(AppTheme.FontFamily, 9.75f),
        };
        foreach (var step in _steps)
            _stepList.Items.Add(step.Title);
        _stepList.DrawItem += StepsList_DrawItem;
        _stepList.SelectedIndexChanged += StepsList_SelectedIndexChanged;

        _contentPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        _hintLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Padding = new Padding(12, 0, 8, 0),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            Width = 300,
            Padding = new Padding(4, 8, 12, 8),
            WrapContents = false,
        };
        _backButton = new Button { Text = "< Back" };
        _nextButton = new Button { Text = "Next >" };
        _finishButton = new Button { Text = "Finish", Enabled = false };
        AppTheme.StyleButton(_backButton);
        AppTheme.StyleButton(_nextButton);
        AppTheme.StyleButton(_finishButton);
        _nextButton.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold);
        _finishButton.Font = new Font(AppTheme.FontFamily, 9f, FontStyle.Bold);
        _backButton.Click += (_, _) => { if (_currentIndex > 0) GoToStep(_currentIndex - 1); };
        _nextButton.Click += (_, _) => { if (_currentIndex < _steps.Count - 1) GoToStep(_currentIndex + 1); };
        _finishButton.Click += (_, _) => { if (_currentIndex < _steps.Count - 1) GoToStep(_currentIndex + 1); };
        buttons.Controls.Add(_backButton);
        buttons.Controls.Add(_nextButton);
        buttons.Controls.Add(_finishButton);

        bottom.Controls.Add(_hintLabel);
        bottom.Controls.Add(buttons);

        Controls.Add(_contentPanel);
        Controls.Add(_stepList);
        Controls.Add(bottom);

        _maxReachedIndex = 0;
        ShowStep(0, navigate: false);

        // Step 6 flushes the delta grid into the draft right before a build
        // (defensive — the delta step normally commits on leave).
        ((StepBuild)_steps[5]).SetDeltaCommit(() => ((StepDelta)_steps[3]).Commit());
    }

    /// <summary>The open registry store this wizard reads/writes.</summary>
    public IRegistryStore Registry { get; }

    /// <summary>The package builder (folder mapping, stale checks, builds).</summary>
    public PackageBuilder Builder { get; }

    /// <summary>State accumulated across the steps (internal — only the
    /// wizard and its steps, all in this assembly, touch the draft).</summary>
    internal PackageDraft Draft { get; }

    /// <summary>True when the draft holds work that would be LOST by closing
    /// the wizard: the user moved past the folder step (or already resolved a
    /// folder+component through git sync) and no build has completed yet.
    /// Once a build succeeded the work is persisted (zip + registry row),
    /// so closing afterwards is lossless and never prompts.</summary>
    public bool HasDraftProgress =>
        Draft.BuildResult is null
        && (_currentIndex > 0 || (Draft.Component is not null && Draft.FolderPath is not null));

    /// <inheritdoc />
    public bool HasUnsavedWork => HasDraftProgress;

    /// <inheritdoc />
    public string UnsavedWorkDescription =>
        $"the package wizard is in progress (step {_currentIndex + 1} of {_steps.Count})";

    /// <inheritdoc />
    public void CloseWithoutPrompt()
    {
        // The shell confirmed with the user — skip the form's own guard.
        _closeSuppressed = true;
        Close();
    }

    /// <summary>Steps call this whenever the draft changed so the buttons refresh.</summary>
    public void OnDraftChanged()
    {
        RefreshButtons();
        UpdateTitle();
    }

    // ---------------------------------------------------------------
    // Navigation

    private void GoToStep(int index)
    {
        if (index == _currentIndex)
            return;
        if (index > _maxReachedIndex)
            return;

        _steps[_currentIndex].OnLeave();
        ShowStep(index, navigate: true);
    }

    private void ShowStep(int index, bool navigate)
    {
        _currentIndex = index;
        if (index > _maxReachedIndex)
            _maxReachedIndex = index;

        _contentPanel.Controls.Clear();
        var step = _steps[index];
        _contentPanel.Controls.Add(step);
        step.OnEnter();

        _stepList.SelectedIndex = index; // redraws via OwnerDraw
        RefreshButtons();
        UpdateTitle();
    }

    private void RefreshButtons()
    {
        var step = _steps[_currentIndex];

        _hintLabel.Text = step.Hint;

        _backButton.Visible = _currentIndex > 0 && _currentIndex < _steps.Count - 1;
        _backButton.Enabled = _currentIndex > 0;

        var isBuildStep = _currentIndex == _steps.Count - 2; // step 6: Build package
        _nextButton.Visible = !isBuildStep && _currentIndex < _steps.Count - 1;
        _finishButton.Visible = isBuildStep;

        // Linear reachability (plan §10): completing the current step unlocks
        // the NEXT one — for the Next button and for clicking the step list.
        // Without this the wizard deadlocked on step 1: GoToStep refuses any
        // index beyond _maxReachedIndex, but _maxReachedIndex was only ever
        // raised by ShowStep, which only runs once a navigation is already
        // allowed — so Next (and the step list) could never advance anywhere.
        if (step.CanProceed && _currentIndex + 1 < _steps.Count && _maxReachedIndex < _currentIndex + 1)
            _maxReachedIndex = _currentIndex + 1;

        _nextButton.Enabled = step.CanProceed;
        _finishButton.Enabled = Draft.BuildResult is not null;
        _stepList.Invalidate();
    }

    /// <summary>"Build another package for this component" (StepDone): keeps
    /// the folder/component choice, resets everything downstream, jumps to
    /// the publish step.</summary>
    internal void RestartFromPublishStep()
    {
        Draft.ResetForRebuild();
        _maxReachedIndex = 1;
        ShowStep(1, navigate: true);
    }

    // ---------------------------------------------------------------
    // Close guard (MDI child): the draft lives only in memory

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel)
            return;

        // Never fight a hard OS/task-manager close.
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
            return;

        // Shell-driven closes arrive with consent already collected.
        if (_closeSuppressed || !HasDraftProgress)
            return;

        // The wizard FORM stays enabled while a Guard runs on it (the busy
        // dialog freezes only the content, keeping MDI activation stable) —
        // detect busy via Guard, not via Enabled alone.
        var busyHint = Enabled && !Guard.IsBusy(this)
            ? string.Empty
            : "A publish/build operation may still be running — it will be cancelled or detached.\n\n";
        var message =
            $"This {UnsavedWorkDescription}. {busyHint}" +
            "Closing it discards the draft — the chosen folder, publish output, diff selections, " +
            "appsettings delta and DB scripts are not kept anywhere else.\n\n" +
            "Close and discard the draft?";

        var answer = MessageBox.Show(this, message, AppTheme.Caption, MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            e.Cancel = true; // keep the wizard — closing the shell is aborted too
        else
            _closeSuppressed = true; // consented; don't re-ask if close re-enters
    }

    /// <summary>MDI window list readability: title tracks the draft. Also the
    /// shell's status cell shows it.</summary>
    private void UpdateTitle()
    {
        var label = (Draft.Component?.Name, Draft.Version) switch
        {
            ({ } component, { } version) => $"{component} {version}",
            ({ } component, _) => $"{component} — step {_currentIndex + 1} of {_steps.Count}",
            _ => _currentIndex == 0 ? "New package" : $"New package — step {_currentIndex + 1}",
        };
        Text = $"{label} — DeployToolkit Packager";
    }

    // ---------------------------------------------------------------
    // Step list drawing

    private void StepsList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _steps.Count)
            return;

        var available = e.Index <= _maxReachedIndex;
        var selected = e.Index == _currentIndex;

        var backColor = selected ? Color.FromArgb(204, 228, 247) : Color.White;
        using (var backBrush = new SolidBrush(backColor))
            e.Graphics.FillRectangle(backBrush, e.Bounds);

        var textColor = available ? Color.Black : SystemColors.GrayText;
        var fontStyle = selected ? FontStyle.Bold : FontStyle.Regular;
        using var textBrush = new SolidBrush(textColor);
        using var font = new Font(AppTheme.FontFamily, 9.75f, fontStyle);
        var bounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        var format = new StringFormat { LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(_steps[e.Index].Title, font, textBrush, bounds, format);
    }

    private void StepsList_SelectedIndexChanged(object? sender, EventArgs e)
    {
        // Clicking a not-yet-reachable step snaps back to the current one.
        if (_stepList.SelectedIndex != _currentIndex)
        {
            if (_stepList.SelectedIndex >= 0 && _stepList.SelectedIndex <= _maxReachedIndex)
                GoToStep(_stepList.SelectedIndex);
            else
                _stepList.SelectedIndex = _currentIndex;
        }
    }
}
