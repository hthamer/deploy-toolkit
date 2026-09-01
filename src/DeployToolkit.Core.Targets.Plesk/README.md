# DeployToolkit.Core.Targets.Plesk

Phase 13 — Plesk shared-hosting deployment target: an `IDeploymentExecutor`
implementation that uploads a package delta over **SFTP** and handles the
"restart the app" question via pluggable modes. Pure in-process .NET over
SSH.NET — **no scripts, no shell-outs, ever** (plan §1).

## What works today (self-tested)

- **SFTP upload** (`SftpFileUploader` over SSH.NET, package id `SSH.NET`,
  namespace `Renci.SshNet`): password or private-key auth, lazy connect,
  idempotent per-segment `mkdir`, overwrite-on-upload, delete support.
  Path contract: manifest paths are POSIX (`bin/App.dll`); local resolution
  uses platform separators; remote paths are `{RemoteRootPath}/{manifest path}`
  with forward slashes.
- **Deleted files**: removed from the server when present, skipped when not.
- **`app_offline.htm` mode** (`PleskAppOfflineHelper`): drop before upload,
  remove after — also guaranteed on every failure path (the site is never
  left in maintenance mode by this executor). Stops ASP.NET apps only.
- **Plesk XML API client** (`PleskXmlApiClient`): HTTP contract is
  `POST {panel}/enterprise/control/agent.php`, `Content-Type: text/xml`,
  HTTP Basic auth; 2xx + `<status>error</status>` in the body counts as
  failure. The restart packet is one public, one-line-swappable constant:
  `PleskXmlApiClient.DefaultRestartPacketTemplate` ({{SITE_ID}} placeholder).

## ⚠ Needs validation against YOUR Plesk clients (plan §7 checklist)

Restart behavior varies a lot by Plesk configuration. Before trusting any
non-`None` restart mode, validate:

- [ ] Does the subscription recycle the app automatically when files change?
      If yes → use `PleskRestartMode.None` and you're done.
- [ ] Is the app ASP.NET (Core/Framework)? If yes, `AppOffline` is the safest
      explicit restart: `app_offline.htm` stops the app during upload and its
      removal restarts it. Confirm the file is honored by your app pool
      (.NET Framework 4.8 and ASP.NET Core both honor it by default).
- [ ] For non-ASP.NET runtimes (PHP etc.): does a Plesk XML API call actually
      restart the site on your version? Open `https://your-host:8443/enterprise/control/agent.php`
      behavior manually / via the Plesk API reference for your Plesk version
      and confirm the exact operation.
- [ ] Replace `PleskXmlApiClient.DefaultRestartPacketTemplate` with the packet
      your Plesk version accepts (it's a `public const` — a one-line swap),
      and confirm Basic auth is accepted vs. an API-key header
      (`HTTP_PLESK_API_KEY` / `X-API-Key`) for your account type.
- [ ] Confirm the SFTP account's home maps where you expect: `/httpdocs`
      for the main subscription, `/subdomains/{name}/httpdocs`, or an
      absolute path if the account is chrooted differently.
- [ ] Confirm `SiteId` (used for the {{SITE_ID}} placeholder) matches the
      site's ID in Plesk (Websites & Domains → hosting settings / `plesk bin site --list`).

## Wiring credentials

- **SFTP (file transfer):** from the Plesk panel — *Websites & Domains →
  FTP Access* (or *SSH Access* for key auth). Host = the server hostname,
  port usually `22`. Fill a `PleskConnectionOptions`; prefer
  `PrivateKeyPath` over `Password` when the client allows keys.
- **XML API (restart):** use a dedicated Plesk user with the minimum role
  that can manage the subscription; its login/password go into
  `PleskDeployOptions.XmlApiLogin/XmlApiPassword`, panel URL (usually
  `https://host:8443`) into `XmlApiBaseUrl`, and the site's numeric ID into
  `SiteId`.
- Credentials come from the publish profile / secret store — this library
  never persists or logs them (the XML API result body is returned raw to
  the caller for display; Plesk does not echo credentials in it).

## Security note

- No scripts and no shell anywhere: SFTP protocol + HTTPS only, in-process.
- Read/write is limited to the SFTP account's scope (usually one
  subscription's `httpdocs`) — far narrower than RDP/IIS access.
- `app_offline.htm` content is static HTML; nothing user-supplied is written
  to the server.
