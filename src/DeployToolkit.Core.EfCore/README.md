# DeployToolkit.Core.EfCore — Registry data access (plan Phase 1)

EF Core / SQL Server implementation of `IRegistryStore` — the Central
Registry Database from plan §2.2. This is the source of truth for clients,
components, package history, and deployment status.

## Why a separate project

`DeployToolkit.Core` intentionally has **zero NuGet dependencies** (see its
csproj note): the domain POCOs, `IRegistryStore` interface, and all core
engines build anywhere. Anything needing external packages lives in its own
project — same pattern the `IDeploymentExecutor` implementations will follow.
If you'd rather follow the plan §2 tree literally (Registry inside Core),
move these three source files + `Migrations/` into Core and add the two
package references there — nothing else changes.

## Contents

| File | Purpose |
|---|---|
| `RegistryDbContext.cs` | Provider-neutral EF model over the existing domain POCOs (`Client`, `DeploymentComponent`, `PackageRecord`, `DeploymentRunRecord`). String keys (`Guid` "N"), enums stored as readable strings, Restrict FKs so audit history can't cascade-delete. |
| `EfCoreRegistryStore.cs` | `IRegistryStore` implementation. Short-lived contexts from an `IDbContextFactory` — safe for desktop apps, no stale change-tracker state. Semantics mirror `LocalFileRegistryStore` (case-insensitive client lookup, latest-deployed baseline, stale-package scan). |
| `DesignTimeDbContextFactory.cs` | For `dotnet ef` tooling only. Targets LocalDB by default; override with `REGISTRY_CONNECTION_STRING`. |
| `Migrations/20260831061202_InitialCreate` | Schema for the four tables, generated against the SQL Server provider. |
| `Migrations/20260831084956_AddClientProfileFields` | Client-profile columns added pre-WinForms (contact, git repo/branch, publish configuration JSON, AMC, infrastructure/hosting ownership). Safe `ALTER TABLE … ADD` — applies cleanly to an existing registry. |

## SQL Server / Azure SQL setup

1. **Provision the DB** (plan §2.2 recommends a small Azure SQL Database so
   both your machine and client servers can reach it):
   - Azure SQL: `az sql db create -g <rg> -s <server> -n DeployToolkitRegistry -e Basic`
   - Or any SQL Server 2016+ / LocalDB instance.

2. **Connection strings** (store the secret part per plan §12 — never in
   source control):
   ```
   Azure SQL:  Server=tcp:<server>.database.windows.net,1433;Database=DeployToolkitRegistry;User Id=<user>;Password=<password>;Encrypt=True;
   LocalDB:    Server=(localdb)\MSSQLLocalDB;Database=DeployToolkitRegistry;Trusted_Connection=True;TrustServerCertificate=True;
   ```

3. **Apply the schema** — either let the app do it at startup:
   ```csharp
   var registry = EfCoreRegistryStore.CreateSqlServer(connectionString);
   await registry.InitializeAsync();   // applies migrations, idempotent
   ```
   or explicitly:
   ```
   dotnet ef database update --project src/DeployToolkit.Core.EfCore
   ```

4. **When the model changes:**
   ```
   dotnet ef migrations add <Name> --project src/DeployToolkit.Core.EfCore
   ```

## Offline mode (plan §9)

`LocalFileRegistryStore` in `DeployToolkit.Core` remains the offline fallback:
when a Deployer run can't reach the registry from inside a client network, it
writes results locally; the Packager later reconciles them into this EF store
(reconciliation flow is a later phase — plan Phase 10 remainder).

## Testing notes

`tools/DeployToolkit.EfCore.SelfTest` runs the full A/B/C baseline +
stale-package + orchestrator scenario against this store on **SQLite**
(`EnsureCreatedAsync`), because the model is provider-neutral. Migrations
themselves are SQL Server-flavored (column types are baked at scaffold time),
so test/throwaway stores bootstrap via `EnsureCreatedAsync()` — that split is
intentional and documented on both methods.

## Retargeting to .NET 10

When you move the solution to `net10.0-windows` on your dev machine, bump
both EF packages from `8.0.11` to the matching 10.x line and run
`dotnet ef migrations has-pending-model-changes` to catch any type-mapping
drift before adding a migration.
