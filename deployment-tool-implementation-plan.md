# Deployment Automation Tool — Implementation Plan (Revision 2)

## 1. Goals & Constraints

- Replace manual, error-prone deployment steps across ~10–30 client servers with a repeatable, versioned process.
- Support .NET Core 3.1 / .NET 6 / 8 / 9 / 10 and .NET Framework 4.8 publish outputs, both self-contained and framework-dependent.
- Support IIS (direct RDP, RDP+VPN, jump-host RDP), **Azure App Service**, and **Plesk shared hosting** as deployment targets.
- Support multiple **components per client** (CMS as parent app, website as nested app, or fully separated apps, or split across machines).
- **No PowerShell/batch scripts on target machines** — some environments block script execution outright. All operations happen as native in-process .NET code (file I/O, `Microsoft.Web.Administration`, `Microsoft.Data.SqlClient`, HTTPS calls) — never a spawned script or `sqlcmd.exe`.
- Package preparation happens same-day, not weeks in advance — the Packager must be fast, low-friction, and git-integrated.
- Packages must have a **confirmed-deployed status**, distinct from merely "created," so future diffs never baseline against a package that was built but never actually shipped.
- Free/self-built.

## 2. High-Level Architecture

```
DeployToolkit.sln
├── DeployToolkit.Core          (class library — all logic, no UI)
│   ├── Manifest/               (manifest model, hashing, diff engine)
│   ├── Packaging/               (zip build/read)
│   ├── Config/                  (appsettings merge engine)
│   ├── GitIntegration/          (LibGit2Sharp wrapper — fetch/pull/status)
│   ├── Targets/                 (IDeploymentExecutor + IIS / Azure App Service / Plesk implementations)
│   ├── IisControl/              (app-pool stop/start via Microsoft.Web.Administration)
│   ├── Database/                (SQL script execution via Microsoft.Data.SqlClient)
│   ├── Backup/                  (backup + rollback engine)
│   ├── Registry/                (EF Core data access to the central registry DB)
│   └── Logging/                 (structured logging)
├── DeployToolkit.Packager       (WinForms app — runs on YOUR machine)
└── DeployToolkit.Deployer       (WinForms app — copied over RDP, runs ON the target server;
                                   for Azure App Service targets it can also run directly
                                   from your machine — no RDP needed, see §7)
```

### 2.1 Data flow

```
[Your machine]                                    [Target: IIS server via RDP,
                                                     or Azure App Service / Plesk
                                                     reachable directly]
Select git folder
   → auto pull (LibGit2Sharp)
   → dotnet publish
   → Packager.exe: diff vs. last DEPLOYED
     manifest for this component
   → delta.zip + manifest.json
        │
        │ (copy over RDP: clipboard / shared drive / zip transfer
        │  — OR sent directly via HTTPS for Azure App Service)
        ▼
   Deployer.exe (or direct API call for Azure)
   → backup → stop → deploy files → merge config
   → run DB scripts → start → health check
   → writes result back to Central Registry DB
     (status: Deployed, with commit SHA + manifest)
```

### 2.2 Central Registry Database

Both apps connect to one shared database — the source of truth for clients, components, package history, and deployment status. Recommend a small Azure SQL Database (cheap at this scale) so it's reachable from both your machine and client servers without you hosting/maintaining a server yourself.

**Schema (core tables):**

```
Clients
  ClientId, Name, Notes

DeploymentComponents
  ComponentId, ClientId (FK), Name              -- e.g. "CMS", "Website"
  TargetType                                     -- IisLocal | AzureAppService | Plesk
  TargetFramework, IsSelfContained
  IisSiteName, IisAppPath                        -- nullable, IIS targets only
  AzureAppServiceName, AzureResourceGroup        -- nullable, Azure targets only
  PleskHost, PleskSiteId                         -- nullable, Plesk targets only
  HealthCheckUrl
  DbConnectionRef                                -- pointer to encrypted secret, not the secret itself

Packages
  PackageId, ComponentId (FK), Version, CreatedUtc
  ManifestJson, GitCommitSha
  Status                                          -- Created | Deployed | Superseded | Abandoned
  DeployedUtc, DeployedBy

DeploymentRuns
  RunId, PackageId (FK), StartedUtc, CompletedUtc
  Result                                          -- Success | Failed | RolledBack
  LogPath, HealthCheckResult
```

**Secrets:** connection strings, Azure publish credentials, and Plesk credentials are never stored in plain text — encrypt at the column level (SQL Server Always Encrypted, or AES with a key held only by you) or keep them in Azure Key Vault and store only a reference in `DbConnectionRef`.

**Connectivity risk:** the Deployer needs outbound access to the registry DB from inside the client's network. Flag this alongside the code-signing question as a Phase 0 validation item — RDP-only environments sometimes also restrict outbound traffic. Build an **offline fallback**: if the registry is unreachable, the Deployer works entirely from the local package/manifest files and writes its result to a local JSON file that the Packager reconciles back into the registry next time you're online.

## 3. Manifest Schema (`manifest.json`)

```json
{
  "componentId": "guid-of-component",
  "client": "ClientA",
  "component": "CMS",
  "version": "1.4.0",
  "createdUtc": "2026-08-30T10:00:00Z",
  "gitCommitSha": "a1b2c3d4...",
  "targetFramework": "net8.0",
  "isSelfContained": false,
  "baselineManifest": "manifest-1.3.0.json",
  "files": [
    { "path": "bin/ClientA.Web.dll", "hash": "sha256:...", "sizeBytes": 154200 }
  ],
  "deletedFiles": [],
  "appSettingsDelta": {
    "Feature:NewToggle": true,
    "Smtp:Host": "smtp.newhost.com"
  },
  "dbScripts": [
    { "file": "db/001_add_index.sql", "kind": "schema" },
    { "file": "db/002_seed_data.sql", "kind": "data" }
  ],
  "healthCheckUrl": "https://clienta.example.com/health"
}
```

### 3.1 Package (zip) layout

```
delta.zip
├── manifest.json
├── files/                  (mirrors the publish-output relative paths — only changed files)
│   └── bin/ClientA.Web.dll
└── db/
    ├── 001_add_index.sql
    └── 002_seed_data.sql
```

## 4. Backups

- Local to the target machine, under `%USERPROFILE%\Documents\Backups\{yyyyMMdd}\`.
- Multiple deployments on the same day to the same client append into that day's folder rather than overwriting, e.g. `...\Backups\20260830\1512-CMS\`.
- Built with `System.IO.Compression` — no external tools, consistent with the "no scripts/external processes" constraint.
- Alongside the zip, a `backup-manifest.json` records exactly what was backed up: file list, pre-merge appsettings values, and which DB scripts were about to run — this is what the rollback flow reads.

## 5. Git-Integrated Project Selection (Packager)

- Selecting a project = browsing to its local git working folder, not a publish-output folder.
- On selection, using **LibGit2Sharp** (a native .NET git library — no `git.exe` shell-out): fetch → pull latest on the current branch → capture the resulting commit SHA → **check for a dirty working tree** and warn before proceeding, so uncommitted or wrong-branch changes can't accidentally get packaged.
- Folder → Component matching: the Packager keeps a small **local** mapping (folder path → ComponentId), since paths are machine-specific and shouldn't live in the shared registry. First time a folder is selected, prompt to pick an existing Client/Component from the registry or create a new one; every subsequent selection of that folder auto-resolves the component and pulls its stored settings (target framework, self-contained flag, target type, health check URL, etc.) automatically.
- After pull, the Packager runs `dotnet publish` itself (a normal `Process.Start` on **your own machine** — the "no scripts" constraint applies to target servers, not your build machine) using the framework/self-contained settings stored on the component.

## 6. Component Model

A Client can have 1+ **Components** (e.g., `ClientA / CMS` and `ClientA / Website`), each independently configured with its own target type, site/app path or Azure resource name, framework settings, health check URL, and DB connection reference.

This covers all four topologies you described:
- CMS as the parent IIS app, website nested underneath (or vice versa) — two Components, both `IisLocal`, pointing at the same site but different app paths.
- Fully separated apps — two Components, potentially different sites or even different servers.
- Website-only, with the CMS deployed on a different machine entirely — just two Components with different `IisSiteName`/machine context.

Packages are built **per-component**, not per-client — so a loaded package already knows which app it targets. For IIS components, the Deployer auto-resolves the site/app path from the stored config; the first time a mapping doesn't exist yet on a given machine, it enumerates IIS sites/applications via `Microsoft.Web.Administration` (including nested applications) and lets you pick, then saves the mapping for next time.

## 7. Multi-Target Deployment

An `IDeploymentExecutor` interface in `DeployToolkit.Core.Targets`, with three implementations. The Deployer selects the right one based on the component's `TargetType`.

**IisLocalExecutor** — runs on the target machine via RDP:
1. Load package, verify manifest hashes against zip contents (integrity check)
2. Pre-flight: confirm target framework/runtime present, IIS site exists, disk space for backup
3. Backup (see §4)
4. Stop: app-pool recycle via `Microsoft.Web.Administration`, or drop `app_offline.htm` as a fallback for accounts without IIS management rights
5. Deploy files: copy delta files into place, progress + per-file log
6. Merge appsettings: before/after diff shown, explicit confirm before writing
7. Run DB scripts: connection entry, script preview, explicit confirm, execution log
8. Start + health check: restart app pool, ping health URL, pass/fail result
9. Write result to the Central Registry (or local file in offline mode) — this is what flips the package's status to `Deployed`
10. Rollback remains available standalone from a separate menu item, not just inline in the wizard

**AzureAppServiceExecutor** — uses the Kudu **ZipDeploy REST API** (`POST https://{app}.scm.azurewebsites.net/api/zipdeploy`) with the app's publish credentials, plus the Azure Resource Manager **App Service Configuration API** for App Settings changes (Azure's equivalent of the appsettings merge — a different, HTTPS-only mechanism, not a file merge). Pure `HttpClient` calls, no script, and notably **doesn't require RDP at all** — can run directly from the Packager or from anywhere with network access. Worth considering deployment slots + swap for zero-downtime as a later enhancement.

**PleskExecutor** — Plesk exposes FTPS/SFTP on essentially every plan; use `FluentFTP` or `SSH.NET` (pure library, no shell-out) to upload changed files directly. App restart behavior varies a lot by Plesk configuration (some recycle automatically on file change, some need an explicit call through Plesk's REST/XML-RPC API) — validate against your actual Plesk clients before finalizing the restart step; flagged as the least certain of the three.

## 8. Core Library Components (detail)

### 8.1 Diff/hash engine
Walks a publish output folder, computes SHA-256 per file, compares against the baseline manifest (the most recent package with `Status = Deployed` for that component — see §9). Produces changed/new files; deleted files are flagged separately rather than silently ignored.

### 8.2 appsettings merge engine
Deep-merges a flat/nested `appSettingsDelta` JSON object into an existing `appsettings.json` using `System.Text.Json` (`JsonNode`). Only keys present in the delta are touched; everything else in the target file is preserved. Returns a before/after diff structure the Deployer renders for confirmation before writing. (Azure App Service targets use the Configuration REST API instead — same delta data, different execution path.)

### 8.3 IIS control
`Microsoft.Web.Administration` for app-pool enumeration and stop/start/recycle, called directly as a library API — never via `Restart-WebAppPool` or any PowerShell cmdlet. Fallback: `app_offline.htm` drop/remove, pure file I/O, works even without IIS management rights.

### 8.4 Database execution
`Microsoft.Data.SqlClient` directly — no `sqlcmd.exe` dependency. Scripts are split on `GO` batch separators in-code and each batch run via `SqlCommand.ExecuteNonQuery`. Transaction-wrapped where safe; DDL-heavy scripts that can't be safely wrapped are flagged for explicit confirmation.

### 8.5 Backup & rollback engine
Zips current versions of files about to be overwritten before any change, per §4. `Rollback()` restores a specific backup: files + pre-merge appsettings values. DB rollback isn't automatic (schema changes aren't generally safely reversible) — the tool surfaces the pre-change DB snapshot/script location and leaves that decision to you.

### 8.6 Logging
Structured (JSON-lines) log per run under `logs/{client}/{component}/{timestamp}.log`, plus a live log pane in the Deployer UI — your audit trail per client per deployment.

## 9. Package Lifecycle & Status Tracking

Every package starts as `Created` when the Packager builds it. It only becomes `Deployed` when a Deployer run completes successfully **and the health check passes** (or you manually flag it deployed, for cases shipped outside the tool). The diff engine's baseline rule is: **always compare against the most recent package where `Status = Deployed` for that component — never the most recently created one.**

This directly solves the scenario you described: package A (v1.2) built today but never deployed, package B (v1.3) built weeks later and actually deployed — a subsequent package C correctly diffs against B, not A. The Packager checks for lingering `Created`-but-undeployed packages for the component before building a new one and prompts you to mark them **Abandoned**, **Deployed** (if it turns out it was shipped some other way), or leave them — an abandoned package can never silently become a diff baseline.

Offline-mode runs (registry unreachable from the target network) write their result locally; the Packager reconciles that file back into the registry — including the status flip — the next time it's back online.

## 10. Packager App — UI Flow (runs on your machine)

1. **Select project folder** — triggers git pull, dirty-tree check, and auto-resolves the Client/Component (or prompts to create/select one if new).
2. **Stale package check** — if a prior `Created`-but-undeployed package exists for this component, prompt to resolve it (Abandon / Mark Deployed / Ignore) before continuing.
3. **Publish** — runs `dotnet publish` with the component's stored framework/self-contained settings.
4. **Diff preview** — changed files vs. the last `Deployed` baseline for this component, with sizes; manual exclude option.
5. **appsettings delta editor** — simple key/value grid for this release's config changes (not raw JSON editing).
6. **DB scripts** — attach `.sql` files, tag each as schema/data.
7. **Build package** — writes `delta.zip` + `manifest.json`, records a `Created` package in the registry, ready to copy over RDP or send directly (Azure App Service).

## 11. Deployer App — UI Flow (runs on target, or directly for Azure App Service)

1. **Load package** — points at `delta.zip`; verifies manifest hashes against contents (integrity check); shows component, target type, files, appsettings changes, DB scripts.
2. **Resolve target** — for IIS, auto-resolves site/app from stored component config or prompts to pick (first time on a machine); for Azure/Plesk, confirms the stored resource/host.
3. **Pre-flight check** — runtime presence, site/app existence, disk space for backup.
4. **Backup** — one click; shows what's being backed up and where (`Documents\Backups\{yyyyMMdd}\`).
5. **Stop** — app-pool recycle or `app_offline.htm`, selectable based on account permissions (IIS only).
6. **Deploy files** — copy/zip-deploy/FTP upload depending on target type; progress + per-file log.
7. **Merge config** — before/after diff, explicit confirm before applying (file merge for IIS, Config API for Azure).
8. **DB scripts** — connection entry, preview, explicit confirm, execution log.
9. **Start + health check** — restart, ping health URL, pass/fail.
10. **Done** — summary + log link; writes result to the registry (flips package to `Deployed` on success); **Rollback** available here and standalone later from a separate menu item.

## 12. Security Considerations

- **No script execution anywhere** — satisfies client policy by construction; the Deployer never spawns `powershell.exe`, `cmd.exe`, or `sqlcmd.exe`, only compiled library calls.
- **Code-sign the executables.** Some of the same policies that block scripts also block unsigned `.exe` files. Validate with a real client early (Phase 0) — if required, budget for a low-cost code-signing certificate before wider rollout.
- **Package integrity.** Since packages travel over RDP clipboard/file share (or HTTPS for Azure), the Deployer verifies manifest file hashes against `delta.zip` contents before applying anything — a corrupted/partial copy fails loudly instead of deploying garbage.
- **Least privilege.** Runs under whatever account you're RDP'd in as — no elevated service account required, since everything is file/API-level, not service installation.
- **Registry secrets.** Connection strings and cloud credentials encrypted at rest (Always Encrypted / AES / Key Vault reference) — never stored as plain text in the registry DB.

## 13. Tech Stack

- .NET 10, WinForms (both apps)
- `Microsoft.Web.Administration` — IIS control
- `Microsoft.Data.SqlClient` — DB execution
- `System.Text.Json` — manifest + appsettings merge
- `System.IO.Compression` — packaging/backup
- `LibGit2Sharp` — git fetch/pull/status without shelling out
- `Microsoft.EntityFrameworkCore.SqlServer` — registry DB access
- `FluentFTP` or `SSH.NET` — Plesk file transfer
- `Azure.Identity` / `HttpClient` — Azure App Service Kudu + ARM calls
- No dependencies beyond the above — keeps the self-contained exe small and avoids extra vetting friction with client security teams.

## 14. Development Phases

| Phase | Scope | Est. days |
|---|---|---|
| 0 | Validate constraints: unsigned exe policy, registry DB outbound reachability from a real client RDP session, `Microsoft.Web.Administration` access level | 1–2 |
| 1 | Registry DB schema + EF Core data access layer | 2–3 |
| 2 | Core library: manifest schema, hash/diff engine, package read/write | 3–4 |
| 3 | Git integration: LibGit2Sharp pull, dirty-tree check, commit SHA capture | 1–2 |
| 4 | Packager app: project/component selection, stale-package check, diff preview, appsettings delta editor, DB scripts, build package | 5–6 |
| 5 | Deployer app (IIS target): package load, integrity check, pre-flight, backup, file deploy, logging | 4–5 |
| 6 | IIS control + component/site-app picker | 2–3 |
| 7 | appsettings merge engine + diff preview UI | 2–3 |
| 8 | DB script runner + connection UI | 2–3 |
| 9 | Rollback flow (standalone + wizard-integrated) | 1–2 |
| 10 | Package lifecycle: status tracking, "mark deployed," baseline resolution, offline-mode reconciliation | 2–3 |
| 11 | Secrets encryption for registry-stored credentials | 1–2 |
| 12 | Azure App Service executor (Kudu zip deploy + Config API) | 3–4 |
| 13 | Plesk executor (FTPS/SFTP + restart handling) — validate against real client early | 3–5 |
| 14 | Cross-environment testing (Server versions, .NET Fx vs Core, permission levels, unsigned-exe/outbound-DB policy) | 2–3 |
| 15 | Code signing (if Phase 0 shows it's required) | 1–2 |

**Total: roughly 36–54 developer-days.**

## 15. Recommended Build Order

**Wave 1 — IIS/RDP clients (the majority of your workload today):** Phases 0–11. This alone replaces your current manual process end-to-end for direct-RDP, VPN-RDP, and jump-host clients, including git-integrated packaging and confirmed-deployed tracking. Ship this first.

**Wave 2 — Azure App Service and Plesk:** Phases 12–13, added once Wave 1 is proven. These are structurally independent (`IDeploymentExecutor` implementations) so they don't block or get blocked by Wave 1 — you can keep deploying those clients manually a bit longer without holding up the rest of the rollout.

## 16. Testing Matrix

| Dimension | Values to cover |
|---|---|
| OS | Windows Server 2012 R2, 2016, 2019, 2022 |
| App type | .NET Core 3.1, .NET 6/8/9/10, .NET Framework 4.8 |
| Account permissions | Full admin, IIS-manager-only, filesystem-only |
| Execution/network policy | Script-blocked, unsigned-exe-blocked, outbound-DB-blocked (offline mode) |
| Deployment target | IIS single app, IIS nested app, Azure App Service, Plesk |
| DB access | Direct connection, RDS-restricted (existing db-sync-tool path) |

## 17. Deliverables Checklist

- [ ] Registry DB provisioned (Azure SQL) + EF Core migrations
- [ ] `DeployToolkit.Core` class library (manifest, diff, git, targets, backup, registry access)
- [ ] `DeployToolkit.Packager` — self-contained single-file exe, git-integrated
- [ ] `DeployToolkit.Deployer` — self-contained single-file exe, IIS target
- [ ] Azure App Service executor
- [ ] Plesk executor (validated against a real client)
- [ ] Offline-mode reconciliation flow
- [ ] Written runbook: "prep a package" and "run a deployment," one page each
- [ ] Pilot completed end-to-end on one real IIS client, including a deliberate rollback test
- [ ] Pilot completed on one Azure App Service client and one Plesk client before calling Wave 2 done

## 18. Implementation Status (living document — update as phases land)

Last updated: 2026-09-01 (Asia/Riyadh), after the Phase 4/5 WinForms
session: both shells are now implemented — Packager (plan §10 wizard)
and Deployer (plan §11 stages) over the shared DeployToolkit.AppKit
library — plus the §19 Clients screen. Every engine item and every UI
shell is implemented; 452 self-test checks across 7 suites pass (incl.
four post-delivery hotfix rounds on the dev machine: (fix1) a deferred,
crash-guarded split layout for the Clients screen; (fix2) a git
credential chain — URL → options → Windows Credential Manager, i.e. what
Git Credential Manager / Visual Studio store — with a one-shot
prompt+retry so HTTPS fetches no longer die with a 401, plus the
two-page Clients screen and taskbar fix; (fix3) a full UI/UX
responsiveness round after the reported "busy dialog then freeze":
every guarded operation is now cancellable via a live busy dialog
(elapsed clock + marquee + Cancel → grace → abandon), git fetches probe
the remote endpoint first (5 s) so a dead VPN fails fast instead of
hanging, LibGit2Sharp fetches are cancellation-aware, and all heavy
synchronous IO (folder hashing, zip build/verify/extract, recursive
project discovery, backup restore, publish-output scans) was moved off
the WinForms UI thread; (fix4) the MDI-shell screen-management round
after the user switched the Packager to an MDI container and reported
that "opening another form never closed the previous one, so at close
time I had to close all of them one by one": the shell now runs a
single-front-screen policy (`ShellScreenPolicy`, pure + headless-tested)
— stateless screens (Clients / Reconcile / Connection) are REPLACED on
every switch instead of stacking, the package wizard is pinned while a
draft is in progress (the §10 flow itself hops to the Clients screen
mid-wizard) with a one-time discard prompt, "New Package…" replaces the
existing wizard (consent when in progress) so wizards never accumulate,
a Window menu lists/switches/closes-all children, the shell menu freezes
while any child is busy or modal, app-close cascades with at most ONE
prompt, unsaved client-profile edits are detected by editor↔record
comparison (DateTimePicker's event-less check-toggle included) and
protected on close/switch/back-navigation, a connection change asks
before discarding guarded work and the embedded Connection dialog stays
open when the host declines, mid-operation closes now cancel the
publish process tree and every async continuation is disposed-form-safe,
the classic maximize-quirk on child activation is fixed, and the shell's
minimum size now fits the largest child so a maximized screen is never
clipped).
WinForms UI code is compile-verified in this sandbox (0 errors / 0 C#
warnings) and runs Windows-only; first visual run happens on the dev
machine. Project layout note: external-package implementations live in
sibling projects so `DeployToolkit.Core` stays zero-NuGet-dependency and
buildable anywhere (see its csproj note); the shells additionally share
`DeployToolkit.AppKit` (multi-targeted: a pure net8.0 asset for headless
self-tests, a net8.0-windows asset for the forms).

| Phase | Scope | Status |
|---|---|---|
| 0 | Constraint validation (unsigned exe policy, outbound DB from client RDP, MWA access) | ☐ Open — **user action, blocks code-signing decision only** |
| 1 | Registry DB schema + EF Core data access layer | ✅ **Done** — `DeployToolkit.Core.EfCore` (EF Core 8 + SQL Server): `RegistryDbContext` over the §2.2 schema (+ §19 client profile), `EfCoreRegistryStore : IRegistryStore`, migrations `InitialCreate` + `AddClientProfileFields`, design-time factory, README with provisioning steps. Verified by `DeployToolkit.EfCore.SelfTest` (48/48) running the full baseline/stale-package/orchestrator scenario **plus client-profile CRUD and package-management rules**; `DeployToolkit.Core.SelfTest` now 135/135 (see Phases 6–13 and §19). Azure SQL provisioning itself is a user action when ready. |
| 2 | Core library: manifest schema, hash/diff engine, package read/write | ✅ **Done** — `DeployToolkit.Core` Manifest/ + Packaging/ (writer, reader, integrity check w/ tamper detection), covered by SelfTest |
| 3 | Git integration (LibGit2Sharp pull, dirty-tree check, SHA capture) | ✅ **Done** — `DeployToolkit.Core.Git`: `IGitSynchronizer`/`LibGit2Synchronizer` (fetch → FF-only pull → dirty-tree skip w/ `PullEvenIfDirty` override → SHA capture, `DivergedBranchException` on divergence — never an auto merge commit). **Post-delivery hotfix (fix2):** HTTPS fetches now resolve credentials in-process — URL-embedded → options → Windows Credential Manager (`git:https://host` entries written by Git Credential Manager / Visual Studio) — and a 401/403 offers the interactive prompt exactly once (`GitCredentialsDialog` + `GitCredentialUi`, optional "remember" via `CredWrite`), then fails with an actionable `GitAuthenticationException`. **Post-delivery hotfix (fix3):** `SynchronizeAsync` is cancellation-aware (`WaitAsync` frees the caller even though libgit2 cannot abort a fetch mid-flight) and a `GitEndpointProbe` (pure endpoint parsing + 5 s TCP probe) fails fast on unreachable remotes instead of hanging. Verified by `DeployToolkit.Core.Git.SelfTest` (67 checks) against real bare-origin + clone repos plus the pure credential-chain and probe math. `dotnet publish` invocation itself belongs to Phase 4. |
| 4 | Packager WinForms app (selection, stale-package prompt, diff preview, delta editor, DB scripts, build) | ✅ **Done** — `DeployToolkit.Packager` (WinForms, code-only UI over `DeployToolkit.AppKit`): 7-step wizard implementing §10 exactly — folder pick → git sync (branch/SHA/dirty decisions via `GitSyncPresenter`) → component resolve (`ComponentPickerDialog` on first-seen folders, mapping auto-registered) → stale-package resolution (`StalePackagesDialog`: Mark Deployed / Abandon / Ignore) → `dotnet publish` with streaming log + cancel (component-authoritative TFM/self-contained merged with the client's §19 publish defaults: RID + extra options) → diff preview vs last **Deployed** baseline with per-file include/exclude → appsettings delta grid → DB scripts (Schema/Data) → build (`PackageBuildRequest` with `GitCommitSha`, delta, scripts, **new optional `ExcludedPaths`**) + unresolved-stale warning; plus an **MDI-container shell** (user-driven change: screens open as in-app child windows) whose screen management is the fix4 single-front-screen policy — `ShellScreenPolicy` decides what closes on every switch (stateless screens replace; the in-progress wizard is pinned with a one-time discard prompt; one wizard at a time; Window menu + Close All Screens; busy/modal menu freeze; unsaved client edits guarded; connection-change consent) — and offline-result reconciliation (`OfflineReconciler`). Core diff engine unchanged; `ExcludedPaths` covered by 3 new Core.SelfTest checks (138 total); the MDI policy by 17 new AppKit.SelfTest checks (71 total) |
| 5 | Deployer WinForms app (IIS target) | ✅ **Done** — `DeployToolkit.Deployer` (WinForms, stage state machine per §11): load + integrity gate + manifest summary + registry package-row match (exact-manifest comparison; offline "record as new" fallback), target resolution (`IisTargetResolver` + candidate picker via `MicrosoftWebAdministrationController`), pre-flight checklist (disk space, paths, per-target option panels: Azure Kudu creds + `.publishsettings` loader + optional ARM settings, Plesk SFTP + restart mode), deploy run with live `RunLogger` pane — IIS via `DeploymentOrchestrator` + `IisDeploymentHooksFactory` (stop/start + DB hook with per-run connection prompt or SecretVault resolution), Azure via `AzureAppServiceExecutor` and Plesk via `PleskExecutor` + `SftpFileUploader` with audit recording identical to the orchestrator — result strip with rollback emphasis, offline results via `OfflineResultWriter`, **standalone rollback** menu item, per-run DB connection never persisted. Runtime is Windows-only by nature (compile-verified here) |
| 6 | IIS control + component/site-app picker | ✅ **Done (engine)** — `DeployToolkit.Core.IisControl`: `IIisController` abstraction, **`IisTargetResolver`** (machine-local `IisTargetMappingStore` → component config → live-verified candidates for the picker), **`IisSiteStopController`** (app-pool first, `app_offline.htm` fallback via `AppOfflineManager` — pure file I/O, testable headless), `IisDeploymentHooksFactory` in Core.Windows wiring it into `DeploymentHooks`. Real `MicrosoftWebAdministrationController` (MWA 11.1.0) in `DeployToolkit.Core.Windows`, compile-verified headless / runtime Windows-only. Picker UI itself belongs to the Deployer shell (Phase 5). |
| 7 | appsettings merge engine + diff preview UI | ✅ **Done** — engine (`AppSettingsMerger.Preview/Apply`; Azure path via `AzureAppSettingsClient.MergeDelta`) + UI: the Packager's delta step is a key/value grid (JSON-attempt values: numbers/bools/objects, `null` removes) and the Deployer shows the manifest delta and merges after the run's explicit confirm flow |
| 8 | DB script runner + connection UI | ✅ **Done** — `DeployToolkit.Core.Database` (Microsoft.Data.SqlClient 7.0.2): `GoBatchSplitter` (string/comment/bracket-aware, `GO n` repeat), `SqlScriptAnalyzer` (transaction-safety flags + advisory warnings per §8.4), provider-neutral `SqlScriptRunner` (batching, per-batch progress/rows/duration, transaction wrap + rollback, ContinueOnError outside tx), thin `SqlServerScriptRunner`. 59/59 SelfTest checks (splitter + analyzer + real execution against SQLite; SqlClient path compile-verified — no SQL Server in sandbox). Connection UI done (Deployer `DbScriptsConnectionPrompt` / SecretVault resolution, never persisted) |
| 9 | Rollback flow | ✅ **Done** — engine (`BackupManager` + orchestrator auto-rollback + appsettings restore) **and** standalone wizard integration: the Deployer's "Standalone Rollback…" menu picks a backup folder (default `Documents\Backups`) and restores with confirmation; the run result strip surfaces rolled-back runs with a jump to the backup folder |
| 10 | Package lifecycle (status tracking, mark-deployed, baseline rule, offline reconciliation) | ✅ **Done** — status enum incl. Superseded/Abandoned, "diff against last **Deployed**" rule, stale-package surfacing, MarkDeployed/MarkStatus on both stores, **plus the offline fallback round-trip: `OfflineResultWriter` (Deployer side, atomic `{packageId}.offline-result.json` + `.deploy.log`) and `OfflineReconciler` (Packager side: replays into the registry, flips Success→Deployed, keeps Failed/RolledBack redeployable, idempotent via `.reconciled` markers + registry-state double-check)** |
| 11 | Secrets encryption for registry credentials | ✅ **Done** — `DeployToolkit.Core.Secrets`: `AesGcmSecretProtector` (AES-256-GCM, passphrase mode with self-describing PBKDF2 payload — fresh process only needs the passphrase — or 32-byte key file; purpose bound as AAD so ciphertexts can't be repurposed), `SecretVault` (atomic JSON file, `vault://{name}` refs stored in `DbConnectionRef`, entry-name-bound purposes defeat ciphertext swapping), `DpapiSecretProtector` (Windows DPAPI, CurrentUser, in Core.Windows) |
| 12 | Azure App Service executor (Kudu + ARM Config API) | ✅ **Done (engine)** — `AzureAppServiceExecutor` in Core (zero NuGet: pure HttpClient; ARM auth via injectable `ArmTokenProvider` so Azure.Identity plugs in at the UI layer): `KuduClient` (zipdeploy POST, basic auth from publish credentials, latest-deployment query), `AzureAppSettingsClient` (GET/PUT full settings per ARM semantics + `MergeDelta` applying the manifest delta — null removes a key). 20 fake-handler checks (wire format, auth, zip content, settings merge). **Real-cloud smoke test is a user action** |
| 13 | Plesk executor (FTPS/SFTP + restart) | ✅ **Done (engine)** — `DeployToolkit.Core.Targets.Plesk` (SSH.NET 2026.0.0): `SftpFileUploader` behind `IPleskFileUploader` seam, `PleskExecutor` (POSIX path mapping, mkdir-once, guarded deletes, never leaves `app_offline.htm` behind), restart pluggable per plan §7 (`None` / `AppOffline` / `XmlApi` with packet template flagged for real-client validation — README checklist included). 60/60 fake-uploader checks. **Restart behavior + chroot paths need real-client validation (plan's least-certain item)** |
| 14 | Cross-environment testing | ☐ Not started |
| 15 | Code signing | ☐ Blocked on Phase 0 outcome |

Also done beyond the phase grid: `JsonFileProjectMappingStore` (plan §5
folder→component mapping), `IDeploymentExecutor` contract,
`ManifestHasher`/`ManifestDiffEngine` with forward-slash path
normalization, structured backup folder layout
`Backups/{yyyyMMdd}/{HHmm-component}/` with `backup-manifest.json`
(plan §4), JSON-lines `RunLogger` (§8.6, orchestrator-integrated),
`DotNetPublisher`, `PackageReader.ReadEntryText` (script preview /
zip-stream DB execution without extracting the package), the
**client & package management feature** (§19), and the **WinForms shell
layer** — `DeployToolkit.AppKit` (theme, Guard/BusyOverlay, registry
connection settings+factory, Clients screen, component/git/delta/log
dialogs; multi-targeted so its pure layer is headless-testable) consumed
by both `DeployToolkit.Packager` and `DeployToolkit.Deployer`.

## 19. Client & Package Management (added pre-WinForms, 2026-08-31)

Requested before the WinForms shell so the Clients screen has real data
and lifecycle control behind it. Implemented headless in
`DeployToolkit.Core.Registry` and mirrored by BOTH stores
(`EfCoreRegistryStore` + `LocalFileRegistryStore`) so online and offline
mode behave identically.

### Client profile (10 stored fields)

| # | Field | Column / property | Type & notes |
|---|---|---|---|
| 1 | Client name | `Name` | nvarchar(200), unique (case-insensitive) |
| 2 | Contact phone | `ContactPhone` | nvarchar(50), free-form |
| 3 | Contact e-mail | `ContactEmail` | nvarchar(255), light format check (must contain @, no spaces) |
| 4 | Git repository | `GitRepositoryUrl` | nvarchar(1024), must be an absolute URL |
| 5 | Deployment branch | `DeploymentBranch` | nvarchar(100), no spaces (git rule) |
| 6 | Deployment configuration | `PublishConfigurationJson` | nvarchar(max), canonical JSON via `PublishConfigurationSerializer` (enums as readable strings); typed accessor `Client.PublishConfiguration` = `PublishConfiguration { DeploymentType (FrameworkDependent/SelfContained), TargetRuntime (RID, e.g. win-x64), AdditionalPublishOptions (verbatim extra dotnet publish args) }`; maps 1:1 onto `DotNetPublisher.PublishSettings` via `ToPublishSettings(projectPath)` |
| 7 | Has AMC | `HasAmc` | bit |
| 8 | AMC expiry date | `AmcExpiryDate` | SQL `date` (DateOnly) |
| 9 | Infrastructure managed by | `InfrastructureManagedBy` | `ManagedBy` enum (Boxon / Client), stored as readable string |
| 10 | Hosting account managed by | `HostingAccountManagedBy` | nvarchar(200) free text ("Boxon", "Client", or a specific person/note) |

Plus the pre-existing `Notes`. Strings are trimmed and validated by
`Client.NormalizeAndValidate()` on every create/update — bad data can
never enter the registry through either store.

### New registry operations (on `IRegistryStore`, both stores)

| Operation | Semantics |
|---|---|
| `GetAllClientsAsync()` | All clients, case-insensitive name order (Clients screen list) |
| `UpdateClientAsync(client)` | Full-profile update; validates; refuses unknown ids and duplicate (case-insensitive) names with actionable messages |
| `DeleteClientAsync(clientId)` | Refused while the client still has components (audit trail is never cascade-deleted) |
| `GetPackageAsync(packageId)` | One package, any status, or null |
| `GetPackagesForComponentAsync(componentId)` | All packages any-status, newest first (package-management grid) |
| `DeletePackageAsync(packageId, deleteRunHistory=false)` | Refused while deployment-run records exist; `deleteRunHistory:true` removes package + runs explicitly (irreversible) |
| Flag-as-deployed | Existing `MarkDeployedAsync` / `MarkStatusAsync` — the UI lifecycle buttons call these directly |

EF migration `AddClientProfileFields` applies the new columns to an
existing registry with a plain `ALTER TABLE … ADD` (no data loss). The
deleted-package guard deliberately preserves the §9 baseline rule: the
"latest Deployed" pointer recomputes from the remaining records, and
deleting a deployed package is allowed but must be explicit in the UI.

### Verification

`DeployToolkit.EfCore.SelfTest` 48/48 (profile round-trip incl. DateOnly
+ enum + typed JSON accessor, rename/duplicate/unknown guards, all
validation rejections, delete rules, baseline intact after deletes) and
`DeployToolkit.Core.SelfTest` 135/135 (same rules against the file store
+ serializer/bridge tests). The WinForms Clients screen (Phase 4/5) is
now pure presentation: bind the grid to `GetAllClientsAsync()` /
`GetPackagesForComponentAsync()`, edit through `UpdateClientAsync()`,
wire buttons to `MarkDeployedAsync` / `MarkStatusAsync` /
`DeletePackageAsync` / `DeleteClientAsync`.

**Verification status:** full solution builds 0 errors / 0 C# warnings on
.NET 8 (the transient MSB3026 native-dylib copy retries are a sandbox
FUSE filesystem quirk, not code warnings; absorbed with
`-p:CopyRetryCount=60 -p:CopyRetryDelayMilliseconds=500`); self-tests:
Core 138, Database 59, Plesk 60, Git 67, EfCore 48, AppKit 71
(incl. the 17 new `ShellScreenPolicy` checks: stateless screens replace
each other on switch, the in-progress wizard is pinned, re-open
activates, one wizard at a time, close-all consent rules, null-argument
guards), Windows 9 (+3 skipped Windows-only) = **452 green**. The
WinForms shells (Packager/Deployer/AppKit's forms) are compile-verified
only in this sandbox — they run Windows-only, so the first visual run
happens on the dev machine (retarget net8.0-windows →
net10.0-windows, one line per csproj).

**Suggested next items:** first visual run of both shells on the dev
machine (retarget + `dotnet run` — for the MDI shell specifically: open
New Package → hop to Clients mid-wizard → Window menu → Close All
Screens → change the connection with a draft open); the two user-side
validations flagged above (real Azure app + real Plesk client); Phase
0's constraint checks (unsigned-exe policy, outbound DB from a client
RDP session, MWA access level — the last one now has a real UI path to
test through); then Phase 14 (cross-environment testing) and Phase 15
(code signing, if Phase 0 demands it).
