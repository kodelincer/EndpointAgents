# Adding a package (BAT or EXE)

This guide shows an administrator how to turn a vendor's BAT file into a package that
endpoints can run unattended. EXE files, files that need no input, and fallback
packages for the same problem are covered in
[General repair packages](#general-repair-packages-exe-files-no-input-runs-fallbacks). You need a PC with this repository and the .NET 10 SDK;
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

## General repair packages: EXE files, no-input runs, fallbacks

The steps above build an Office or Windows **activation** package (`schemaVersion: 1`,
`-Kind office` or `windows`). Every other fix is a **general repair package**
(`schemaVersion: 2`, the default `-Kind general`). It can hold a BAT or an EXE, and the
support agent picks it by the symptoms you list. It runs on the user's PC without asking
them, because you reviewed it.

```powershell
# A file that needs no answers: it just runs to the end.
.\scripts\new-package.ps1 -File C:\Vendor\SpoolerFix.exe -Id spooler-fix -NoInput
# A file with menus: leave out -NoInput and discover the screens as in step 3.
.\scripts\new-package.ps1 -File C:\Vendor\NetReset.bat -Id net-reset
```

A harmless worked example is checked in at
`deploy\control-plane\FileRepository\packages\no-input-repair-sample`.

What is different in a general `package.json`:

| Field | Meaning |
| --- | --- |
| `file.type`, `file.fileName`, `file.sha256` | `bat` or `exe`, the file in the folder, and its pinned hash. Endpoints stage it as `<id>.bat` or `<id>.exe`. |
| `file.publisher` | Optional. The exact signer subject of a signed EXE; endpoints then refuse the file unless Windows verifies that signature. `new-package.ps1` fills it in when the EXE is validly signed. |
| `symptoms` | 1-16 short phrases in the user's words (Persian is fine). The agent runs a package only when the user's problem matches them. |
| `problem`, `order` | Optional defaults. Packages with the same `problem` (for example `printing-stuck`) are alternatives, tried lowest `order` first (1-999, default 100). The agent asks the user whether each one worked and on a "no" runs the next; when none is left it hands the conversation to the helpdesk. **The skill decides first:** a "Problem guides" entry in `deploy/hermes/skills/repair-packages/SKILL.md` (or "Try order" in the Office and Windows skills) overrides this order, so you steer fallbacks by editing the skill, then running `scripts/sync-hermes-profile.ps1`. |
| `compatibility` | Optional `minWindowsVersion` / `maxWindowsVersionExclusive`. |
| `playbook.version: 3` | Allows the extra fields below. |
| `playbook.steps: []` | No input. Nothing is typed, input is closed at start (a stray `pause` or `set /p` returns at once), and the run waits for the file to end. |
| `playbook.arguments` | Up to 16 fixed arguments, such as `["/quiet", "/norestart"]`. Letters, digits, spaces and `- _ . / \ : = , + @ #` only; never passwords or keys. |
| `playbook.timeoutSeconds` | Overall limit for the whole run, 30-3600 (default 480). |
| `playbook.restartExitCodes` | Exit codes that mean "worked, restart needed", such as `[3010]`. The agent asks the user to restart; nothing restarts automatically. |

EXE rules: a **console** EXE may answer screens like a BAT. A **windowed (GUI)** EXE, which
includes most installers, may only run with `steps: []` and its silent-mode arguments. If
it opens a window anyway, the run stops after `guiDialogGraceSeconds`. An EXE without an
embedded signature is published with a warning; the SHA-256 pin still protects it.

Try it exactly as an endpoint would, then pin the hashes:

```powershell
.\scripts\test-bat-playbook.ps1 -Package <folder> -Execute
.\scripts\test-bat-playbook.ps1 -Package <folder> -Fix
```

## Legacy manifests

`office-bugfix-managers.json` and `Windows\windows-bugfix-managers.json` still load. If a
package and a manifest entry use the same id, the manifest entry wins and the package
shows as Invalid; move the entry into a package to resolve it.
