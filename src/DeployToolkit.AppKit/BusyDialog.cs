using System.Runtime.CompilerServices;

namespace DeployToolkit.AppKit;

/// <summary>
/// The busy state shown by <see cref="Guard"/> while an async operation runs.
/// Replaces the old fire-and-forget BusyOverlay after user feedback: the old
/// overlay disabled the owner and offered NO way out, so any hung operation
/// (git fetch against a dead VPN, a stuck connection) bricked the whole app
/// and had to be killed from Task Manager ("everything readonly, cannot
/// close, had to force close").
///
/// The busy dialog fixes that class of problem:
///  - it stays fully responsive (it lives on the UI thread, which Guard keeps
///    free by running work as awaitable tasks),
///  - it is shown with a short DELAY (<see cref="ShowDelay"/>): operations
///    that finish inside the delay never flash a popup at all (user-reported:
///    opening the Clients screen or a client's details showed a dialog that
///    "popped up and closed quickly" because local-registry loads are
///    near-instant),
///  - it is CENTERED over its owner's on-screen rectangle — computed with
///    <see cref="Control.RectangleToScreen"/>, which also resolves MDI
///    children (user-reported: the "Testing connection…" dialog appeared
///    offset because an MDI child's Left/Top are MDI-client coordinates, not
///    screen coordinates),
///  - it has a Cancel button (and Esc) that cancels the operation's
///    cancellation token and — if the operation cannot observe the token
///    within a short grace period — abandons it: the UI is freed and the
///    operation's eventual completion is silently ignored,
///  - the owner's INPUT is blocked while it shows (preventing re-entrancy)
///    and restored when the dialog is disposed (idempotent). For MDI-child
///    owners the child's CONTENT controls are disabled instead of the child
///    FORM: disabling an active MDI child window makes the MDI frame move
///    activation to the next child (user-reported: the still-open package
///    wizard kept jumping in front of the Clients screen, which then looked
///    like "the Clients page did not open" until it was clicked again), and
///    that activation is never restored automatically — so the form stays
///    enabled, keeps the front position, and only its content is frozen,
///  - the owner window is disabled while it shows (preventing re-entrancy)
///    and restored when the dialog is disposed (idempotent).
/// </summary>
public sealed class BusyDialog : Form
{
    /// <summary>How long an operation must run before this dialog becomes
    /// visible. Short enough that slow operations still get timely feedback
    /// (and a way to cancel); long enough that the many near-instant
    /// registry reads (client list, client components, packages) never flash
    /// a dialog. The owner's input is blocked from the START regardless —
    /// re-entrancy protection does not wait for the delay.</summary>
    internal static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Nesting bookkeeping per owner form. A guarded operation can
    /// itself trigger another guarded operation on the same owner (e.g. a
    /// combo's SelectedIndexChanged firing during a programmatic fill inside
    /// the first guard's continuation). Without the depth counter each
    /// BusyDialog captured "owner is disabled" from the dialog above it and
    /// re-applied that stale state on dispose — the inner dialog closed
    /// after the outer one and left the owner PERMANENTLY disabled (a grey
    /// dead form that cannot be closed). The owner is restored only when the
    /// outermost dialog goes away. A <see cref="ConditionalWeakTable"/> keeps
    /// the state alive exactly as long as the owner form.</summary>
    private static readonly ConditionalWeakTable<Form, OwnerBusyState> OwnerBusyStates = new();

    private sealed class OwnerBusyState
    {
        public int Depth;

        /// <summary>Non-MDI owners: the form itself is disabled.</summary>
        public bool FormEnabled = true;

        /// <summary>MDI-child owners: the child's direct CONTENT controls are
        /// disabled instead of the form (disabling the active MDI child form
        /// itself shifts MDI activation to the next child and never returns
        /// it — see the class comment). Null when the form-disable path is
        /// used.</summary>
        public List<(Control Control, bool Enabled)>? ContentStates;

        public Cursor Cursor = Cursors.Default;
    }

    private readonly Form _owner;
    private readonly Label _statusLabel;
    private readonly Label _elapsedLabel;
    private readonly Button _cancelButton;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _showTimer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _cancelRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _shown;
    private bool _disposed;

    private BusyDialog(Form owner, string busyText)
    {
        _owner = owner;

        var state = OwnerBusyStates.GetOrCreateValue(owner);
        if (state.Depth == 0)
            DisableOwnerInteraction(owner, state);
        state.Depth++;

        _statusLabel = new Label
        {
            Text = busyText,
            AutoSize = false,
            Height = 34,
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Padding = new Padding(2, 4, 2, 0),
        };

        var progress = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Height = 14,
        };

        _elapsedLabel = new Label
        {
            Text = UiText.Elapsed(TimeSpan.Zero),
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 6, 2, 2),
            Anchor = AnchorStyles.Left,
        };

        _cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.None };
        AppTheme.StyleButton(_cancelButton);
        _cancelButton.Margin = new Padding(8, 4, 0, 2);
        _cancelButton.Click += (_, _) => RequestCancel();

        var bottomRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0),
        };
        bottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottomRow.Controls.Add(_elapsedLabel, 0, 0);
        bottomRow.Controls.Add(_cancelButton, 1, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(14, 10, 14, 10),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_statusLabel);
        layout.Controls.Add(progress);
        layout.Controls.Add(bottomRow);

        Controls.Add(layout);

        Text = "Working…";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.Manual;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        // Deliberately NOT TopMost: an owned window already stays above its
        // owner, while TopMost kept the dialog floating ABOVE modal dialogs
        // and message boxes opened by the guarded work — covering them with
        // an unclickable sheet (the "reconnecting dialog I cannot get rid
        // of" incident). Modals now take their natural place above it.
        KeyPreview = true;
        AppTheme.Apply(this);
        Size = new Size(480, 148);
        CancelButton = _cancelButton; // Esc routes through the button's handler below

        // Delayed visibility: the dialog instance exists (and blocks the
        // owner's input) from the start, but only APPEARS once the operation
        // has outlived <see cref="ShowDelay"/>. Fast operations dispose the
        // dialog before that — no popup flash.
        _showTimer = new System.Windows.Forms.Timer { Interval = (int)ShowDelay.TotalMilliseconds };
        _showTimer.Tick += (_, _) =>
        {
            _showTimer.Stop();
            if (!_disposed)
                ShowOverOwner();
        };
        _showTimer.Start();

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) =>
        {
            _elapsedLabel.Text = UiText.Elapsed(_clock.Elapsed);

            // If the owner died while we were busy (form closed underneath the
            // operation), get out of the way — Guard observes the close as a
            // cancel request and abandons the operation.
            if (_owner.IsDisposed || _owner.Disposing)
                Close();
        };
        _timer.Start();

        RaiseBusyChanged();
    }

    /// <summary>Shows the busy dialog over <paramref name="owner"/> (whose
    /// input is blocked until <see cref="Dispose"/>). The dialog becomes
    /// VISIBLE only after <see cref="ShowDelay"/> — see the class comment.
    /// </summary>
    public static BusyDialog Show(Form owner, string busyText) => new(owner, busyText);

    /// <summary>True while <paramref name="owner"/> has a live busy dialog
    /// (possibly still in its hidden pre-delay window). Host shells use this
    /// alongside <c>Form.Enabled</c> to gate screen switching: MDI-child
    /// owners stay form-ENABLED while busy, so Enabled alone no longer
    /// detects them.</summary>
    internal static bool IsOwnerBusy(Form? owner) =>
        owner is not null && OwnerBusyStates.TryGetValue(owner, out var state) && state.Depth > 0;

    /// <summary>Raised on the UI thread whenever any owner's busy state
    /// begins or ends. Hosts (the Packager shell) refresh their busy gating
    /// from this — an MDI child under Guard keeps its form enabled, so no
    /// EnabledChanged event fires for it.</summary>
    internal static event Action? OwnerBusyStateChanged;

    internal static void RaiseBusyChanged() => OwnerBusyStateChanged?.Invoke();

    /// <summary>The token handed to the guarded work. Cancelled the moment
    /// the user asks to cancel (or the dialog closes for any reason).</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Completes when the user pressed Cancel / Esc or the dialog
    /// closed for any other reason — Guard races this against the work.</summary>
    public Task CancelRequested => _cancelRequested.Task;

    private void RequestCancel()
    {
        if (_cancelRequested.Task.IsCompleted)
            return;

        _statusLabel.Text = "Cancelling… (finishing or detaching the operation)";
        _cancelButton.Enabled = false;
        _cts.Cancel();
        _cancelRequested.TrySetResult();
    }

    /// <summary>Makes the dialog visible (first show only) and centers it
    /// over the owner's ON-SCREEN rectangle. <c>Control.RectangleToScreen</c>
    /// walks the whole ancestor chain, so this is correct for MDI-child
    /// owners too — an MDI child's own Left/Top would be MDI-client
    /// coordinates and produced the off-center dialog reported on the
    /// connection screen.</summary>
    private void ShowOverOwner()
    {
        if (_shown || _disposed || _owner.IsDisposed || _owner.Disposing)
            return; // already visible, disposed, or owner died inside the delay window

        _shown = true;
        _clock.Restart(); // elapsed time counts from when the dialog appears
        Show(_owner);
        CenterOverOwner();
    }

    private void CenterOverOwner()
    {
        var rect = _owner.RectangleToScreen(_owner.ClientRectangle);
        if (rect.Width <= 0 || rect.Height <= 0)
            rect = Screen.FromControl(_owner).WorkingArea; // degenerate/minimized fallback

        Location = new Point(
            rect.Left + Math.Max(0, (rect.Width - Width) / 2),
            rect.Top + Math.Max(0, (rect.Height - Height) / 2));
    }

    /// <summary>Blocks the owner's input for the duration of the guard. MDI
    /// children get their CONTENT disabled and keep the form (and its MDI
    /// activation) enabled — see <see cref="OwnerBusyState.ContentStates"/>.
    /// </summary>
    private static void DisableOwnerInteraction(Form owner, OwnerBusyState state)
    {
        if (owner.IsMdiChild)
        {
            state.ContentStates = new List<(Control, bool)>();
            foreach (Control content in owner.Controls)
            {
                state.ContentStates.Add((content, content.Enabled));
                content.Enabled = false;
            }
        }
        else
        {
            state.FormEnabled = owner.Enabled;
            owner.Enabled = false;
        }
    }

    /// <summary>Undoes <see cref="DisableOwnerInteraction"/> (outermost dialog
    /// only — the depth counter is maintained by the callers).</summary>
    private static void RestoreOwnerInteraction(Form owner, OwnerBusyState state)
    {
        if (state.ContentStates is { } content)
        {
            foreach (var (control, enabled) in content)
            {
                if (!control.IsDisposed)
                    control.Enabled = enabled;
            }
            state.ContentStates = null;
        }
        else
        {
            owner.Enabled = state.FormEnabled;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        RequestCancel(); // any close path (owner died, Alt+F4 edge cases) is a cancel
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape && _cancelButton.Enabled)
        {
            RequestCancel();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _showTimer.Stop();
            _showTimer.Dispose();
            _timer.Stop();
            _timer.Dispose();

            // Safety net: whatever takes this dialog down (dispose, a never-
            // shown dialog whose owner died — Close() on those skips
            // FormClosing), release the guarded work too: Guard's
            // CancelRequested race completes, and an operation that observes
            // the token stops instead of running detached against a dead
            // owner.
            _cancelRequested.TrySetResult();
            _cts.Cancel();
            _cts.Dispose();

            if (!_owner.IsDisposed)
            {
                var state = OwnerBusyStates.GetOrCreateValue(_owner);
                state.Depth = Math.Max(0, state.Depth - 1);
                if (state.Depth == 0)
                {
                    // Only the outermost busy dialog restores the owner —
                    // see OwnerBusyStates above.
                    RestoreOwnerInteraction(_owner, state);
                    _owner.Cursor = state.Cursor;
                }
            }

            RaiseBusyChanged();
        }
        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW — no Alt-Tab entry
            return cp;
        }
    }
}
