# Generalized package runner (BAT and EXE) - design proposal

Status: built on branch `claude/package-runner`. The admin guide is `docs/bat-packages.md`. Decisions taken:
general packages run without asking the user (every package is admin-reviewed), unsigned EXEs
publish with a warning, and packages for one `problem` are tried in `order`, with escalation to
the helpdesk when none solves it.

## Goal

The control plane delivers many kinds of reviewed files (BAT now, EXE next) and the endpoint
runs each one by following its pinned run plan. Some packages show a console menu or text
prompts (answered only from the playbook), and some need no input and just run to the end.

Rules that do not change:

- The AI picks **which package** fits the user's problem. It never chooses menu answers, arguments
  or files; those come only from the reviewed, hash-pinned `package.json`.
- The endpoint runs only published catalog packages whose file, supporting files and run plan
  match their SHA-256 pins. No arbitrary shell, no caller-provided arguments.
- Administrators build packages themselves with shipped tooling (script + playbook lab + dashboard).

## What exists today

- `FileRepository\packages\<id>\package.json` (schema 1) must say `office` or `windows`, and the
  package joins that bugfix catalog. Only `.bat` is accepted, and it is always run as
  `cmd.exe /d /s /c <bat>` as LocalSystem, hidden, with redirected stdin/stdout.
- Playbook v2 needs 1-32 steps, so a BAT with no prompts cannot be packaged.
- The overall run timeout is a fixed 8 minutes in `OfficeBugfixRunner`.
- The agent can reach a package only through the activation tools
  (`run_office_activation_repair`, `run_windows_activation_repair`), which pick from
  activation diagnostics. There is no general "find the package for this problem" path.

## Proposal

### 1. `package.json` schema 2 (schema 1 keeps loading unchanged)

```json
{
  "schemaVersion": 2,
  "id": "printer-spooler-reset",
  "displayName": "Reset the print spooler",
  "description": "Clears stuck print jobs and restarts the spooler.",
  "category": "general",               // general | office | windows
  "symptoms": [                        // what a user would say; used to match requests
    "Printing is stuck",
    "چاپگر چاپ نمی‌کند",
    "Print jobs stay in the queue"
  ],
  "file": { "type": "exe", "fileName": "SpoolerFix.exe", "sha256": "..." },
  "supportFiles": [],
  "playbook": {
    "version": 3,
    "arguments": ["/quiet", "/norestart"],   // fixed, literal, pinned
    "steps": [],                             // empty = no-input package
    "timeoutSeconds": 600,
    "successExitCodes": [0],
    "restartExitCodes": [3010],
    "successMarker": null,
    "failureMarkers": ["ERROR:"],
    "guiDialogGraceSeconds": 15
  },
  "playbookSha256": "...",
  "compatibility": { "minWindowsVersion": "10.0.17763" },  // or the existing office/windows blocks
  "review": { "reviewedBy": "...", "reviewedOn": "...", "source": "..." }
}
```

### 2. Playbook version 3 (versions 1 and 2 keep their exact hashes)

- `steps` may be empty. An empty playbook is a **no-input package**: stdin is closed at start, so a
  stray `pause` or `set /p` gets end-of-input and moves on instead of hanging.
- `arguments`: up to 16 fixed literal arguments, each at most 128 characters, no control
  characters, no `& | < > ^ %` and no secrets (same guard as responses). Passed with
  `ArgumentList`, never through a shell string. BATs still run through `cmd.exe /d /s /c`; EXEs
  are started directly.
- `timeoutSeconds`: overall run limit, 30-3600, default 480 (today's 8 minutes). Per-step
  timeouts stay as they are.
- `restartExitCodes`: exit codes that mean "succeeded, restart needed" (for example 3010). The
  result reports `Completed` with `restartRequired: true`; the endpoint never restarts the PC itself.
- Everything else (alternatives, optional/repeating steps, markers, GUI grace) is unchanged from v2.

### 3. EXE support

- Hash pin is mandatory, as for BATs.
- Publish-time checks (shown in the Packages tab):
  - PE header subsystem. A **console** EXE may use prompts. A **GUI-subsystem** EXE (most
    installers) may only be a no-input package; it must run silently via its fixed arguments.
    If it opens a visible window, the existing GUI-dialog detector stops it after the grace period.
  - Authenticode signature and publisher are shown. Optional `file.publisher` pin: when set, the
    endpoint refuses the EXE unless it is validly signed by that publisher (checked before launch).
- Runs as LocalSystem in the protected service staging folder, hidden, exactly like BATs today. EXEs
  that need the signed-in user's desktop are out of scope for this round.

### 4. Request-to-package mapping

Two new MCP tools, added to both the ControlPlane and Hermes allow-lists:

- `find_repair_packages(runCapability)` returns the published packages compatible with this device
  (filtered server-side by Windows/Office version from endpoint info): id, display name,
  description and symptoms only. The agent compares the user's words with the symptoms and picks
  one, or none.
- `run_repair_package(runCapability, packageId, reason)` pins the package server-side and creates a
  Tray approval card (the same consent flow as `propose_endpoint_action`). After the user approves,
  the endpoint runs the new `system.package_run` command and the agent gets a sanitized result
  (state, exit code, restartRequired, no output).

The existing activation tools keep working; `office`/`windows` packages still join those catalogs.
A new Hermes skill `package-support` says: list packages, match on symptoms, explain in one simple
Persian sentence, ask for approval through the tool, and if nothing matches say so and suggest
contacting the help desk. Synced with `scripts\sync-hermes-profile.ps1`.

### 5. Endpoint

- New `system.package_run` handler in `Platform.Windows`, built from the existing BAT runner:
  download + hash verify + protected staging (`BatchSupportFileDownloader`), playbook execution
  (`RunProcessAsync`, unchanged matching logic), plus the v3 additions above.
- The current Office/Windows runners keep their code paths; they gain v3 support for free because
  they share `RunProcessAsync`.

### 6. Tooling and dashboard

- `scripts\new-package.ps1 -File <bat|exe> -Id <id> -Category general|office|windows -NoInput`
  (the old `new-bat-package.ps1` stays as a thin wrapper). It writes the draft `package.json` with
  TODO markers, pins hashes, and reports the EXE subsystem and signer.
- Playbook lab (`test-bat-playbook.ps1`) accepts EXE packages and no-input packages, so
  `-Execute` shows the real exit code, restart flag and output tail before publishing.
- Packages tab: shows file type, interaction (menu / no input), timeout, symptoms, signer.
- Docs: `docs/bat-packages.md` becomes `docs/packages.md` with an EXE and a no-input walkthrough,
  plus a harmless sample of each (`deploy\samples`).

### 7. Tests

Playbook v3 normalization and hash stability (v1/v2 hashes unchanged), empty-step runs, closed
stdin, overall timeout, restart exit codes, argument validation, EXE subsystem and publisher checks,
schema 2 loader statuses, both MCP tools (allow-list, compatibility filter, consent card), and the
endpoint handler with the sample BAT and a sample console EXE.

## Open decisions for Reza

1. Should general packages always need the user's Tray approval? Recommended: yes, always.
2. Unsigned EXEs: allow with a dashboard warning (hash pin still required), or reject unless signed?
   Recommended: allow with a warning, and let the admin pin a publisher when the vendor signs.
