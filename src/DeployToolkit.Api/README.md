# DeployToolkit.Api — Central Registry REST API (Phase 1)

ASP.NET Core **.NET 8 minimal API** that fronts the toolkit's central
registry database — the **same database the Packager app uses**
(`DeployToolkit.Core.EfCore` / `RegistryDbContext`, plan §2.2), with the
connection string taken from `appsettings.json` instead of the WinForms
settings file.

Phase 1 scope: **username/password authentication — token-free by design**
plus a **background service that rotates the passwords every 45 minutes**.

**Everything credential- and rotation-related lives in the DATABASE** —
never in appsettings.json or any other configuration file:

| Table | Holds |
|-------|-------|
| `ApiUsers` | The API accounts (username, PBKDF2 password hash, `IsActive`, `PasswordChangedUtc`). |
| `ApiSettings` | All rotation settings (`Auth.Rotation.*` key/value rows). |
| `ApiCredentialLogs` | Audit trail of every credential change; **the latest row per username IS the current working password**. |

```
POST {baseUrl}/api/auth/authenticate
```

The route and payload contract are pinned by the WinForms clients:
`RegistryApiClient` (DeployToolkit.AppKit) POSTs camelCase
`{"username": …, "password": …}` to `{baseUrl}/api/auth/authenticate`,
treats HTTP 2xx as "Login OK" (the Deployer's *Registry connection* dialog
shows the response body as its green status text) and surfaces any other
status code plus the response body as the failure detail.

## Endpoints

| Method | Route                    | Auth | Purpose |
|--------|--------------------------|------|---------|
| POST   | `/api/auth/authenticate` | none | Validates the username/password pair against `ApiUsers` in the registry DB. **200** → `{status, message, username, displayName, passwordChangedUtc, passwordRotatesAtUtc}` · **400** → missing fields · **401** → unknown user / wrong password / disabled account (one generic message — no user enumeration) · **429** → per-IP rate limit. |
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

## Creating additional users (until phase 2 adds management endpoints)

Insert a row into `ApiUsers` with any placeholder hash and `IsActive = 1` —
the rotation service picks the user up on its next cycle, replaces the
password with a generated one, and registers the real credential in
`ApiCredentialLogs` for you. To keep a user OUT of rotation, add its
username to `Auth.Rotation.Usernames`… the allow-list works the other way:
when non-empty, ONLY the listed usernames are rotated.

## What phase 2+ is expected to add

* `POST /api/deploy` — receives the Deployer's `ApiDeploymentReport`
  (camelCase, already defined in `RegistryApiClient`), authenticated by the
  same username/password pair (sent per request — e.g. HTTP Basic or a
  small credential header — matching the token-free phase-1 model), and
  writes a `DeploymentRunRecord`.
* User management endpoints (create / disable / trigger an out-of-band
  rotation — writing to the same `ApiUsers` + `ApiCredentialLogs` tables).
* If the 45-minute rotation ever collides with real-world deployment
  windows, consider per-user rotation schedules (per-user
  `ApiSettings` rows) or a manual "extend current password" toggle.
