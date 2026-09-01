namespace DeployToolkit.AppKit;

/// <summary>
/// Makes async work safe to trigger from UI event handlers: disables the
/// owner window, shows a cancellable <see cref="BusyDialog"/>, runs the
/// work, and catches EVERY exception — surfacing it as an
/// <see cref="AppTheme.Error"/> dialog — so nothing ever escapes into the
/// WinForms message loop (an unhandled exception there kills the app).
///
/// Cancellation (user feedback after a force-close incident: the old busy
/// overlay had no escape hatch, so a hung git fetch bricked the app):
/// the work receives a <see cref="CancellationToken"/>; when the user hits
/// Cancel the token fires and the work gets a short grace period to wind
/// down. Work that cannot observe the token in time (a libgit2 fetch, a
/// stuck socket) is <b>abandoned</b>: the busy dialog closes, the owner is
/// re-enabled, and the operation's eventual completion/exception is
/// silently ignored. There is always a way out.
///
/// Deliberate cancellations (<see cref="OperationCanceledException"/>) are
/// swallowed silently; they are not errors.
///
/// Handlers that need custom error handling (e.g. the Clients screen save
/// flow distinguishes validation errors from infrastructure errors) catch
/// those specific exceptions inside <paramref name="work"/> and let only the
/// unexpected ones reach this guard.
///
/// IMPORTANT: the synchronous part of <paramref name="work"/> runs on the UI
/// thread (before its first await). Wrap any heavy synchronous IO in
/// <c>Task.Run</c> — hashing a folder or walking a tree on the UI thread
/// freezes the message pump (and this dialog with it).
///
/// The busy dialog appears only after a short delay (see
/// <see cref="BusyDialog.ShowDelay"/>): near-instant registry reads never
/// flash a popup. The owner's input is blocked from the start; MDI-child
/// owners keep their FORM enabled while busy (only the content freezes) so
/// the MDI frame never moves activation to another screen — hosts must
/// therefore gate switching with <see cref="IsBusy"/> in addition to
/// <c>Form.Enabled</c>, and can subscribe to <see cref="BusyStateChanged"/>
/// to refresh that gating when a guard starts/ends.
/// </summary>
public static class Guard
{
    /// <summary>Grace period between the user's cancel request and abandoning
    /// the operation. Long enough for responsive code (registry IO, file IO)
    /// to observe the token; short enough that cancel never feels broken.</summary>
    internal static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="work"/> with the owner disabled and a busy dialog
    /// shown. Never throws: any exception is reported via
    /// <c>AppTheme.Error</c> and swallowed.
    /// </summary>
    /// <param name="owner">Owner window to disable while busy (null = run
    /// unguarded UI-less — still exception-safe).</param>
    /// <param name="busyText">Short progress text shown in the dialog.</param>
    /// <param name="work">The async operation. Runs on the UI thread; its
    /// awaits resume on the UI thread, so UI access inside is safe.</param>
    public static Task RunAsync(Form? owner, string busyText, Func<Task> work)
        => RunAsync(owner, busyText, _ => work());

    /// <summary>Token-aware overload: the work observes the guard's
    /// cancellation token (fired by the busy dialog's Cancel button / Esc).
    /// Unobservable work is still safe — it is abandoned after the grace
    /// period and detached.</summary>
    public static async Task RunAsync(Form? owner, string busyText, Func<CancellationToken, Task> work)
    {
        if (owner is null || owner.IsDisposed)
        {
            try
            {
                await work(CancellationToken.None).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppTheme.Error(null, ex, "Operation failed");
            }
            return;
        }

        var dialog = BusyDialog.Show(owner, busyText);
        try
        {
            Task workTask;
            try
            {
                workTask = work(dialog.Token);
            }
            catch (OperationCanceledException)
            {
                return; // deliberate — silent
            }
            catch (Exception ex)
            {
                dialog.Dispose(); // dialog down before the error dialog
                AppTheme.Error(owner, ex, "Operation failed");
                return;
            }

            var completed = await Task.WhenAny(workTask, dialog.CancelRequested).ConfigureAwait(true);
            if (completed != workTask && !workTask.IsCompleted)
            {
                // The user asked to cancel. Give the work a grace period to
                // observe the token and wind down cleanly.
                await Task.WhenAny(workTask, Task.Delay(CancelGracePeriod)).ConfigureAwait(true);
            }

            if (!workTask.IsCompleted)
            {
                // Abandoned: the operation keeps running but the UI is freed.
                // Observe its eventual exception so it never reaches the
                // task-scheduler unobserved-exception handler.
                _ = workTask.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return;
            }

            await workTask.ConfigureAwait(true); // propagate result / exception
        }
        catch (OperationCanceledException)
        {
            // deliberate cancellation — not an error
        }
        catch (Exception ex)
        {
            dialog.Dispose(); // take the dialog down before the error dialog
            AppTheme.Error(owner.IsDisposed ? null : owner, ex, "Operation failed");
        }
        finally
        {
            dialog.Dispose(); // idempotent — restores the owner
        }
    }

    /// <summary>Convenience one-liner for fire-and-forget button handlers —
    /// same guarantees as <see cref="RunAsync"/>, result discarded.</summary>
    public static void FireAndForget(Form? owner, string busyText, Func<Task> work)
        => _ = RunAsync(owner, busyText, work);

    /// <summary>Token-aware fire-and-forget — see <see cref="RunAsync(Form?,string,Func{CancellationToken,Task})"/>.</summary>
    public static void FireAndForget(Form? owner, string busyText, Func<CancellationToken, Task> work)
        => _ = RunAsync(owner, busyText, work);

    /// <summary>True while <paramref name="owner"/> has a live guarded
    /// operation (busy dialog, possibly still in its hidden pre-delay
    /// window). Host shells combine this with <c>Form.Enabled</c> when
    /// deciding whether a screen may be switched away: an MDI child under
    /// Guard stays form-enabled (its content is frozen instead), so
    /// Enabled alone no longer identifies a busy child.</summary>
    public static bool IsBusy(Form? owner) => BusyDialog.IsOwnerBusy(owner);

    /// <summary>Raised on the UI thread whenever a guard starts or ends on
    /// any owner (including the hidden pre-delay window). Hosts refresh
    /// their busy gating here — see <see cref="IsBusy"/>.</summary>
    public static event Action? BusyStateChanged
    {
        add => BusyDialog.OwnerBusyStateChanged += value;
        remove => BusyDialog.OwnerBusyStateChanged -= value;
    }
}
