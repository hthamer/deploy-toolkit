# DeployToolkit.Api — Central Registry REST API (Phase 1)

ASP.NET Core **.NET 8 minimal API** that fronts the toolkit's central
registry database — the **same database the Packager app uses**
(`DeployToolkit.Core.EfCore` / `RegistryDbContext`, plan §2.2), with the
connection string taken from `appsettings.json` instead of the WinForms
settings file.

Phase 1 scope: **username/password authentication.**

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

| Method | Route                    | Auth           | Purpose |
|--------|--------------------------|----------------|---------|
| POST   | `/api/auth/authenticate` | none           | Validates the username/password pair against `ApiUsers` in the registry DB. **200** → `{status, message, username, displayName, tokenType, accessToken, expiresAtUtc}` · **400** → missing fields · **401** → unknown user / wrong password / disabled account (one generic message — no user enumeration) · **429** → per-IP rate limit. |
| GET    | `/api/auth/me`           | Bearer (JWT)   | Protected sample endpoint: echoes the identity carried by the token. The pattern phase 2 protected endpoints (`POST /api/deploy`, …) reuse. |
| GET    | `/`                      | none           | Health/identity probe. |
| *      | `/swagger`               | Development only | Swagger UI for interactive testing. |

Successful login response example:

```json
{
  "status": "ok",
  "message": "Authentication succeeded.",
  "username": "admin",
  "displayName": "Registry Administrator",
  "tokenType": "Bearer",
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.…",
  "expiresAtUtc": "2026-09-03T18:24:11.794+00:00"
}
```

Call a protected endpoint with:

```
Authorization: Bearer <accessToken>
```

## Security model

* **Passwords are never stored.** `ApiUsers.PasswordHash` holds a versioned
  PBKDF2-SHA256 string (`pbkdf2-sha256$<iterations>$<salt>$<subkey>`,
  210 000 iterations, 128-bit random salt, 256-bit subkey) produced by
  `Pbkdf2PasswordHasher`. Verification is constant-time
  (`CryptographicOperations.FixedTimeEquals`), and unknown usernames burn
  the same PBKDF2 work as real lookups (no timing-based user enumeration).
* **Tokens** are HS256 JWTs (≥ 256-bit signing key), lifetime-validated with
  a 1-minute clock skew, carrying `sub` (stable user id), `unique_name`
  (username) and `display_name`. Stateless — no session table; expiry (and
  re-authentication) is the phase-1 logout story.
* **Brute-force backstop** — per-IP fixed-window rate limit on
  `/api/auth/*` (default 10 requests/minute, `Auth:RateLimit:*`).
* **Fail-fast configuration** — the host refuses to start with a missing or
  too-short signing key, a missing connection string, or an unsupported
  database provider, instead of failing at the first login.

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
      "Password": "",
      "DisplayName": ""
    },
    "Jwt": {
      "Issuer": "DeployToolkit.RegistryApi",
      "Audience": "DeployToolkit.Clients",
      "AccessTokenMinutes": 480,
      "SigningKey": ""                // REQUIRED, ≥ 32 chars: openssl rand -base64 48
    },
    "RateLimit": { "PermitLimit": 10, "WindowSeconds": 60 }
  }
}
```

Every value can be overridden by environment variables (never commit real
secrets — plan §12): `ConnectionStrings__Registry`,
`Auth__Jwt__SigningKey`, `Auth__SeedAdmin__Username`,
`Auth__SeedAdmin__Password`, …

`appsettings.Development.json` switches the store to a local SQLite file
(`registry-dev.db`), seeds `admin` / `ChangeMe!123` and uses a throwaway
signing key — **development conveniences only**, exactly the provider-neutral
model + `EnsureCreated` split the `DeployToolkit.EfCore.SelfTest` harness
uses. Production stays on SQL Server / Azure SQL with real migrations.

## Database changes

`DeployToolkit.Core.EfCore` gained one entity + migration:

* `ApiUser` / table **`ApiUsers`** — API credentials (username unique,
  PBKDF2 password hash, `IsActive` soft-disable, `CreatedUtc`,
  `LastLoginUtc`). Migration `20260903103809_AddApiUsers` is a plain
  `CREATE TABLE`: it applies cleanly to an existing registry, and the
  Packager/Deployer simply never query the table. Their startup
  `MigrateAsync` picks the new migration up automatically.

## Running

```bash
# from the repo root
dotnet run --project src/DeployToolkit.Api            # http://localhost:5080 (Development profile)

# production-ish: point at SQL Server and provide a real key
ConnectionStrings__Registry="Server=tcp:<server>.database.windows.net,1433;Database=DeployToolkitRegistry;User Id=…;Password=…;Encrypt=True;" \
Auth__Jwt__SigningKey="<openssl rand -base64 48>" \
dotnet run --project src/DeployToolkit.Api --urls http://+:80
```

First run on an empty SQL Server registry: the startup migration applies
`InitialCreate` → `AddClientProfileFields` → `AddPackageLocation` →
`AddApiUsers` in order, then the seed step (if configured) creates the
first user. Behind IIS/reverse proxy, terminate TLS there and keep the
API on plain HTTP internally — the Deployer's `ApiBaseUrl` should then be
the `https://…` front door.

## Creating additional users (until phase 2 adds management endpoints)

```sql
-- hash format produced by Pbkdf2PasswordHasher (210000 iterations);
-- generate the row's PasswordHash with the API's hasher (dotnet-script /
-- a small console call to Pbkdf2PasswordHasher.Hash) and insert:
INSERT INTO ApiUsers (UserId, Username, DisplayName, PasswordHash, IsActive, CreatedUtc)
VALUES (LOWER(REPLACE(NEWID(),'-','')), 'deployer1', 'Deployer account', 'pbkdf2-sha256$…', 1, SYSUTCDATETIME());
```

## What phase 2+ is expected to add

* `POST /api/deploy` — receives the Deployer's `ApiDeploymentReport`
  (camelCase, already defined in `RegistryApiClient`) and writes a
  `DeploymentRunRecord`; protected by the same Bearer token.
* User management endpoints (create / disable / password change).
* Refresh-token or short-lived-token strategy if the deployment flows need
  longer sessions than `AccessTokenMinutes`.
