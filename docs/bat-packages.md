# Curating BAT packages

A BAT package is one reviewed BAT file plus the metadata that lets the endpoint run it
unattended and lets Hermes pick it safely. The endpoint never improvises: it runs only
a catalogued BAT whose file hash and playbook hash match, and answers only the screens
the playbook lists. This guide describes how an administrator curates that metadata.
Feature specifics live in [office-bugfix.md](office-bugfix.md) and
[windows-activation.md](windows-activation.md).

## 1. Review the BAT first

Run the playbook lab without `-Execute`; it only reads the file:

```powershell
.\scripts\test-bat-playbook.ps1 -Bat .\my-repair.bat -Playbook .\my-repair.playbook.json
```

- **Lint errors** (elevation through `runas`/`-Verb RunAs`, encoded PowerShell, run-time
  downloads or decoding) keep the BAT out of the catalog.
- **Lint warnings** need a decision. Self-elevation or self-extraction code, `start`,
  `timeout /t`, GUI tools, and `%TEMP%` scripts usually mean the BAT cannot run
  unattended, or runs code the hash review never saw. Ask the vendor for a version
  without them, or ship extracted files as reviewed `supportFiles`.

Only package BATs whose provenance and licensing purpose you have confirmed.

## 2. Manifest entry

One entry per BAT in the catalog manifest (`office-bugfix-managers.json` or
`Windows/windows-bugfix-managers.json`):

| Field | Purpose |
| --- | --- |
| `artifactId` | Lowercase id; the file must be named `<artifactId>.bat`. |
| `displayName`, `description` | Shown to operators; describe what the repair does. |
| `sha256` | Exact hash of the reviewed file. Any byte change makes the package unavailable. |
| Compatibility | Office: `minOfficeVersion`, `maxOfficeVersionExclusive`, `officeProductReleaseIds`; Windows: version range and edition ids; optional `additionalCompatibility` rules. Hermes only sees packages compatible with the user's PC. |
| `supportFiles` | Optional flat files with their own hashes, placed under `<artifactId>.files/`. |
| `playbook` | The screens and answers below. |

`.bat` files are stored with CRLF line endings (see `.gitattributes`) so the hash is the
same in every checkout.

## 3. Playbook: screens and answers

Walk through the BAT once by hand and write one step per screen that needs input:

```json
{
  "version": 2,
  "steps": [
    { "expect": "Main menu choice:", "send": "1", "submit": "line", "timeoutSeconds": 30 },
    { "expect": "Press any key", "send": "x", "submit": "key", "timeoutSeconds": 30,
      "optional": true }
  ],
  "successMarker": "Task completed successfully",
  "failureMarkers": ["FAILED", "Invalid choice"]
}
```

- `expect` is a short, stable, literal piece of the prompt text (no regular expressions).
- `send` is the fixed answer; `submit: "line"` adds Enter, `submit: "key"` sends one
  character. Answers never contain credentials, tokens, or product keys.
- Use `alternatives` when a step can show different prompts, `optional` for screens that
  appear only sometimes, and `maxRepeats` for screens that repeat (notice pages).
- Use `successMarker` for the line that proves the job worked, and `failureMarkers` for
  error lines and known surprise prompts; they stop the run at once.
- `successExitCodes` lists accepted exit codes (default `[0]`).

The full field reference is in [office-bugfix.md](office-bugfix.md#playbook-version-2),
and `deploy/samples/menu-scenarios-sample.playbook.json` is a complete example.

## 4. Build and test with the playbook lab

```powershell
.\scripts\test-bat-playbook.ps1 -Bat .\my-repair.bat -Playbook .\my-repair.playbook.json `
    -Execute -Discover
```

`-Execute` really runs the BAT as the current user, the way the endpoint would answer
it. `-Discover` prints the screen that follows your last step, so you can add steps one
screen at a time until the run reports `Completed`. The lab prints the BAT hash, the
playbook hash, and a manifest entry draft to copy into the catalog.

## 5. Windows that need a mouse

The endpoint service runs BATs in a hidden session where no one can see or click a
window. If a window from the BAT's process tree stays visible for the playbook's
`guiDialogGraceSeconds` (default 15, range 5–600), the run stops as `GuiDialogBlocked`
instead of waiting for the step timeout. Clicking dialogs automatically is not supported.

The grace period is a trade-off. A BAT that legitimately shows a progress window for
longer (for example an installer run with `/passive`) is stopped by mistake unless its
playbook raises `guiDialogGraceSeconds`; a longer value makes a real dialog take longer
to report.

The best fix for a BAT with dialogs is a silent or unattended switch from its vendor
(many tools accept `/silent`, `/s`, or `/quiet`), so that it needs no window at all.

## 6. Publish

1. Copy the BAT (and any `supportFiles`) into the protected file repository.
2. Add the manifest entry and check the dashboard's catalog issues for lint findings.
3. Update the relevant Hermes skill in `deploy/hermes/skills` if the agent needs a rule
   for choosing between packages, and add tests (see `CLAUDE.md`).
4. Pilot on one test endpoint before rollout.
