# Walkthrough: packaging a reseller's Office or Windows activation BAT

This takes one real BAT from your reseller, turns it into a signed package, and tests it
end to end on this PC: Tray chat, then the agent, then the repair, then "is it solved?".
Run every command from `D:\EndpointAgents-runner` in PowerShell. The general reference is
`docs\bat-packages.md`.

## Before you start

- The stack must be running (`docs\run-dev-stack.md`): control plane, Hermes, dashboard,
  and the endpoint service + Tray.
- `-Execute` and the end-to-end test really run the BAT on this PC.
- **Run the endpoint as administrator for real activation.** The development endpoint
  runs as you, not as LocalSystem. Activation commands (`slmgr`, `ospp.vbs`) need admin
  rights, so start the "Endpoint service + Tray" window from an elevated PowerShell.
- The repairs only run when they are needed. The Office repair refuses to run when Office
  already has healthy KMS activation, and the Windows repair refuses when Windows already
  reports Licensed. If this PC is already activated, the run stops with "already
  activated" or "already has healthy KMS"; use `-Execute` in the playbook lab to watch the
  BAT itself run.

## 1. Save the BAT

Put the reseller's file somewhere on this PC, for example `C:\Vendor\office-fix.bat`.
Read it first. By default the lint rejects BATs that elevate themselves (`runas`,
`-Verb RunAs`), run or decode code carried inside the file (`-EncodedCommand`,
`certutil -decode`), or download code. If the BAT does the first two (the service is
already admin and the code is inside the file your signature covers), accept those risks
for this package in step 4. Downloading code at run time can never be accepted.

## 2. Create the package folder

Office:

```powershell
.\scripts\new-package.ps1 -File C:\Vendor\office-fix.bat -Id office-reseller-fix -Kind office -Development
```

Windows: use `-Kind windows` and an id such as `windows-reseller-fix`.

Add `-NoInput` if the BAT just runs to the end without menus or questions. The folder
appears under `deploy\control-plane\FileRepository\packages\<id>\` with a draft
`package.json` full of `TODO` markers.

## 3. Find the screens (only for a BAT with menus or questions)

```powershell
.\scripts\test-bat-playbook.ps1 -Package .\deploy\control-plane\FileRepository\packages\office-reseller-fix -Execute -Discover
```

`Output tail` shows the next screen the BAT is waiting at. In `package.json`, set that
step's `expect` to a short, exact piece of the prompt text, `send` to the answer (for
example `1`), and `submit` to `line` (type and press Enter) or `key` (one key press).
Run the same command again: each round shows the next screen. Repeat until the BAT
finishes. Section 3 of `docs\bat-packages.md` shows a full example.

## 4. Fill in the rest of package.json

- `displayName`, `description`: what the repair does.
- `playbook.successMarker`: text that appears only when activation worked.
  `failureMarkers`: texts that mean it failed.
- Compatibility: the `office` block (`minOfficeVersion`, `maxOfficeVersionExclusive`,
  optional `officeProductReleaseIds`) or the `windows` block (`minWindowsVersion`,
  `maxWindowsVersionExclusive`, optional `editionIds`) must include the test PC. To see
  the installed Office version, run "Office inventory" from the dashboard Helpdesk tab.
- `review`: your name, the date, and where the BAT came from. If the BAT elevates itself
  or runs code encoded inside itself, add the risks you accept:
  ```json
  "review": {
    "reviewedBy": "Reza",
    "acceptedRisks": ["self-elevation", "embedded-payload"]
  }
  ```
  Use only the ones the BAT needs. The lint then reports them as warnings, the dashboard
  lists them under "Accepted risks", and `-Sign` signs them together with the file, so
  they cannot be added later without your key.
- Remove every `TODO`.

## 5. Pin the hashes and sign

```powershell
.\scripts\test-bat-playbook.ps1 -Package .\deploy\control-plane\FileRepository\packages\office-reseller-fix -Fix -Sign -KeyName EndpointAgents.DevPackageSigning
```

`EndpointAgents.DevPackageSigning` is the development key this PC's settings already
trust. For production, sign with your own key instead (`new-package-signing-key.ps1`, then
`-Sign` without `-KeyName`). Continue when the output says `Status: Ready`.

Optionally run it once more with `-Execute` to see the whole run answer itself and finish
with `Run state: Completed`.

## 6. Check the dashboard

Open http://127.0.0.1:3000, go to **Packages**, and refresh. The package must show
**Ready**. If it doesn't, the row says exactly what to fix. No restart is needed.

## 7. Tell the agent to try it

Edit the "Try order" list in `deploy\hermes\skills\office-support\SKILL.md` (or
`windows-support\SKILL.md`), and put your package id first:

```text
1. `office-reseller-fix`
2. `office-activation-repair`
```

Then copy the skills into Hermes and restart the gateway:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\sync-hermes-profile.ps1
hermes -p endpointagents gateway run
```

Close the old Hermes window first; the second command runs in the new window.

## 8. Test it end to end

1. On Office, close Word, Excel, Outlook and the other Office apps.
2. Open the Tray chat and write the problem the way a user would, for example
   «آفیس من فعال نیست» or «ویندوز فعال نشده».
3. The agent checks activation, runs your package, and asks «آیا مشکل شما حل شد؟».
4. Answer «نه» to watch the fallback: it runs the next repair in the Try order, and when
   none is left it hands the case to the helpdesk.
5. In the dashboard **Helpdesk** tab, open the conversation. The timeline shows each
   repair tried and its result; "Raw trace" holds the endpoint's full response, including
   the BAT's output tail when it failed.

## When something fails

| What you see | What it means |
| --- | --- |
| Status **Hash mismatch** | The BAT changed after `-Fix`. Review the change, then run `-Fix -Sign` again. |
| Status **Not signed** | Run `-Sign`, or trust the key you signed with. |
| Status **Rejected by lint** | The BAT elevates itself or downloads code; ask the reseller for a version that doesn't. |
| Run state **PromptTimedOut** | A step's `expect` text never appeared. Check `Output tail` and fix that step. |
| Run state **GuiDialogBlocked** | The BAT opened a window that needs a click; unattended runs cannot answer it. |
| "not compatible" | Widen the Office/Windows version range in `package.json`, then `-Fix -Sign`. |
| "already has healthy KMS" / "already activated" | Nothing to repair on this PC; use one that is not activated. |
