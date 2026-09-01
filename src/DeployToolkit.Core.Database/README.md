# DeployToolkit.Core.Database

Headless DB script runner for the deployment tool (plan §8.4). Splits T-SQL
scripts on `GO` batch separators in-process and executes the batches over
plain ADO.NET — **no `sqlcmd.exe`, no shell, no scripts on target machines**
(plan §1 policy: everything is compiled in-process code).

## Packages

- `Microsoft.Data.SqlClient` — SQL Server / Azure SQL connectivity.
- The runner itself only uses `System.Data.Common`, so it also runs against
  `Microsoft.Data.Sqlite` (offline mode / self-tests).

## Usage

```csharp
// SQL Server / Azure SQL (connection string; see notes below)
var report = await SqlServerScriptRunner.ExecuteAsync(
    connectionString, scriptText, "01-schema.sql",
    new SqlScriptRunnerOptions(CommandTimeoutSeconds: 120),
    progress: new Progress<SqlBatchResult>(b => log.LogInformation(...)));

// Any provider, caller-owned connection (must already be open)
var report2 = await SqlScriptRunner.ExecuteAsync(openDbConnection, scriptText, "01-schema.sql");
```

`SqlScriptRunReport` carries per-batch results (rows affected, duration,
error), overall success, whether a rollback happened, and the first error —
script-level failures are reported, never thrown.

## GO semantics

`GO` is a **client-side** SSMS/sqlcmd convention — the server never sees it.
`GoBatchSplitter` implements it:

- `GO` is recognized only as a whole-line token (`^\s*GO(\s+(\d+))?\s*(--.*)?$`,
  case-insensitive) **and** only when not inside a string literal, `--` line
  comment, `/* */` block comment, or bracketed identifier like `[GO]`.
- `GO 3` repeats the preceding batch 3 times.
- Batches are trimmed; consecutive `GO`s produce no empty batches.

## Transaction policy

`SqlScriptAnalyzer` inspects the script (comments/strings stripped first):

- Statements that SQL Server cannot run inside a transaction — `CREATE/ALTER/
  DROP DATABASE`, `BACKUP`, `RESTORE`, FULLTEXT/AVAILABILITY GROUP/ENDPOINT
  DDL — set `CanRunInTransaction=false`; the runner then executes batch-by-
  batch **without** a wrapping transaction.
- Schema/proc DDL (`CREATE/ALTER/DROP TABLE|PROCEDURE|FUNCTION|VIEW|TRIGGER|
  INDEX|SCHEMA|TYPE|CONSTRAINT`) is transaction-safe in SQL Server; it only
  produces the advisory warning *"schema-changing statement present — review
  before production run"*.
- Heuristics are word-boundary based, not a SQL parser: warnings are advisory
  and may rarely over-flag; only the hard list changes execution behavior.

When wrapped: first failing batch → rollback + stop (`RolledBack=true`).
Unwrapped: failures stop the run unless `ContinueOnError=true`.

## Azure SQL connection strings

- Microsoft.Data.SqlClient 4.0+ defaults `Encrypt=Mandatory`. Production
  Azure SQL wants that; **test servers** with self-signed certificates need
  `TrustServerCertificate=True` (or `Encrypt=False`) appended or connections
  fail certificate validation.
- Azure SQL Database logins must exist in the target database, and DDL-heavy
  scripts need correspondingly elevated permissions.

## .NET version note

Kept on `net8.0` to match `DeployToolkit.Core`; retarget to `net10.0-windows`
alongside the rest of the solution on a dev machine — no package bump needed
(`Microsoft.Data.SqlClient` 7.x supports net10.0).
