# DeployToolkit.Api — Central Registry REST API (Phase 1)

ASP.NET Core **.NET 8 minimal API** that fronts the toolkit's central
registry database — the **same database the Packager app uses**
(`DeployToolkit.Core.EfCore` / `RegistryDbContext`, plan §2.2), with the
connection string taken from `appsettings.json` instead of the WinForms
settings file.

Phase 1 scope: **username/password authentication — token-free by design**
plus a **background service that rotates the passwords every 45 minutes**.

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
  "passwordChangedUtc": "2026-09-03T11:03:48.295+00:00",
  "passwordRotatesAtUtc": "2026-09-03T11:48:48.295+00:00"
}
```

## Password rotation (background service)

`PasswordRotationService` is a hosted `BackgroundService` that replaces the
API users' passwords on a schedule:

* **Cadence** — first pass `InitialDelaySeconds` (default 10) after startup,
  then every `IntervalMinutes` (default **45 minutes**, per requirement).
  The initial pass immediately supersedes any password that came from
  configuration/seed — a plaintext in appsettings.json should not stay
  valid forever.
* **What changes** — every *active* `ApiUser` row (optionally filtered with
  the comma-separated `Usernames` allow-list) gets its own crypto-random
  password (24 chars, unambiguous alphabet, `RandomNumberGenerator`), stored
  as a PBKDF2-SHA256 hash. `PasswordChangedUtc` is stamped for auditing —
  `SELECT Username, PasswordChangedUtc FROM ApiUsers` tells you the rotation
  is alive.
* **Where the new password goes** — with no token flow, the operator/clients
  must learn the current password out-of-band:
  * `App_Data/current-api-password.json` — the CURRENT password set
    (overwritten every cycle, never a history):
    `{"generatedAtUtc": …, "nextRotationUtc": …, "users": [{"username": …, "password": …}]}`.
  * The application log (`NEW password for API user '…'`), when
    `LogPasswords` is enabled (default).
  Both channels are deliberate, documented trade-offs: anyone who can read
  them can log in until the next cycle. Disable `LogPasswords` in hardened
  environments and treat the state file as the single distribution channel.
* **Failure policy** — a failed cycle (DB blip) is logged and retried at the
  next tick; the previous password stays valid meanwhile (availability over
  strict rotation SLA in phase 1).

## Security model

* **Passwords are never stored.** `ApiUsers.PasswordHash` holds a versioned
  PBKDF2-SHA256 string (`pbkdf2-sha256$<iterations>$<salt>$<subkey>`,
  210 000 iterations, 128-bit random salt, 256-bit subkey) produced by
  `Pbkdf2PasswordHasher`. Verification is constant-time
  (`CryptographicOperations.FixedTimeEquals`), and unknown usernames burn
  the same PBKDF2 work as real lookups (no timing-based user enumeration).
* **No tokens, no sessions** — every request presents username + password;
  the stateless API validates and answers. The 45-minute rotation caps the
  blast radius of any leaked credential.
* **Brute-force backstop** — per-IP fixed-window rate limit on
  `/api/auth/*` (default 10 requests/minute, `Auth:RateLimit:*`).
* **Fail-fast configuration** — the host refuses to start with a missing
  connection string or an unsupported database provider, instead of failing
  at the first login.

## Configuration (appsettings.json)

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
    "SeedAdmin": {                    // creates the FIRST ApiUser ONLY when the table is empty
      "Username": "",
      "Password": "",                 // rotation replaces it after the first cycle
      "DisplayName": ""
    },
    "PasswordRotation": {
      "Enabled": true,
      "IntervalMinutes": 45,          // user requirement: every 45 minutes
      "InitialDelaySeconds": 10,
      "PasswordLength": 24,
      "Usernames": "",                // empty = all active users
      "WriteStateFile": true,
      "LogPasswords": true
    },
    "RateLimit": { "PermitLimit": 10, "WindowSeconds": 60 }
  }
}
```

Every value can be overridden by environment variables (never commit real
secrets — plan §12): `ConnectionStrings__Registry`,
`Auth__SeedAdmin__Username`, `Auth__SeedAdmin__Password`,
`Auth__PasswordRotation__IntervalMinutes`, …

`appsettings.Development.json` switches the store to a local SQLite file
(`registry-dev.db`) and seeds `admin` / `ChangeMe!123` — development
conveniences only, exactly the provider-neutral model + `EnsureCreated`
split the `DeployToolkit.EfCore.SelfTest` harness uses. Production stays on
SQL Server / Azure SQL with real migrations. **Note:** rotation is active in
Development too — 10 seconds after startup the seeded password stops
working; read `App_Data/current-api-password.json` for the current one.

## Database changes

`DeployToolkit.Core.EfCore` gained one entity + two migrations:

* `ApiUser` / table **`ApiUsers`** — API credentials (username unique,
  PBKDF2 password hash, `IsActive` soft-disable, `CreatedUtc`,
  `LastLoginUtc`, `PasswordChangedUtc`). `20260903103809_AddApiUsers` is a
  plain `CREATE TABLE` and `20260903105500_AddPasswordChangedUtc` a plain
  `ALTER TABLE … ADD`: both apply cleanly to an existing registry, and the
  Packager/Deployer simply never query the table. Their startup
  `MigrateAsync` picks the new migrations up automatically.

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
`AddApiUsers` → `AddPasswordChangedUtc` in order, then the seed step (if
configured) creates the first user, and the rotation service takes over
from there. Behind IIS/reverse proxy, terminate TLS there and keep the API
on plain HTTP internally — the Deployer's `ApiBaseUrl` should then be the
`https://…` front door.

## Creating additional users (until phase 2 adds management endpoints)

Insert rows directly into `ApiUsers` with hashes produced by
`Pbkdf2PasswordHasher.Hash` (e.g. via a small console call). The rotation
service picks up new active rows automatically on its next cycle — you do
NOT need to manage their passwords by hand.

## What phase 2+ is expected to add

* `POST /api/deploy` — receives the Deployer's `ApiDeploymentReport`
  (camelCase, already defined in `RegistryApiClient`), authenticated by the
  same username/password pair (sent per request — e.g. HTTP Basic or a
  small credential header — matching the token-free phase-1 model), and
  writes a `DeploymentRunRecord`.
* User management endpoints (create / disable / trigger an out-of-band
  rotation).
* If the 45-minute rotation ever collides with real-world deployment
  windows, consider per-user rotation schedules or a manual
  "extend current password" toggle.
