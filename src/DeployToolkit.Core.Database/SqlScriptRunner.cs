using System.Data.Common;
using System.Diagnostics;

namespace DeployToolkit.Core.Database;

/// <summary>
/// Outcome of one executed batch.
/// </summary>
/// <param name="BatchIndex">Zero-based position of the batch in the split script.</param>
/// <param name="BatchText">The batch text exactly as executed (GO separators removed).</param>
/// <param name="RowsAffected">
/// Value returned by <see cref="DbCommand.ExecuteNonQueryAsync(CancellationToken)"/>
/// (rows changed by INSERT/UPDATE/DELETE; providers return 0 or -1 for DDL).
/// </param>
/// <param name="Duration">Wall-clock time the batch took on the server.</param>
/// <param name="Success">False when the server rejected the batch.</param>
/// <param name="Error">Exception message on failure, null on success.</param>
public sealed record SqlBatchResult(
    int BatchIndex,
    string BatchText,
    int RowsAffected,
    TimeSpan Duration,
    bool Success,
    string? Error);

/// <summary>
/// Outcome of one whole script run. Never thrown for script-level problems —
/// failures are described here, so a deployer can log and roll back files
/// without catching exceptions.
/// </summary>
/// <param name="ScriptName">Echo of the caller-supplied name (log/display only).</param>
/// <param name="Batches">Per-batch results, in execution order, up to (and including) the first failure unless continuing.</param>
/// <param name="TotalDuration">Whole-script wall-clock duration.</param>
/// <param name="Success">True only when every executed batch succeeded and nothing was cancelled.</param>
/// <param name="RolledBack">True when an open transaction was rolled back due to failure/cancellation.</param>
/// <param name="FirstError">Message of the first failing batch (or cancellation/commit note), null on success.</param>
public sealed record SqlScriptRunReport(
    string ScriptName,
    IReadOnlyList<SqlBatchResult> Batches,
    TimeSpan TotalDuration,
    bool Success,
    bool RolledBack,
    string? FirstError);

/// <summary>
/// Tunables for <see cref="SqlScriptRunner.ExecuteAsync(DbConnection, string, string, SqlScriptRunnerOptions?, IProgress{SqlBatchResult}?, CancellationToken)"/>.
/// </summary>
/// <param name="WrapInTransaction">
/// Wrap all batches in one transaction when the analyzer says that is safe
/// (scripts containing e.g. CREATE DATABASE run without one regardless).
/// </param>
/// <param name="CommandTimeoutSeconds">Per-batch <see cref="DbCommand.CommandTimeout"/>.</param>
/// <param name="ContinueOnError">
/// Keep executing later batches after a failure. Only honored when NOT
/// running inside a transaction — inside a transaction the first failure
/// always rolls back and stops, because partial effects are precisely what
/// the transaction exists to prevent.
/// </param>
public sealed record SqlScriptRunnerOptions(
    bool WrapInTransaction = true,
    int CommandTimeoutSeconds = 60,
    bool ContinueOnError = false);

/// <summary>
/// Provider-neutral headless DB script runner (plan §8.4): splits a T-SQL
/// script on GO separators, analyzes transaction safety, then executes batch
/// by batch over plain <see cref="System.Data.Common"/> abstractions. Works
/// with any ADO.NET provider whose commands accept plain text batches —
/// Microsoft.Data.SqlClient (SQL Server / Azure SQL) and Microsoft.Data.Sqlite
/// (self-test / offline mode) are both exercised. No sqlcmd.exe, no shell.
/// </summary>
public static class SqlScriptRunner
{
    /// <summary>
    /// Runs <paramref name="script"/> against an <b>already open</b>
    /// <paramref name="connection"/> and returns a full report instead of
    /// throwing for script-level failures (SQL errors, cancellations, commit
    /// failures are all reported; connection-level exceptions like a closed
    /// or broken connection may still propagate).
    /// </summary>
    /// <remarks>
    /// Behavior:
    /// <list type="bullet">
    /// <item><description>Splits via <see cref="GoBatchSplitter.Split"/>; each
    /// batch runs as one <see cref="DbCommand"/> with
    /// <see cref="SqlScriptRunnerOptions.CommandTimeoutSeconds"/>.</description></item>
    /// <item><description>When <see cref="SqlScriptRunnerOptions.WrapInTransaction"/>
    /// is set AND <see cref="SqlScriptAnalyzer"/> says the script is
    /// transaction-safe, everything runs inside one
    /// <see cref="DbConnection.BeginTransactionAsync(CancellationToken)"/>
    /// transaction; the first failing batch rolls it back and stops. Only
    /// provider base-type APIs are used, so this works identically on
    /// SqlClient and Microsoft.Data.Sqlite.</description></item>
    /// <item><description>Reports progress via <paramref name="progress"/> after
    /// each completed batch (success or failure).</description></item>
    /// <item><description>Cancellation is checked between batches and returns
    /// the partial report (rolling back an open transaction best-effort); a
    /// cancellation fired mid-command is caught as
    /// <see cref="OperationCanceledException"/> and reported the same way
    /// rather than thrown.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<SqlScriptRunReport> ExecuteAsync(
        DbConnection connection,
        string script,
        string scriptName,
        SqlScriptRunnerOptions? options = null,
        IProgress<SqlBatchResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var opts = options ?? new SqlScriptRunnerOptions();
        var batches = GoBatchSplitter.Split(script);
        var analysis = SqlScriptAnalyzer.Analyze(script);
        var useTransaction = opts.WrapInTransaction && analysis.CanRunInTransaction && batches.Count > 0;

        var results = new List<SqlBatchResult>();
        string? firstError = null;
        var rolledBack = false;
        var total = Stopwatch.StartNew();

        DbTransaction? tx = null;
        try
        {
            if (useTransaction)
            {
                tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var i = 0; i < batches.Count; i++)
            {
                // Cancellation check between batches (per plan §8.4 flow):
                // stop before doing more work, keep what we have, roll back.
                if (cancellationToken.IsCancellationRequested)
                {
                    firstError ??= $"Cancelled before batch {i} of {batches.Count}.";
                    break;
                }

                var result = await ExecuteBatchAsync(
                    connection, tx, i, batches[i], opts.CommandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);

                results.Add(result);
                progress?.Report(result);

                if (!result.Success)
                {
                    firstError ??= result.Error;
                    if (tx is not null)
                    {
                        // Inside a transaction the first failure poisons the
                        // whole run: roll everything back and stop.
                        await RollbackBestEffortAsync(tx).ConfigureAwait(false);
                        rolledBack = true;
                        tx = null;
                        break;
                    }

                    if (!opts.ContinueOnError)
                    {
                        break;
                    }
                    // ContinueOnError without a transaction: keep going.
                }
            }

            if (tx is not null)
            {
                // Capture in a local: inside the catch below, the compiler
                // can't know tx is still non-null, but 'pending' always is.
                var pending = tx;
                try
                {
                    await pending.CommitAsync(cancellationToken).ConfigureAwait(false);
                    tx = null; // committed — nothing left to clean up
                }
                catch (Exception commitEx) when (commitEx is DbException or OperationCanceledException)
                {
                    firstError ??= $"Commit failed: {commitEx.Message}";
                    await RollbackBestEffortAsync(pending).ConfigureAwait(false);
                    rolledBack = true;
                    tx = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Mid-command cancellation: report, don't throw — the deployer
            // gets a truthful "how far did we get" report either way.
            firstError ??= "Execution was cancelled.";
            if (tx is not null)
            {
                await RollbackBestEffortAsync(tx).ConfigureAwait(false);
                rolledBack = true;
                tx = null;
            }
        }
        finally
        {
            // Safety net: if any unexpected path above exited with a live
            // transaction, clean it up so the connection isn't left with a
            // zombie pending transaction.
            if (tx is not null)
            {
                await RollbackBestEffortAsync(tx).ConfigureAwait(false);
                rolledBack = true;
            }
        }

        total.Stop();
        var success = firstError is null && results.All(r => r.Success);
        return new SqlScriptRunReport(scriptName, results, total.Elapsed, success, rolledBack, firstError);
    }

    /// <summary>Executes one batch, converting SQL failures into a result record.</summary>
    private static async Task<SqlBatchResult> ExecuteBatchAsync(
        DbConnection connection,
        DbTransaction? transaction,
        int batchIndex,
        string batchText,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batchText;
            command.CommandTimeout = timeoutSeconds;
            if (transaction is not null)
            {
                command.Transaction = transaction;
            }

            var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new SqlBatchResult(batchIndex, batchText, rows, Stopwatch.GetElapsedTime(started), true, null);
        }
        catch (DbException ex)
        {
            // Batch-level SQL failure — recorded, never thrown (provider-
            // neutral: SqlException, SqliteException, ... all derive from this).
            return new SqlBatchResult(batchIndex, batchText, 0, Stopwatch.GetElapsedTime(started), false, ex.Message);
        }
    }

    /// <summary>
    /// Best-effort rollback + dispose. net8's <see cref="DbTransaction.RollbackAsync"/>
    /// exists for all providers, but a broken connection (the usual reason we
    /// roll back) can make it throw — in which case the server rolls the
    /// transaction back on its own, so swallowing is correct here.
    /// </summary>
    private static async Task RollbackBestEffortAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: see remarks above.
        }

        try
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
