# DeployToolkit.Core.Windows

Windows-only implementations, isolated in one project so everything else
in the solution compiles and self-tests on any OS (including CI/sandboxes
without Windows).

## Contents

| Component | Plan | What it does |
|---|---|---|
| `DpapiSecretProtector` | §11/§12 | DPAPI (`CurrentUser`) flavor of `ISecretProtector` — ciphertexts bound to the Windows user on the machine that created them. Ideal for the operator's local secret vault; **not portable** across machines. Cross-platform alternative: `AesGcmSecretProtector` in DeployToolkit.Core. |
| `MicrosoftWebAdministrationController` | §6/§8.3 | The real `IIisController` over `Microsoft.Web.Administration` — site/app/app-pool enumeration, stop/start/recycle as library calls (never PowerShell). Requires IIS + IIS-config access rights. |
| `IisDeploymentHooksFactory` | §7 | Composition glue: builds the `DeploymentHooks` (stop/start + DB scripts) that `DeploymentOrchestrator` consumes for an IIS deploy. DB scripts are read straight out of the package zip (`db/*.sql`) and executed via the Phase 8 `SqlServerScriptRunner`. |

## Runtime requirements

- Windows with IIS installed (controller only — DPAPI just needs Windows).
- The running account needs IIS configuration access for
  stop/start/recycle. Accounts with filesystem-only rights still work:
  the Deployer selects the `app_offline.htm` strategy
  (`IisSiteStopController` with `AppOffline`, pure file I/O — see
  `DeployToolkit.Core`).
- `System.Security.Cryptography.ProtectedData` and
  `Microsoft.Web.Administration` are Windows-only **at runtime**; they
  compile anywhere, so CI builds of this project still succeed and the
  self-tests skip gracefully on non-Windows (`OperatingSystem.IsWindows()`
  guards).

## Retargeting

Keep on the same TFM as the rest of the solution (currently `net8.0`,
retarget to `net10.0-windows` on the dev machine along with the solution —
one-line change in each csproj, no package bumps).
