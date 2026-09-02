using System.Data.Common;
using DeployToolkit.Core.Database;
using Microsoft.Data.Sqlite;

// ---------------------------------------------------------------------------
// Self-test for DeployToolkit.Core.Database (Phase 8 — DB script runner).
// Pattern follows tools/DeployToolkit.Core.SelfTest: Check(name, bool) with
// [pass]/[FAIL] lines, a failure summary and a non-zero exit code on failure.
//
// NOTE: the SqlServerScriptRunner connection-string path cannot be exercised
// in this sandbox — there is no SQL Server / Azure SQL instance to connect
// to. That path is compile-verified only (see the final note in the output).
// Everything else runs for real against Microsoft.Data.Sqlite through the
// provider-neutral System.Data.Common surface the runner uses.
// ---------------------------------------------------------------------------

var failures = new List<string>();
var passed = 0;

void Check(string name, bool condition)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  [pass] {name}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"  [FAIL] {name}");
    }
}

var workRoot = Path.Combine(Path.GetTempPath(), "DeployToolkitDbSelfTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workRoot);

try
{
    // ---------------------------------------------------------------
    Console.WriteLine("== GoBatchSplitter ==");
    var s1 = GoBatchSplitter.Split("SELECT 1;\nGO\nSELECT 2;");
    Check("simple 2-batch split", s1.Count == 2 && s1[0] == "SELECT 1;" && s1[1] == "SELECT 2;");

    var s2 = GoBatchSplitter.Split("CREATE TABLE a(x int)\ngo\nINSERT INTO a VALUES (1)");
    Check("lowercase 'go' is recognized", s2.Count == 2);

    var s3 = GoBatchSplitter.Split("INSERT INTO t VALUES (1)\nGO 3");
    Check("GO 3 expands preceding batch to 3 executions",
        s3.Count == 3 && s3.All(b => b == "INSERT INTO t VALUES (1)"));

    var s4 = GoBatchSplitter.Split("SELECT 'line1\nGO\nline2'");
    Check("GO inside multi-line single-quoted string NOT split", s4.Count == 1);

    var s5 = GoBatchSplitter.Split("SELECT 'it''s ok'\nGO\nSELECT 2");
    Check("GO recognized after ''-escaped quote inside string", s5.Count == 2);

    var s6 = GoBatchSplitter.Split("SELECT 1 -- GO\nSELECT 2");
    Check("GO inside -- line comment NOT split", s6.Count == 1);

    var s7 = GoBatchSplitter.Split("/* header note\nGO\nstill commented */\nSELECT 1");
    Check("GO inside multi-line /* */ block comment NOT split", s7.Count == 1);

    var s8 = GoBatchSplitter.Split("SELECT [GO] FROM [tbl]\nGO\nSELECT 2");
    Check("[GO] bracketed identifier NOT split", s8.Count == 2 && s8[0].Contains("[GO]"));

    var s9 = GoBatchSplitter.Split("SELECT \"my \"\"GO\"\" col\"\nGO\nSELECT 2");
    Check("GO inside double-quoted identifier NOT split", s9.Count == 2);

    var s10 = GoBatchSplitter.Split("SELECT 1\nGO\nGO\nGO\nSELECT 2");
    Check("consecutive GOs produce no empty batches", s10.Count == 2);

    var s11 = GoBatchSplitter.Split("\n\n   SELECT 1  \n\nGO\n\n  SELECT 2 \n\n");
    Check("batch text trimmed of surrounding blank lines",
        s11.Count == 2 && s11[0] == "SELECT 1" && s11[1] == "SELECT 2");

    Check("empty input yields 0 batches", GoBatchSplitter.Split("").Count == 0);
    Check("GO-only input yields 0 batches", GoBatchSplitter.Split("GO\nGO").Count == 0);

    var s14 = GoBatchSplitter.Split("SELECT 1 GO");
    Check("inline GO (not whole-line token) NOT a separator", s14.Count == 1);

    var s15 = GoBatchSplitter.Split("SELECT 1\n  GO -- run it\nSELECT 2");
    Check("indented GO with trailing comment IS a separator", s15.Count == 2);

    // ---------------------------------------------------------------
    Console.WriteLine("== SqlScriptAnalyzer ==");
    var a1 = SqlScriptAnalyzer.Analyze("CREATE DATABASE MyDb;");
    Check("CREATE DATABASE -> cannot run in transaction",
        !a1.CanRunInTransaction && a1.Warnings.Any(w => w.Contains("CREATE DATABASE")));

    var a2 = SqlScriptAnalyzer.Analyze("ALTER DATABASE MyDb SET AUTO_CLOSE OFF;");
    Check("ALTER DATABASE -> cannot run in transaction", !a2.CanRunInTransaction);

    var a3 = SqlScriptAnalyzer.Analyze("DROP DATABASE OldDb;");
    Check("DROP DATABASE -> cannot run in transaction", !a3.CanRunInTransaction);

    var a4 = SqlScriptAnalyzer.Analyze("BACKUP DATABASE db TO DISK = 'x.bak';");
    Check("BACKUP -> cannot run in transaction", !a4.CanRunInTransaction);

    var a5 = SqlScriptAnalyzer.Analyze("RESTORE DATABASE db FROM DISK = 'x.bak';");
    Check("RESTORE -> cannot run in transaction", !a5.CanRunInTransaction);

    var a6 = SqlScriptAnalyzer.Analyze("CREATE FULLTEXT CATALOG ft;");
    Check("CREATE FULLTEXT CATALOG -> cannot run in transaction", !a6.CanRunInTransaction);

    var a7 = SqlScriptAnalyzer.Analyze("CREATE AVAILABILITY GROUP ag1;");
    Check("CREATE AVAILABILITY GROUP -> cannot run in transaction", !a7.CanRunInTransaction);

    var a8 = SqlScriptAnalyzer.Analyze("CREATE ENDPOINT ep1 STATE = STARTED;");
    Check("CREATE ENDPOINT -> cannot run in transaction", !a8.CanRunInTransaction);

    var a9 = SqlScriptAnalyzer.Analyze("CREATE PROCEDURE dbo.P AS BEGIN SELECT 1; END");
    Check("CREATE PROCEDURE -> can run in transaction", a9.CanRunInTransaction);
    Check("CREATE PROCEDURE -> advisory schema-change warning present",
        a9.Warnings.Any(w => w.Contains("schema-changing statement present")));

    var a10 = SqlScriptAnalyzer.Analyze("INSERT INTO t VALUES (1); UPDATE t SET x = 2;");
    Check("plain INSERT/UPDATE -> transaction-safe, no warnings",
        a10.CanRunInTransaction && a10.Warnings.Count == 0);

    var a11 = SqlScriptAnalyzer.Analyze("-- CREATE DATABASE inside a comment\n/* BACKUP here too */");
    Check("comment-only text produces no false positives",
        a11.CanRunInTransaction && a11.Warnings.Count == 0);

    var a12 = SqlScriptAnalyzer.Analyze("SELECT 'CREATE DATABASE in a string' AS x");
    Check("CREATE DATABASE inside a string literal produces no false positive",
        a12.CanRunInTransaction && a12.Warnings.Count == 0);

    var a13 = SqlScriptAnalyzer.Analyze("CREATE TABLE dbo.T (Id int)");
    Check("CREATE TABLE -> transaction-safe but flagged for review",
        a13.CanRunInTransaction && a13.Warnings.Count == 1);

    // ---------------------------------------------------------------
    Console.WriteLine("== SqlScriptRunner vs SQLite (in-memory) ==");
    await using var sqlite = new SqliteConnection("Data Source=:memory:");
    sqlite.Open();

    async Task<int> CountAsync(string sql)
    {
        await using var cmd = sqlite.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    async Task<bool> TableExistsAsync(string name) =>
        await CountAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{name}'") > 0;

    // Success path: CREATE TABLE + 2 INSERTs across 3 batches.
    var successScript = """
        CREATE TABLE RunnerT (Id INTEGER PRIMARY KEY, Name TEXT);
        GO
        INSERT INTO RunnerT (Name) VALUES ('alpha');
        GO
        INSERT INTO RunnerT (Name) VALUES ('beta');
        """;

    var progress = new CollectingProgress();
    var report = await SqlScriptRunner.ExecuteAsync(sqlite, successScript, "runner-success.sql", progress: progress);
    Check("multi-batch script reports success", report.Success);
    Check("report echoes script name", report.ScriptName == "runner-success.sql");
    Check("3 batches executed", report.Batches.Count == 3);
    Check("insert batches report rows affected",
        report.Batches[1].RowsAffected == 1 && report.Batches[2].RowsAffected == 1);
    Check("progress received all completed batches", progress.Items.Count == 3);
    Check("batch durations non-negative", report.Batches.All(b => b.Duration >= TimeSpan.Zero));
    Check("total duration positive", report.TotalDuration > TimeSpan.Zero);
    Check("no rollback / no error on success", !report.RolledBack && report.FirstError is null);
    Check("rows persisted (verified via direct SELECT COUNT)",
        await CountAsync("SELECT COUNT(*) FROM RunnerT") == 2);

    // Transaction rollback: batch 2 references a missing table.
    var rollbackScript = """
        CREATE TABLE RollT (Id INTEGER);
        GO
        INSERT INTO MissingTable_XYZ VALUES (1);
        """;

    var reportRoll = await SqlScriptRunner.ExecuteAsync(sqlite, rollbackScript, "runner-rollback.sql");
    Check("failing batch marks report failed", !reportRoll.Success);
    Check("failure inside transaction sets RolledBack=true", reportRoll.RolledBack);
    Check("first error recorded with failing table name",
        reportRoll.FirstError is not null && reportRoll.FirstError.Contains("MissingTable_XYZ"));
    Check("table created by batch 1 no longer exists after rollback",
        !await TableExistsAsync("RollT"));
    Check("both attempted batches (incl. failed) are in the report",
        reportRoll.Batches.Count == 2 && !reportRoll.Batches[1].Success);

    // No transaction: partial effects remain.
    var partialScript = """
        CREATE TABLE PartialT (Id INTEGER);
        GO
        INSERT INTO PartialT VALUES (1);
        GO
        INSERT INTO MissingTable_YZW VALUES (1);
        """;

    var reportPartial = await SqlScriptRunner.ExecuteAsync(
        sqlite, partialScript, "runner-partial.sql", new SqlScriptRunnerOptions(WrapInTransaction: false));
    Check("without transaction: partial effects remain, RolledBack=false",
        !reportPartial.RolledBack && reportPartial.Success == false);
    Check("without transaction: table from batch 1 persists", await TableExistsAsync("PartialT"));
    Check("without transaction: earlier insert persists",
        await CountAsync("SELECT COUNT(*) FROM PartialT") == 1);
    Check("without transaction: all 3 batches attempted", reportPartial.Batches.Count == 3);

    // ContinueOnError keeps going past a failing batch (no transaction).
    var coScript = """
        INSERT INTO PartialT VALUES (10);
        GO
        INSERT INTO MissingTable_ABC VALUES (1);
        GO
        INSERT INTO PartialT VALUES (20);
        """;

    var reportCo = await SqlScriptRunner.ExecuteAsync(
        sqlite, coScript, "runner-continue.sql",
        new SqlScriptRunnerOptions(WrapInTransaction: false, ContinueOnError: true));
    Check("ContinueOnError runs all 3 batches", reportCo.Batches.Count == 3);
    Check("ContinueOnError records per-batch success/failure",
        reportCo.Batches[0].Success && !reportCo.Batches[1].Success && reportCo.Batches[2].Success);
    Check("ContinueOnError still reports overall failure", !reportCo.Success);
    Check("batches after the failure actually executed",
        await CountAsync("SELECT COUNT(*) FROM PartialT") == 3);

    // Default (no ContinueOnError, no transaction) stops at the failure.
    var stopScript = """
        CREATE TABLE StopT (Id INTEGER);
        GO
        INSERT INTO MissingTable_DEF VALUES (1);
        GO
        INSERT INTO StopT VALUES (1);
        """;

    var reportStop = await SqlScriptRunner.ExecuteAsync(
        sqlite, stopScript, "runner-stop.sql", new SqlScriptRunnerOptions(WrapInTransaction: false));
    Check("default stops at failing batch (later batch not executed)",
        reportStop.Batches.Count == 2);

    // Analyzer interplay: a schema-DDL warning does NOT block execution.
    var schemaScript = """
        CREATE TABLE WarnT (Id INTEGER);
        GO
        INSERT INTO WarnT VALUES (1);
        """;

    var reportWarn = await SqlScriptRunner.ExecuteAsync(sqlite, schemaScript, "runner-schema-warning.sql");
    Check("script with schema-change warning still executes in a transaction",
        reportWarn.Success && !reportWarn.RolledBack);
    Check("schema-warning script data committed",
        await CountAsync("SELECT COUNT(*) FROM WarnT") == 1);

    // The transaction-blocking flag itself is verified on the analyzer (a real
    // CREATE DATABASE cannot run on SQLite); here we confirm a WrapInTransaction
    // request flows through the runner cleanly for a transaction-safe script.
    var blocked = SqlScriptAnalyzer.Analyze("CREATE DATABASE X;");
    var reportNoTxFlag = await SqlScriptRunner.ExecuteAsync(
        sqlite, "CREATE TABLE NoTxT (Id INTEGER); INSERT INTO NoTxT VALUES (1);", "runner-notx.sql");
    Check("transaction-blocking analyzer flag computed; safe script still runs",
        !blocked.CanRunInTransaction && reportNoTxFlag.Success);

    // GO 3 end-to-end against a file-backed database (exercises temp cleanup).
    var fileDbPath = Path.Combine(workRoot, "go3.db");
    await using (var fileDb = new SqliteConnection($"Data Source={fileDbPath};Pooling=False"))
    {
        await fileDb.OpenAsync();
        var reportGo3 = await SqlScriptRunner.ExecuteAsync(
            fileDb,
            "CREATE TABLE Go3T (Id INTEGER);\nGO\nINSERT INTO Go3T VALUES (42)\nGO 3",
            "runner-go3.sql");
        await using var cmd = fileDb.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Go3T";
        var rows = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Check("GO 3 expansion executes 3 inserts end-to-end (file db)",
            reportGo3.Success && reportGo3.Batches.Count == 4 && rows == 3);
    }

    // Empty script: nothing to do, nothing to wrap.
    var reportEmpty = await SqlScriptRunner.ExecuteAsync(sqlite, "", "empty.sql");
    Check("empty script succeeds with 0 batches",
        reportEmpty.Success && reportEmpty.Batches.Count == 0);

    // Cancellation: pre-cancelled token -> failed report, no partial effects,
    // no exception thrown.
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var reportCancel = await SqlScriptRunner.ExecuteAsync(
        sqlite,
        "CREATE TABLE CancelT (Id INTEGER);\nGO\nINSERT INTO CancelT VALUES (1);",
        "runner-cancel.sql",
        cancellationToken: cts.Token);
    Check("pre-cancelled token returns failed report (no throw)",
        !reportCancel.Success && reportCancel.FirstError is not null);
    Check("cancelled run leaves no partial effects", !await TableExistsAsync("CancelT"));

    // ---------------------------------------------------------------
    Console.WriteLine("== MigrationScriptGenerator.DiscoverMigrations (EF migration file detection) ==");
    var efRoot = Path.Combine(workRoot, "ef-project");
    var migrationsDir = Path.Combine(efRoot, "Migrations");
    Directory.CreateDirectory(migrationsDir);

    // EF Core migrations are FILES in the Migrations folder (not subdirectories):
    //   <timestamp>_<Name>.cs
    //   <timestamp>_<Name>.Designer.cs   (generated, not a migration)
    //   <DbContext>ModelSnapshot.cs      (generated, not a migration)
    // Three real migrations + the generated designer + snapshot files.
    File.WriteAllText(Path.Combine(migrationsDir, "20260101120000_InitialCreate.cs"), "// mig");
    File.WriteAllText(Path.Combine(migrationsDir, "20260101120000_InitialCreate.Designer.cs"), "// designer");
    File.WriteAllText(Path.Combine(migrationsDir, "20260201120000_AddUsers.cs"), "// mig");
    File.WriteAllText(Path.Combine(migrationsDir, "20260201120000_AddUsers.Designer.cs"), "// designer");
    File.WriteAllText(Path.Combine(migrationsDir, "20260301120000_AddIndexes.cs"), "// mig");
    File.WriteAllText(Path.Combine(migrationsDir, "20260301120000_AddIndexes.Designer.cs"), "// designer");
    File.WriteAllText(Path.Combine(migrationsDir, "AppDbContextModelSnapshot.cs"), "// snapshot");

    // A non-migration .cs file (no timestamp_<name> pattern) — should be excluded.
    File.WriteAllText(Path.Combine(migrationsDir, "RandomFile.cs"), "// not a migration");
    // A stray subdirectory (should be ignored — we scan files, not dirs).
    Directory.CreateDirectory(Path.Combine(migrationsDir, "20260401120000_StrayDir"));

    var discovered = MigrationScriptGenerator.DiscoverMigrations(efRoot);
    Check("discovers all 3 EF migrations (files, not dirs)", discovered.Count == 3);
    Check("newest-first ordering (AddIndexes first)",
        discovered[0].Name == "20260301120000_AddIndexes");
    Check("second is AddUsers", discovered[1].Name == "20260201120000_AddUsers");
    Check("oldest is InitialCreate", discovered[2].Name == "20260101120000_InitialCreate");
    Check("DisplayName strips the timestamp prefix", discovered[0].DisplayName == "AddIndexes");
    Check(".Designer.cs files excluded", !discovered.Any(m => m.Name.EndsWith(".Designer")));
    Check("ModelSnapshot.cs excluded", !discovered.Any(m => m.Name.Contains("ModelSnapshot")));
    Check("non-migration .cs file excluded", !discovered.Any(m => m.Name == "RandomFile"));
    Check("stray subdirectory ignored", !discovered.Any(m => m.Name == "20260401120000_StrayDir"));

    Check("missing project folder yields empty list",
        MigrationScriptGenerator.DiscoverMigrations(Path.Combine(workRoot, "missing")).Count == 0);
    Check("project without Migrations folder yields empty list",
        MigrationScriptGenerator.DiscoverMigrations(workRoot).Count == 0);

    // ---------------------------------------------------------------
    Console.WriteLine("== MigrationScriptGenerator.BuildArguments (dotnet ef script command) ==");
    var args1 = MigrationScriptGenerator.BuildArguments(
        @"C:\repo\DB Project", @"C:\out\script.sql", fromMigration: null, toMigration: null);
    Check("full-schema script (no from/to) — quotes spaced project path",
        args1 == "ef migrations script --project \"C:\\repo\\DB Project\" --output \"C:\\out\\script.sql\" --no-build");

    var args2 = MigrationScriptGenerator.BuildArguments(
        @"C:\repo\DB", @"C:\out\script.sql", fromMigration: "20260101120000_InitialCreate", toMigration: "20260301120000_AddIndexes");
    Check("delta script — from/to migrations precede --project",
        args2 == "ef migrations script 20260101120000_InitialCreate 20260301120000_AddIndexes --project C:\\repo\\DB --output C:\\out\\script.sql --no-build");

    var argEx1 = false;
    try { MigrationScriptGenerator.BuildArguments("", "out.sql", null, null); }
    catch (ArgumentException) { argEx1 = true; }
    Check("empty project folder throws ArgumentException", argEx1);
    var argEx2 = false;
    try { MigrationScriptGenerator.BuildArguments("proj", "", null, null); }
    catch (ArgumentException) { argEx2 = true; }
    Check("empty output file throws ArgumentException", argEx2);

    // Idempotent flag adds --idempotent so the script is safe to re-run on a DB
    // that already has some migrations applied (handles the "migrations in the
    // middle added later" case).
    var args3 = MigrationScriptGenerator.BuildArguments(
        @"C:\repo\DB", @"C:\out\script.sql", "20260101_Initial", "20260301_AddIndexes", idempotent: true);
    Check("idempotent flag adds --idempotent before --no-build",
        args3.Contains("--idempotent") && args3.EndsWith("--no-build"));

    Console.WriteLine();
    Console.WriteLine("Note: SqlServerScriptRunner connection-string path is compile-verified only");
    Console.WriteLine("      (no SQL Server / Azure SQL available in this sandbox). All runner");
    Console.WriteLine("      behavior above was exercised through System.Data.Common + SQLite.");
}
finally
{
    try { Directory.Delete(workRoot, recursive: true); } catch { /* best effort cleanup */ }
}

Console.WriteLine();
Console.WriteLine($"== {passed} passed, {failures.Count} failed ==");
if (failures.Count > 0)
{
    Console.WriteLine("Failures:");
    foreach (var f in failures) Console.WriteLine($"  - {f}");
    Environment.Exit(1);
}

/// <summary>Synchronous IProgress collector — deterministic for tests (no sync-context posting).</summary>
internal sealed class CollectingProgress : IProgress<SqlBatchResult>
{
    private readonly object _gate = new();
    private readonly List<SqlBatchResult> _items = [];

    public void Report(SqlBatchResult value)
    {
        lock (_gate)
        {
            _items.Add(value);
        }
    }

    public IReadOnlyList<SqlBatchResult> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }
}
