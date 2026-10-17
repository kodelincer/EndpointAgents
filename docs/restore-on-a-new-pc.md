# Continuing the project on a new PC

Everything needed to build, run and test the system is in this repository. This page lists what is
**not** in it, so a fresh Windows install can pick up where the last one stopped.

## 1. Get the code

```powershell
git clone https://github.com/kodelincer/EndpointAgents.git
cd EndpointAgents          # master = the WebView2 Tray line, same commit as claude/tray-webview
```

Everything below is pushed to `origin`. Branches that are **already contained in `master`** are
history only; branches **ahead of `master`** hold work that has not been merged yet.

### Branches ahead of master (unmerged work)

| Branch | What it holds |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |
| --- | --- |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |
| `claude/org-knowledge` | Newest line. Contains `claude/dashboard-auth` and `verify/endpoint-actions` plus the organization knowledge file, the masked `get_site_knowledge` tool and the site-knowledge skill. Start here to continue that work. |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |
| `verify/endpoint-actions` | Catalogued general endpoint actions (system and user side), their control-plane side, Hermes rules and symptom skills, dashboard Capabilities, Skills and Environment pages, helpdesk roles. Contained in `claude/org-knowledge`. |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |
| `claude/dashboard-auth` | Dashboard sign-in, helpdesk roles and the Operators page. Contained in `claude/org-knowledge`. |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |
| `claude/hermes-queue` | Admin-tunable request queue in front of Hermes. Branched from master; **not** contained in `claude/org-knowledge`, so merge it separately. |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |
| `claude/chrome-install-package` | Catalogued Chrome install package, software-install skill, a general installer packager and a Chrome settings reset. |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |
| `claude/package-runner` | Master plus one **WIP** commit: package signature, BAT lint and package contract changes for "generic file delivery and playbooks". Uncommitted when found, may not build or pass tests. |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |
| `wip/menu-repair-sample-local-changes` | Edits that were left uncommitted in a working tree: an EndpointInfo handler change, removal of two `tmpfiles` bug-fixer scripts and a stray `SupportWindow.xaml`. Review before merging. |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |
| `wip/stash-menu-repair-sample` | The contents of a local git stash ("menu-repair-sample local changes before tray-webview"), saved as a branch so they survive. Review before merging. |
| `appsettings.Development.json` (control plane and service) | `src\ControlPlane.Api`, `src\EndpointAgents.Service` | Checked in with localhost-only test keys, so they come with the clone. Real or production keys (LLM key, package signing public key list, AD settings, device tokens) are **not** in git: re-enter them on the new PC, see `docs/control-plane-operations.md`. |
| Windows/Office tooling state and logs | `%LOCALAPPDATA%\EndpointAgents` (`Reports`, `Tray`), `.local\*.log` | Per-machine runtime state; nothing to copy. |
| Uncommitted live data on the old PC | `.local\control-plane.db*` in the old checkouts (the DB is tracked, so the old copy was left modified, not committed) | If you need the old chats, runs, audit or operator accounts, copy `.local\control-plane.db` (and `-wal`, `-shm`, stopped first) from the old PC by hand. |

### Branches already contained in master (bookmarks)

`claude/tray-webview` (same commit as `master`), `claude/tray-redesign` (the WinUI Tray front end),
`claude/system-info`, `claude/package-catalog`, `claude/generic-bat-runner`,
`claude/menu-repair-sample`, `claude/setup-skills`. They can be ignored.

### Recreating the side checkouts (git worktrees)

The old PC used one worktree per branch next to the main clone. They are not needed to build or
run, only for working on several branches at once:

```powershell
cd EndpointAgents
git worktree add ..\EndpointAgents-actions  verify/endpoint-actions
git worktree add ..\EndpointAgents-auth     claude/dashboard-auth
git worktree add ..\EndpointAgents-orgknow  claude/org-knowledge
git worktree add ..\EndpointAgents-queue    claude/hermes-queue
git worktree add ..\EndpointAgents-chrome   claude/chrome-install-package
git worktree add ..\EndpointAgents-runner   claude/package-runner
git worktree add ..\EndpointAgents-sysinfo  claude/system-info
git worktree add ..\EndpointAgents-packages claude/package-catalog
git worktree add ..\EndpointAgents-tray     claude/tray-redesign
```

A branch can be checked out in only one worktree at a time, so give each branch its own folder or
switch with `git switch`.
## 2. Install the tools

- .NET 10 SDK (`dotnet --version` should say 10.x). Visual Studio is **not** needed.
- Node.js (current LTS or newer) and `npm`.
- Git.
- Microsoft Edge WebView2 Runtime (built into Windows 11; see `docs/tray.md` for Windows 10).
- Hermes agent runtime (`hermes` on PATH), see `docs/hermes-support-agent.md`.

## 3. Rebuild and test

```powershell
dotnet build EndpointAgents.slnx            # also builds the Tray UI (runs npm ci once)
dotnet test EndpointAgents.slnx
cd src\ControlPlane.Web; npm.cmd ci; cd ..\..
```

## 4. Things that live outside the repository

These are per-machine or secret on purpose; recreate them, do not look for them in git.

| What | Where it lives | How to recreate |
| --- | --- | --- |
| Hermes profile `endpointagents` (config, `.env`, skills, SOUL.md) | `%LOCALAPPDATA%\hermes\profiles\endpointagents` | `hermes profile create endpointagents --no-skills --no-alias`, then `.\scripts\sync-hermes-profile.ps1 -Provision` (see `docs/hermes-support-agent.md`). Re-run the sync after every change in `deploy\hermes`. |
| LLM provider API key | Hermes `.env` | Copied from the default Hermes profile by `-Provision`, or paste your own. Never commit it. |
| Dev device credential | `%LOCALAPPDATA%\EndpointAgents\dev-device-credential.bin` | Created when the PC enrolls; delete it if the control plane database is reset. |
| Package signing key (private) | Created by `.\scripts\new-package-signing-key.ps1` | Make a new one, add its public key to the two `appsettings.Development.json` files as `docs/run-dev-stack.md` describes, and re-sign packages. The development sample packages are signed with the checked-in dev key. |
| WebView2 offline installer | `deploy\endpoint\MicrosoftEdgeWebView2RuntimeInstallerX64.exe` (git-ignored) | Download the x64 Evergreen Standalone Installer from Microsoft; only needed for installs on PCs without WebView2. |
| Control plane dev data | `.local\control-plane.db*` | These files are tracked as a baseline; a live database from another PC is not part of the history. A new PC starts from the baseline and enrolls again. |

Design-tool state (`.impeccable\questions`) and build output (`bin`, `obj`, `ui\dist`, `node_modules`,
`.artifacts`) are git-ignored and rebuilt on demand.

## 5. Start it

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-dev-stack.ps1
```

This opens the control plane API (5052), the Hermes gateway (8642), the dashboard (3000) and the
endpoint service with the WebView2 Tray. See `docs/run-dev-stack.md` for the one-time setup and
troubleshooting, and `docs/tray.md` for the Tray architecture.

## 6. Where the work stands

- **Tray:** the WebView2 Tray is the default and the one-pipe service channel is in place. The WinUI
  Tray (`src/EndpointAgents.Tray`) is kept on purpose and runs with `-LegacyTray`; delete it only
  after the new Tray has been accepted.
- **Known failing test:** `BatPackageLoaderTests.CheckedInDevelopmentPackageIsReady` reports that
  `no-input-repair-sample.bat` no longer matches the SHA-256 pinned in its `package.json`.
  It predates the Tray work; re-pin the package with `.\scripts\test-bat-playbook.ps1 -Fix -Sign`
  after reviewing the file.
- **Live check (2026-10-13):** the full stack ran from this branch on a dev PC. The service accepted the WebView2 Tray's attach (it needs `TokenImpersonationLevel.Impersonation`; `Identification` makes the caller's token unreadable on the service side). The Hermes profile was synced and in sync.
- **Still to do:** run a real repair end to end through the new Tray's approval sheet, and delete the WinUI Tray once the new one is accepted.
