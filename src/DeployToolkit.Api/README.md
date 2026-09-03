# DeployToolkit.Api — Central Registry REST API (Phase 1)

ASP.NET Core **.NET 8 minimal API** that fronts the toolkit's central
registry database — the **same database the Packager app uses**
(`DeployToolkit.Core.EfCore` / `RegistryDbContext`, plan §2.2), with the
connection string taken from `appsettings.json` instead of the WinForms
settings file.

Phase 1 scope: **username/password authentication — token-free by design**
plus a **background service that rotates the passwords every 45 minutes**.
Phase 2 scope: **`POST /api/deploy`** — the Deployer reports a finished
deployment and the API flags the package as Deployed in the registry using
the same "Mark Deployed" semantics the Packager/orchestrator use.

**Everything credential- and rotation-related lives in the DATABASE** —
never in appsettings.json or any other configuration file:

| Table | Holds |
|-------|-------|
| `ApiUsers` | The API accounts (username, PBKDF2 password hash, `IsActive`, `PasswordChangedUtc`). |
| `ApiSettings` | All rotation settings (`Auth.Rotation.*` key/value rows). |
| `ApiCredentialLogs` | Audit trail of every credential change; **the latest row per username IS the current working password**. |

```
POST {baseUrl}/api/auth/authenticate
POST {baseUrl}/api/deploy          (HTTP Basic credentials + deploy report)
```

The route and payload contracts are pinned by the WinForms clients:
`RegistryApiClient` (DeployToolkit.AppKit) POSTs camelCase
`{"username": …, "password": …}` to `{baseUrl}/api/auth/authenticate`
(HTTP 2xx = "Login OK"; the Deployer's *Registry connection* dialog shows
the response body as its green status text) and the camelCase
`ApiDeploymentReport` to `{baseUrl}/api/deploy` after every deployment,
with the session credentials in the HTTP Basic header. Any non-2xx status
code plus the response body is surfaced as the failure detail.

## Endpoints

| Method | Route                    | Auth | Purpose |
|--------|--------------------------|------|---------|
| POST   | `/api/auth/authenticate` | none | Validates the username/password pair against `ApiUsers` in the registry DB. **200** → `{status, message, username, displayName, passwordChangedUtc, passwordRotatesAtUtc}` · **400** → missing fields · **401** → unknown user / wrong password / disabled account (one generic message — no user enumeration) · **429** → per-IP rate limit. |
| POST   | `/api/deploy` | HTTP Basic | Registers the Deployer's finished deployment. **200** → `{status, message, packageId, packageStatus, runId, result, deployedUtc, authenticatedAs}` · **400** → missing `packageId` / invalid `result` · **401** → missing/invalid credentials (`WWW-Authenticate: Basic` challenge included) · **404** → unknown `packageId` · **429** → per-IP rate limit. |
| GET    | `/`                      | none | Health/identity probe. |
| *      | `/swagger`               | Development only | Swagger UI for interactive testing. |

Successful login response example (no token fields — there is no token flow):

```json
{
  "status": "ok",
  "message": "Authentication succeeded.",
  "username": "admin",
  "displayName": "Registry Administrator",
  "passwordChangedUtc": "2026-09-03T11:53:26.581+00:00",
  "passwordRotatesAtUtc": "2026-09-03T12:38:26.581+00:00"
}
```

## First run — credentials registered in the database (never in config)

On the very first startup against an `ApiUsers` table with no rows, the API
registers the initial credential **in the database**:

* username `admin` (fixed in code — no credential material ever lives in
  configuration),
* a crypto-random 24-character password,
* the PBKDF2-SHA256 hash in `ApiUsers.PasswordHash`,
* the plaintext in `ApiCredentialLogs` with `Reason = 'InitialSeed'`.

Retrieve the current working credential with a plain SELECT:

```sql
SELECT TOP 1 Username, Password, CreatedUtc
  FROM ApiCredentialLogs
 WHERE Username = 'admin'
 ORDER BY CreatedUtc DESC;
-- SQLite: ... SELECT ... LIMIT 1
```

The plaintext never appears in appsettings.json, the console log, or any
file. Rename the row (or insert additional users) if `admin` is not wanted.

## Password rotation (background service)

`PasswordRotationService` is a hosted `BackgroundService` that replaces the
API users' password with a fresh crypto-random one on a schedule:

* **Cadence** — driven entirely by the `ApiSettings` table (default
  **45 minutes**, per requirement). The service re-reads the settings every
  cycle and at least every ~15 s while idle, so an operator can retune the
  schedule on the RUNNING service with a plain UPDATE:
  ```sql
  UPDATE ApiSettings SET Value = '60'
   WHERE Key = 'Auth.Rotation.IntervalMinutes';
  ```
  No restart, no redeploy. A missing or past `Auth.Rotation.NextRunUtc`
  means "due now".
* **What changes** — every *active* `ApiUser` row (optionally filtered with
  the comma-separated `Auth.Rotation.Usernames` allow-list) gets ONE fresh
  crypto-random password per cycle, stored as a PBKDF2-SHA256 hash, and
  `PasswordChangedUtc` is stamped. `SELECT Username, PasswordChangedUtc
  FROM ApiUsers` tells you the rotation is alive.
* **Where the new password goes** — into the DATABASE
  (`ApiCredentialLogs`, one row per rotated user, `Reason =
  'ScheduledRotation'`). There is no state file (the former
  `App_Data/current-api-password.json` is gone) and no plaintext in the
  log by default. Anyone with SELECT access to the registry database can
  read the current password and log in until the next rotation — restrict
  read access to the registry accordingly; it is the same trust boundary
  as the old state file, but centralized, auditable, and backed up with
  the registry.
* **Failure policy** — a failed cycle (DB blip) is logged and retried
  after a 30 s backoff; the previous password stays valid meanwhile
  (availability over strict rotation SLA in phase 1). A cycle with zero
  matching users still advances the schedule (no busy spinning).

### Rotation settings (ApiSettings table — seeded on first run)

| Key | Default | Meaning |
|-----|---------|---------|
| `Auth.Rotation.Enabled` | `true` | Master switch; `false` = idle (polls until re-enabled). |
| `Auth.Rotation.IntervalMinutes` | `45` | Minutes between rotations (fractional allowed, floor 0.5). |
| `Auth.Rotation.PasswordLength` | `24` | Generated password length (16–128). |
| `Auth.Rotation.Usernames` | *(empty)* | Comma-separated allow-list; empty = all active users. |
| `Auth.Rotation.LogPasswords` | `false` | ALSO log new passwords (debug convenience only). |
| `Auth.Rotation.LastRunUtc` | *(service-written)* | Last completed rotation (ISO-8601, UTC). |
| `Auth.Rotation.NextRunUtc` | *(service-written)* | Next due rotation; missing/past = due now. |

Unknown keys are ignored — other components may store their own rows.

## Deploy endpoint (the Deployer flags the package as deployed)

`POST /api/deploy` receives the report the Deployer sends after finishing a
deployment and applies the SAME "Mark Deployed" semantics the local
orchestrator/Packager path uses (`EfCoreRegistryStore.MarkDeployedAsync` +
run recording):

* **Authentication** — HTTP Basic with the session's registry
  username/password (the same pair the Login button verified). Token-free,
  per-request; missing header → **401** with a
  `WWW-Authenticate: Basic` challenge; wrong/disabled credentials →
  **401** with the same generic messages as the login endpoint. Each report
  burns a PBKDF2 verify, so the endpoint sits behind the same per-IP rate
  limiter as `/api/auth/*`.
* **Payload** — the camelCase `ApiDeploymentReport` the
  `RegistryApiClient` already sent before this endpoint existed
  (`packageId, client, component, version, result, healthCheckPassed,
  message, deployedBy, startedUtc, completedUtc, targetType`).
* **PackageId traceability (user-requested fix)** — the PackageId reported
  here MUST be the one embedded in the package's own `manifest.json`
  (`"PackageId"` key). The Packager now generates the id BEFORE writing the
  zip (`PackageBuilder.BuildAsync`), stamps it into the manifest, and
  creates the registry row with the SAME id — zip, row and report always
  agree. The Deployer matches the loaded zip by that manifest id first
  (`StageLoadPackage`), refuses to guess when a manifest id is not in the
  connected registry (that meant the wrong registry), and reuses the
  manifest id for offline catch-up records. Previously the manifest carried
  NO id, so the Deployer guessed by version+hash or invented one offline —
  reports then 404'd with a PackageId the registry had never heard of.
  The lookup itself accepts any textual GUID format (N/D/B/P/X) — ids are
  normalized before the query.
* **Success (`result = "Success"`)** → package `Status = Deployed`,
  `DeployedBy` (from the report, falling back to the authenticated user)
  and `DeployedUtc` (from `completedUtc`) are stamped — exactly what the
  orchestrator does after a green health check. "Latest deployed baseline"
  and "stale packages" queries in the Packager immediately see it.
* **Failed / RolledBack** → the package status is left UNTOUCHED (a
  rolled-back run never shipped), but the outcome is still audited.
* **Every accepted report** writes a `DeploymentRunRecord` with the same
  fields `RecordRunStartAsync` / `RecordRunCompleteAsync` write locally
  (`RunId`, `PackageId`, `StartedUtc`, `CompletedUtc`, `Result`,
  `HealthCheckResult`). The live log file stays on the Deployer machine.
* **Response 200** → `{status, message, packageId, packageStatus, runId,
  result, deployedUtc, authenticatedAs}` — the Deployer's log pane prints
  `Central API accepted the deploy report: …` with this body.
* **Client side** — `RegistryApiClient.ReportDeploymentAsync(baseUrl,
  report, username, password)` sets the Basic header;
  `StageDeploy` passes `Shell.ConnectionSettings.ApiUsername/ApiPassword`
  (session-only, never persisted). Without session credentials the report
  is skipped with a WARN line telling the user to log in via the Registry
  connection dialog.
* **Rotation interplay** — credentials rotate every 45 minutes
  (see below); a report sent with a password that has since been rotated
  returns 401 with the API's message, and the Deployer logs it at ERROR
  without failing the deployment itself.

## Security model

* **Passwords are never stored in `ApiUsers`.** That table holds a
  versioned PBKDF2-SHA256 string
  (`pbkdf2-sha256$<iterations>$<salt>$<subkey>`, 210 000 iterations,
  128-bit random salt, 256-bit subkey) produced by
  `Pbkdf2PasswordHasher`. Verification is constant-time
  (`CryptographicOperations.FixedTimeEquals`), and unknown usernames burn
  the same PBKDF2 work as real lookups (no timing-based user enumeration).
* **The current password IS recoverable from `ApiCredentialLogs`** — a
  deliberate, user-required trade-off so operators/clients can pick up the
  new credential after each rotation without any config file or token
  flow. Keep `SELECT` on that table as restricted as the old state file
  would have been.
* **No tokens, no sessions** — every request presents username + password;
  the stateless API validates and answers. The 45-minute rotation caps the
  blast radius of any leaked credential.
* **Brute-force backstop** — per-IP fixed-window rate limit on
  `/api/auth/*` (default 10 requests/minute, `Auth:RateLimit:*`).
* **Fail-fast configuration** — the host refuses to start with a missing
  connection string or an unsupported database provider, instead of failing
  at the first login.

## Configuration (appsettings.json)

Only infrastructure stays in configuration — **no credentials, no rotation
settings**:

```jsonc
{
  "ConnectionStrings": {
    // The SAME registry database the Packager app uses.
    "Registry": "Server=(localdb)\\MSSQLLocalDB;Database=DeployToolkitRegistry;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "Database": {
    "Provider": "SqlServer",          // Sqlite = local dev only
    "ApplyMigrationsOnStartup": true  // applies the EF migrations in Core.EfCore
  },
  "Auth": {
    "RateLimit": { "PermitLimit": 10, "WindowSeconds": 60 }
  }
}
```

`appsettings.Development.json` switches the store to a local SQLite file
(`registry-dev.db`) — development convenience only, exactly the
provider-neutral model + `EnsureCreated` split the
`DeployToolkit.EfCore.SelfTest` harness uses. Production stays on
SQL Server / Azure SQL with real migrations. On first run the API
registers the `admin` credential and the `Auth.Rotation.*` settings rows
directly in the database; read the current password from
`ApiCredentialLogs`.

## Database changes

`DeployToolkit.Core.EfCore` gained three API-owned entities + migrations
(plain `CREATE TABLE`s — they apply cleanly to an existing registry, and
the Packager/Deployer simply never query these tables):

* `ApiUser` / **`ApiUsers`** — API credentials (username unique, PBKDF2
  password hash, `IsActive` soft-disable, `CreatedUtc`, `LastLoginUtc`,
  `PasswordChangedUtc`).
* `ApiSetting` / **`ApiSettings`** — key/value runtime settings (the
  rotation schedule above).
* `ApiCredentialLog` / **`ApiCredentialLogs`** — credential audit trail
  (latest row per username = current password; indexed on
  `(Username, CreatedUtc)`).

## Running

```bash
# from the repo root
dotnet run --project src/DeployToolkit.Api            # http://localhost:5080 (Development profile)

# production-ish: point at SQL Server
ConnectionStrings__Registry="Server=tcp:<server>.database.windows.net,1433;Database=DeployToolkitRegistry;User Id=…;Password=…;Encrypt=True;" \
dotnet run --project src/DeployToolkit.Api --urls http://+:80
```

First run on an empty SQL Server registry: the startup migration applies
`InitialCreate` → `AddClientProfileFields` → `AddPackageLocation` →
`AddApiUsers` → `AddPasswordChangedUtc` → `AddApiSettingsAndCredentialLogs`
in order, then the bootstrap registers the initial `admin` credential in
the database, and the rotation service takes over from there. Behind
IIS/reverse proxy, terminate TLS there and keep the API on plain HTTP
internally — the Deployer's `ApiBaseUrl` should then be the `https://…`
front door.

## Creating additional users (until management endpoints exist)

Insert a row into `ApiUsers` with any placeholder hash and `IsActive = 1` —
the rotation service picks the user up on its next cycle, replaces the
password with a generated one, and registers the real credential in
`ApiCredentialLogs` for you. To keep a user OUT of rotation, add its
username to `Auth.Rotation.Usernames`… the allow-list works the other way:
when non-empty, ONLY the listed usernames are rotated.

## What phase 3+ is expected to add

* Deployment history/log retrieval endpoints (e.g. GET /api/runs for a
  component) and package-store lookup by client/component, so the Deployer
  can pick packages over the API without direct DB access.
* User management endpoints (create / disable / trigger an out-of-band
  rotation — writing to the same `ApiUsers` + `ApiCredentialLogs` tables).
* If the 45-minute rotation ever collides with real-world deployment
  windows, consider per-user rotation schedules (per-user
  `ApiSettings` rows) or a manual "extend current password" toggle.
