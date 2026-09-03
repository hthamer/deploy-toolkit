using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;

namespace DeployToolkit.Core.Database;

/// <summary>
/// Q8: full database script backup (schema + data + triggers + stored
/// procedures + indexes + constraints + foreign keys + views + UDFs) via
/// Microsoft.SqlServer.Management.Smo (SMO) — the same engine SSMS uses for
/// "Generate Scripts". Full fidelity across SQL Server 2016 through 2022+.
/// Works on AWS RDS and other managed SQL services where
/// BACKUP DATABASE TO DISK is not supported.
/// </summary>
public static class SmoDatabaseScriptBackup
{
    /// <summary>
    /// Writes a full restore script for the database named in the connection
    /// string as <c>{dbName}-backup.sql</c> inside <paramref name="backupFolder"/>,
    /// and returns the script path.
    /// </summary>
    public static string WriteScriptBackup(string connectionString, string backupFolder)
    {
        var dbName = ExtractDatabaseName(connectionString) ?? "database";
        var scriptPath = Path.Combine(backupFolder, $"{dbName}-backup.sql");
        GenerateDatabaseScript(connectionString, dbName, scriptPath);
        return scriptPath;
    }

    /// <summary>Generates a full database script via SMO. Writes schema + data
    /// + triggers + SPs + indexes + constraints + FKs + views + UDFs directly
    /// to <paramref name="outputFilePath"/>.</summary>
    private static void GenerateDatabaseScript(string connectionString, string dbName, string outputFilePath)
    {
        // Let SMO own the connection. Wrapping an externally-opened SqlConnection
        // loses the password (SqlConnection doesn't expose it), and SMO opens
        // additional connections during dependency discovery / data scripting —
        // those fail with "Login failed for user '...'".
        // NOTE: new ServerConnection(str) is the SERVER-NAME overload — there is
        // no connection-string constructor; the string must go to the
        // ConnectionString property so SMO parses it and keeps the credentials.
        var serverConnection = new ServerConnection
        {
            ConnectionString = connectionString,
        };
        var server = new Server(serverConnection);
        var database = server.Databases[dbName];

        if (database is null)
            throw new InvalidOperationException($"Database '{dbName}' not found on the server.");

        // Prefetch all object metadata in batched round trips; without this every
        // Tables/Views/SPs/UDFs iteration issues one query per object and crawls.
        database.PrefetchObjects();

        var scripter = new Scripter(server);
        // NOTE: ScriptData=true is only supported by EnumScriptWithList —
        // Scripter.Script()/ScriptWithList() throw "This method does not
        // support scripting data". FileName/ToFileOnly are therefore not set;
        // the yielded statements are written to the file manually.
        scripter.Options = new ScriptingOptions
        {
            ScriptSchema = true,
            ScriptData = true,
            ScriptDrops = false,
            WithDependencies = true,
            Indexes = true,
            DriAllConstraints = true,
            Triggers = true,
            NoCollation = true,
            EnforceScriptingOptions = true,
            // Batches are terminated manually after each yielded string below —
            // ScriptBatchTerminator is unreliable through the EnumScript* path.
            ScriptBatchTerminator = false,
            IncludeDatabaseContext = false,
        };

        var urns = new List<Urn>();
        foreach (var table in database.Tables)
            if (!table.IsSystemObject) urns.Add(table.Urn);
        foreach (var view in database.Views)
            if (!view.IsSystemObject) urns.Add(view.Urn);
        foreach (var sp in database.StoredProcedures)
            if (!sp.IsSystemObject) urns.Add(sp.Urn);
        foreach (var udf in database.UserDefinedFunctions)
            if (!udf.IsSystemObject) urns.Add(udf.Urn);

        using var writer = new StreamWriter(outputFilePath, append: false, System.Text.Encoding.UTF8);
        foreach (var batch in scripter.EnumScriptWithList(urns.ToArray()))
        {
            writer.WriteLine(batch);
            writer.WriteLine("GO");
        }
    }

    /// <summary>Extracts the Initial Catalog / Database value from a SQL
    /// connection string. Returns null when not found (the caller falls back
    /// to "database").</summary>
    public static string? ExtractDatabaseName(string connectionString)
    {
        foreach (var part in connectionString.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = part.IndexOf('=');
            if (sep <= 0) continue;
            var key = part[..sep].Trim();
            var value = part[(sep + 1)..].Trim();
            if (key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Database", StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }
}
