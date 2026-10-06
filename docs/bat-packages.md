# Adding a BAT package

This guide shows an administrator how to turn a vendor's BAT file into a package that
endpoints can run unattended. You need a PC with this repository and the .NET 10 SDK;
the control plane server only receives the finished package folder.

A **package** is one folder:

```text
FileRepository\packages\<id>\
    package.json        what the BAT is, its hashes, its playbook and compatibility
    <any name>.bat      the reviewed BAT, unchanged
    files\              optional supporting files the BAT needs
```

The endpoint never improvises. It runs a BAT only when the file, its supporting files,
and its playbook all match the hashes in `package.json`, and it answers only the
screens the playbook lists. The Packages tab of the dashboard shows each package's
status and exactly what to fix.

The worked example below uses the harmless mock
`deploy\samples\menu-repair-sample.bat`. It prints a repair menu and changes nothing.

## 1. Place the file

Put the BAT somewhere on your admin PC. Only package BATs from a trusted source whose
purpose you understand and are licensed to use.

## 2. Create the package folder

```powershell
.\scripts\new-bat-package.ps1 -Bat .\deploy\samples\menu-repair-sample.bat `
    -Id menu-repair-sample -Kind office -Development
```

- `-Id`: lowercase letters, numbers, `.`, `_`, `-`; it becomes the folder name.
- `-Kind`: `office` or `windows`, which decides the catalog and compatibility fields.
- `-Development` uses the repository's development `FileRepository`. Without it the
  folder is created under `C:\ProgramData\EndpointAgents\ControlPlane\FileRepository`.

The script copies the BAT, records its SHA-256, writes a draft `package.json` full of
`TODO` markers, and validates the folder. The status is `Invalid` until every `TODO`
is replaced. The BAT is not run.

## 3. Discover the screens, one round at a time

```powershell
.\scripts\test-bat-playbook.ps1 -Package .\deploy\control-plane\FileRepository\packages\menu-repair-sample -Execute -Discover
```

`-Execute` really runs the BAT as you, answering exactly as an endpoint would.
`-Discover` stops after your last playbook step and prints the next screen as
`Output tail`. In the first round the only step still waits for its `TODO` text, so the
tail shows the first screen:

```text
Run state:        PromptTimedOut
Output tail:      ... ============== MAIN MENU ============== [1] Office repair ...
                  [6] Exit ======================================= Select an option [1-6]:
```

Put a short, stable piece of that prompt in the first step and choose the answer,
then run the same command again to see the next screen. Repeat until the BAT finishes.
For the mock that gives six steps:

| Screen ends with | Answer |
| --- | --- |
| `Select an option [1-6]:` | `1`, Enter |
| `Select an option [1-8]:` | `1`, Enter |
| `[A]pps [S]ettings [B]oth :` | the key `B` |
| `Press any key to continue` | any key, e.g. `x` |
| `Select an option [1-8]:` | `8`, Enter (back to the main menu) |
| `Select an option [1-6]:` | `6`, Enter (exit) |

Each round really runs the BAT up to the screen where it stops, so for a BAT that
changes something, every round repeats that change.

## 4. Write the playbook

The playbook lives in `package.json`. The mock's complete playbook is
`deploy\samples\menu-repair-sample.playbook.json`. Each step is:

```json
{ "expect": "Select an option [1-6]:", "send": "1", "submit": "line", "timeoutSeconds": 60 }
```

- `expect`: a short piece of the prompt, copied from the output tail (colours are
  removed and line breaks become single spaces). Avoid dates, versions and PC names.
- `send`: the fixed answer, at most 64 characters, never a password, token or key.
- `submit`: `line` types the answer and presses Enter; `key` sends one character.
- `timeoutSeconds`: 5–300. The first screen also gets 30 seconds of start-up time.
- `optional: true`: the screen may not appear (for example a "Press any key" that is
  shown only sometimes).
- `maxRepeats`: for a screen that repeats, such as notice pages.
- `alternatives`: other prompts the same step may show, each with its own answer.

## 5. Write the finish rules

```json
"successExitCodes": [0],
"successMarker": "Repair completed successfully",
"failureMarkers": ["Repair failed", "Option not available"]
```

- `successMarker`: text that only appears when the job worked. Without it, exit code 0
  alone counts as success.
- `failureMarkers`: error lines or unexpected prompts. Any of them stops the run at
  once as failed.
- `guiDialogGraceSeconds` (default 15): endpoints run BATs in a hidden session where
  nobody can click a window. A window that stays open this long stops the run as
  `GuiDialogBlocked`. Raise it only for a BAT with a legitimate long progress window.
  The best fix for a BAT with dialogs is a silent switch from its vendor (`/silent`,
  `/s`, `/quiet`).

## 6. Set compatibility and review

For `office` packages:

```json
"office": {
  "minOfficeVersion": "16.0", "maxOfficeVersionExclusive": "17.0",
  "officeProductReleaseIds": ["ProPlus2021Volume"]
}
```

Office 2016 to 2024 all report version 16.0, so list the Click-to-Run product IDs the
BAT really supports; an empty list means every product in the range. Optional
`minWindowsVersion` / `maxWindowsVersionExclusive` and `additionalCompatibility` work
as in [office-bugfix.md](office-bugfix.md).

For `windows` packages use `"windows": { "minWindowsVersion", "maxWindowsVersionExclusive", "editionIds" }`.

Fill in `displayName`, `description`, and `review` (`reviewedBy`, `reviewedOn`,
`source`, `notes`).

## 7. Validate and record the hashes

```powershell
.\scripts\test-bat-playbook.ps1 -Package <folder> -Fix -Execute
```

`-Fix` writes the current BAT, supporting-file and playbook hashes into
`package.json`; use it only after you reviewed what changed. Then the package is
checked with the control plane's own rules. Every line starting with `FIX` names a
field to correct; `WARN` lines are lint warnings that do not block publishing. You are
done when it prints:

```text
Status:           Ready
Run state:        Completed
```

What the statuses mean:

| Status | Meaning |
| --- | --- |
| Ready | Offered to compatible endpoints. |
| Ready with lint warnings | Offered; read the warnings. |
| Hash mismatch | The BAT or a supporting file changed after review. |
| Invalid playbook | The playbook breaks a rule, or changed without a new `playbookSha256`. |
| Rejected by lint | The BAT requests elevation, runs encoded PowerShell, or downloads code. |
| Invalid package | `package.json` is unreadable, still has `TODO`, or its `id` differs from the folder. |

## 8. Publish to the control plane

Copy the whole package folder into the server's
`C:\ProgramData\EndpointAgents\ControlPlane\FileRepository\packages\` (an
administrator-only folder). No restart is needed: refresh the Packages tab and check
that the package is Ready. Any later change to the BAT or playbook needs the same
review, `-Fix` and copy again.

## 9. Test on one PC, then pilot

Run the repair on one test endpoint first and confirm the expected result. Then pilot
it on a few users before relying on it. If the agent needs a rule for choosing between
packages, update the relevant Hermes skill in `deploy\hermes\skills`.

## Legacy manifests

`office-bugfix-managers.json` and `Windows\windows-bugfix-managers.json` still load. If a
package and a manifest entry use the same id, the manifest entry wins and the package
shows as Invalid; move the entry into a package to resolve it.
