using Microsoft.Data.SqlClient;

namespace DeployToolkit.Core.Database;

/// <summary>
/// Thin SQL Server / Azure SQL wrapper around <see cref="SqlScriptRunner"/>
/// (plan §8.4). It only owns connection lifetime — all GO splitting, analysis,
/// batching, transaction handling and progress reporting come from the
/// provider-neutral runner.
/// </summary>
/// <remarks>
/// <b>Connection string notes (Azure SQL):</b>
/// <list type="bullet">
/// <item><description>Since Microsoft.Data.SqlClient 4.0 the default for
/// <c>Encrypt</c> is <c>Mandatory</c>. That is what you want for production
/// Azure SQL, but a plain local/dev server with a self-signed certificate
/// will fail with a certificate trust error. For TEST servers append
/// <c>TrustServerCertificate=True</c> (or explicitly <c>Encrypt=False</c>);
/// production connections should keep validation on.</description></item>
/// <item><description>For Azure SQL Database the login user must exist in the
/// target database (not just the server), and DDL-heavy scripts may require
/// elevated permissions — the analyzer warnings are the review hook.</description></item>
/// </list>
/// <b>GO semantics:</b> GO is a client-side batch separator convention of
/// SSMS/sqlcmd — the server has no such keyword. This library implements the
/// convention itself (<see cref="GoBatchSplitter"/>); the word GO never
/// reaches the server. Per plan §1 policy, sqlcmd.exe is never spawned and no
/// scripts are executed on target machines — everything here is in-process
/// compiled code.
/// </remarks>
public static class SqlServerScriptRunner
{
    /// <summary>
    /// Opens a <see cref="SqlConnection"/> from a connection string, runs the
    /// script via <see cref="SqlScriptRunner.ExecuteAsync(DbConnection, string, string, SqlScriptRunnerOptions?, IProgress{SqlBatchResult}?, CancellationToken)"/>,
    /// then closes the connection.
    /// </summary>
    public static async Task<SqlScriptRunReport> ExecuteAsync(
        string connectionString,
        string script,
        string scriptName,
        SqlScriptRunnerOptions? options = null,
        IProgress<SqlBatchResult>? progress = null,
        CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return await SqlScriptRunner.ExecuteAsync(connection, script, scriptName, options, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the script on an <b>already open</b> <see cref="SqlConnection"/>
    /// the caller owns (e.g. reused across several scripts of one deployment);
    /// the connection is left open.
    /// </summary>
    public static Task<SqlScriptRunReport> ExecuteAsync(
        SqlConnection connection,
        string script,
        string scriptName,
        SqlScriptRunnerOptions? options = null,
        IProgress<SqlBatchResult>? progress = null,
        CancellationToken ct = default)
    {
        return SqlScriptRunner.ExecuteAsync(connection, script, scriptName, options, progress, ct);
    }
}
