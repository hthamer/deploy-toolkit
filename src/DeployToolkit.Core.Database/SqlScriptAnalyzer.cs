using System.Text.RegularExpressions;

namespace DeployToolkit.Core.Database;

/// <summary>
/// Result of the static safety analysis of one T-SQL script.
/// </summary>
/// <param name="CanRunInTransaction">
/// False when the script contains a statement that SQL Server refuses to run
/// inside a user transaction (CREATE/ALTER/DROP DATABASE, BACKUP, RESTORE,
/// FULLTEXT/AVAILABILITY GROUP/ENDPOINT DDL, ...). The runner then executes
/// the script WITHOUT a wrapping transaction, batch by batch.
/// </param>
/// <param name="Warnings">
/// Human-readable advisory messages. Only the transaction-blocking statements
/// influence <see cref="CanRunInTransaction"/>; schema/proc DDL produces an
/// informational warning the deployer can show before a production run.
/// </param>
public sealed record SqlScriptAnalysis(bool CanRunInTransaction, IReadOnlyList<string> Warnings);

/// <summary>
/// Static (text-only) safety analysis of T-SQL scripts, used by
/// <see cref="SqlScriptRunner"/> to decide whether a script may be wrapped in
/// a transaction. Never executes SQL and never touches a connection.
/// </summary>
/// <remarks>
/// Heuristics, kept deliberately simple and documented (plan §8.4 —
/// "transaction-wrapped where safe; DDL-heavy flagged"):
/// <list type="bullet">
/// <item><description>Comments and string literals are stripped first via the
/// shared line scanner, so a <c>CREATE DATABASE</c> inside a comment or a
/// quoted message never triggers a flag.</description></item>
/// <item><description>Flags are matched case-insensitively with word
/// boundaries anywhere in the statement text — we do NOT attempt full
/// statement parsing. That means a false positive is possible (e.g. a stored
/// proc named <c>sp_BackupHistory</c> would... not match, but a table named
/// <c>Restore</c> in "SELECT * FROM Restore" would) — acceptable, because
/// warnings are advisory; only the hard transaction-blocker list changes
/// behavior, and over-blocking is the safe direction for a deploy tool.</description></item>
/// <item><description>Schema/proc DDL (CREATE/ALTER/DROP TABLE, PROCEDURE,
/// FUNCTION, VIEW, TRIGGER, INDEX, SCHEMA, TYPE, CONSTRAINT) IS transactional
/// in SQL Server, so it never blocks the transaction — it only raises an
/// informational warning for the production-review path.</description></item>
/// </list>
/// </remarks>
public static class SqlScriptAnalyzer
{
    /// <summary>
    /// Statements SQL Server cannot execute inside a user transaction —
    /// these force the runner to drop the wrapping transaction.
    /// </summary>
    private static readonly Regex TransactionBlockers = new(
        @"\b(?:
            (?:CREATE|ALTER|DROP)\s+DATABASE
          | BACKUP
          | RESTORE
          | (?:CREATE|ALTER|DROP)\s+FULLTEXT\s+(?:CATALOG|INDEX)
          | (?:CREATE|ALTER|DROP)\s+AVAILABILITY\s+GROUP
          | (?:CREATE|ALTER|DROP)\s+ENDPOINT
        )\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    /// <summary>
    /// Schema/proc DDL — transaction-safe in SQL Server, but worth a
    /// human review before a production run.
    /// </summary>
    private static readonly Regex SchemaDdl = new(
        @"\b(?:CREATE|ALTER|DROP)\s+(?:TABLE|PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER|INDEX|SCHEMA|TYPE|CONSTRAINT)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Analyzes one script (or one batch). The flags consider the whole text;
    /// callers that want per-batch decisions can call this per batch.
    /// </summary>
    public static SqlScriptAnalysis Analyze(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return new SqlScriptAnalysis(true, Array.Empty<string>());
        }

        // Strip comments / string contents / bracketed identifier contents so
        // only live statement text is keyword-matched. The line structure is
        // preserved, which keeps the regexes' \b boundaries meaningful.
        var statementText = TSqlLineScanner.Sanitize(script);

        var warnings = new List<string>();

        var blockers = TransactionBlockers.Matches(statementText);
        foreach (Match m in blockers)
        {
            // Normalize whitespace inside the matched phrase for the message,
            // e.g. "CREATE   DATABASE" -> "CREATE DATABASE".
            var phrase = Regex.Replace(m.Value, @"\s+", " ").ToUpperInvariant();
            warnings.Add(
                $"'{phrase}' cannot run inside a SQL Server transaction — " +
                "the script will be executed WITHOUT a wrapping transaction.");
        }

        if (SchemaDdl.IsMatch(statementText))
        {
            warnings.Add(
                "schema-changing statement present — review before production run.");
        }

        // Only the hard transaction-blocker list flips the flag; the advisory
        // schema-DDL warning does NOT stop the script from being wrapped.
        return new SqlScriptAnalysis(blockers.Count == 0, warnings);
    }
}
