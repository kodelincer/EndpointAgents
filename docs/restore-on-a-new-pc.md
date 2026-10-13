# Continuing the project on a new PC

Everything needed to build, run and test the system is in this repository. This page lists what is
**not** in it, so a fresh Windows install can pick up where the last one stopped.

## 1. Get the code

```powershell
git clone https://github.com/kodelincer/EndpointAgents.git
cd EndpointAgents
git checkout claude/tray-webview     # or master, once it has been fast-forwarded to it
```

All earlier work lines are already contained in `claude/tray-webview` (it is one straight history
on top of `master`), so there is nothing to merge. The other branches are kept as bookmarks:

| Branch | What it holds |
| --- | --- |
| `claude/tray-webview` | Latest: WebView2 Tray, one-pipe service channel, installer and docs. |
| `claude/package-runner` | Repair-package runner, packages, system info (parent of the above). |
| `claude/package-catalog`, `claude/generic-bat-runner`, `claude/menu-repair-sample`, `claude/system-info`, `claude/setup-skills` | Earlier steps of the same line. |
| `claude/tray-redesign` | The redesigned WinUI Tray (the legacy front end, still buildable). |
| `wip/menu-repair-sample-local-changes` | Uncommitted edits that were left in a working tree: an EndpointInfo handler change, removal of two `tmpfiles` bug-fixer scripts and a stray `SupportWindow.xaml`. Review before merging. |

Old side checkouts (`EndpointAgents-runner`, `-tray`, `-sysinfo`, `-packages`) were git worktrees of
the same repository. They are not needed on a new PC; check out the branch you want instead, or run
`git worktree add ..\EndpointAgents-runner claude/package-runner`.

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
