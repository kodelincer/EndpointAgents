---
title: Adding and signing packages
summary: Turn a vendor BAT or EXE into a reviewed, hash-pinned and signed package the agent can run unattended.
order: 9
---

# Adding and signing packages

A **package** is how a vendor BAT or EXE becomes something the agent may run. You review the file once; endpoints then run only that exact file.

```text
FileRepository\packages\<id>\
    package.json        what it is, hashes, playbook, compatibility, signature
    <name>.bat | .exe   the reviewed file, unchanged
    files\              optional supporting files
```

The endpoint never improvises. It runs the file only if the file, the supporting files and the playbook all match the hashes in `package.json`, the signature matches a trusted key, and it answers only the screens the playbook lists.

## Two kinds

| Kind | `schemaVersion` | Use |
| --- | --- | --- |
| Activation (`office` or `windows`) | 1 | Windows or Office activation repairs, chosen by the dedicated activation tools |
| General | 2 | Everything else. The agent picks it when the user's problem matches its `symptoms`, and it runs without asking the user. |

## The easy way: the Package builder page

For a BAT or a vendor installer you do not need the scripts or a text editor. Open **Package builder** in the dashboard (or **Build a package** on Packages and files):

1. **Upload the BAT.** The control plane only reads its text, it never runs it. It lists the menus, questions (`set /p`), one-key choices (`choice`) and pauses it finds, with the lines the BAT prints just before each one, the answers the menu offers and where each answer jumps (`:label`). Lint problems show right away.
2. **Describe it.** Pick Office activation, Windows activation or General repair, then the id, the name and what it does. For a general repair add the ways users describe the problem.
3. **Add your answers.** Click the answer you want under a found screen to add it as a step, or add a step by hand. The builder never picks an answer by itself. Check the order and the exact screen text against a real run on a test PC when you are not sure, because a BAT that builds its menus at run time can show screens the text does not reveal.
4. **Set how to tell it worked** (success text, failure texts, exit codes) and **which PCs it fits**, then **Create the package**.

The control plane writes `packages\<id>\` with the BAT unchanged and a `package.json` with the file and playbook hashes already pinned, exactly as `new-package.ps1` plus `-Fix` would. The package is saved as a **draft** (Not signed), so no PC gets it yet. You can try it on a test PC first with `test-bat-playbook.ps1 -Package <folder> -Execute`.

### Approve and sign (second person)

On **Packages and files**, a draft shows **Waiting for approval** and who saved it. An admin who did **not** save it opens **Show details**, checks the steps, and clicks **Approve and sign**. The control plane then signs exactly that revision with its own key, and the package becomes Ready. If the package changed after the admin opened the page, the approval is refused and they must look again. Saving the package again in the builder removes the approval.

The control plane's key is a non-exportable Windows key created on its first start, in the profile of the account it runs as. Its public key is shown at the top of Packages and files for admins. **Each PC must trust it once**, for packages only (never for agent updates): add it to `PackageSigning:ApprovalPublicKeys` in the endpoint settings, or install PCs with `install-endpoint.ps1 -ApprovalSigningPublicKey <key>`. Settings: `PackageApproval:Enabled` (default on) and `PackageApproval:RequireSecondPerson` (default on; switch it off only if your team has a single admin). Signing by hand with `-Sign` keeps working as before.

To change a package, use **Change in the Package builder** on its row, or pick it at the top of the builder; you can keep its BAT or installer, or upload a new one. Any helpdesk member may create and change unsigned packages. Changing a signed package needs an admin, and saving it removes the signature, so it leaves the PCs until it is approved again.

**Supporting files.** Under the BAT, **Add supporting files** uploads files the BAT uses while it runs, such as a tool, a CAB or a text file it shows as a menu. They go in the package's `files\` folder, and the control plane works out each SHA-256 and pins it in `package.json`, so a PC refuses the package if any file changes. Remove a file with its bin button. Menus found in `.txt`, `.ini`, `.cmd` and `.bat` supporting files are added to the suggestions, marked with the file they came from; the screen text the BAT waits at is often in the BAT itself, so check it on a test run. Content a BAT downloads while it runs is always refused: put the file in `files\` instead.

### Install packages (MSI or EXE)

Pick **Install software** at the top of the builder to make an install package from a vendor installer, with no script:

1. **Upload the .msi or .exe.** Large installers are fine; the file goes straight to the server and is never run there. The page shows its vendor signature (an unsigned installer cannot be saved, because PCs refuse it), its installer type (MSI, Inno Setup, NSIS, WiX bundle, InstallShield or Squirrel, found the same way as `new-install-package.ps1`) and, for an EXE, its product name. It fills in the id (ending in `-install`), the name, a description and two request phrases as suggestions.
2. **Check how it installs.** The type picks the silent switches; you can add extra switches, or MSI properties such as `ALLUSERS=1`. For an unknown EXE, type the vendor's silent switches.
3. **Say how to tell it worked:** the name the app shows in Apps & features (Settings > Apps), without version numbers. The PC checks it before installing (already there?) and after.
4. **Add the requirements** from the vendor's page (Windows build, memory, free space, processor, runtimes, domain) and mark **Licensed** software, which always waits for a helpdesk approval.

The package is the same as `new-install-package.ps1 -Typed`: the installer is the package file, pinned by hash and by its signer, with the signed `software` section. It is a draft until a second admin approves it, like any other package. Installers that need recorded wizard answers (`-RecordAnswers`) or cannot run silently (`-Manual`) still use the script.

## Steps (by hand)

You need the repository and the .NET 10 SDK on your admin PC. Run these from the repository root.

1. **Create the folder.** `scripts\new-package.ps1 -File C:\Vendor\Fix.exe -Id spooler-fix -NoInput` (leave out `-NoInput` for a file with menus). It copies the file, records its SHA-256, and writes a draft `package.json` full of `TODO`. The file is not run.
2. **Discover the screens** (only for files with menus). `scripts\test-bat-playbook.ps1 -Package <folder> -Execute -Discover` really runs the file as you and stops at the next prompt. Add one playbook step per screen: a short stable `expect` piece, a fixed `send` answer, `submit: line` or `key`, and a timeout.
3. **Describe it.** Fill in `displayName`, `description`, `symptoms` (1 to 16 phrases in the user's words, Persian is fine), `problem` and `order` if it is one of several alternatives, `compatibility`, and `review` (who reviewed it, when, and where the file came from). Remove every `TODO`.
4. **Set the finish rules.** `successExitCodes`, a `successMarker` text that only appears on success, `failureMarkers`, and `restartExitCodes` such as `[3010]` (the agent asks the user to restart; nothing restarts automatically).
5. **Test.** `scripts\test-bat-playbook.ps1 -Package <folder> -Execute` should end with `Status: Ready` and `Run state: Completed`.
6. **Pin and sign.** `scripts\test-bat-playbook.ps1 -Package <folder> -Fix -Sign`. `-Fix` writes the current hashes into `package.json`; use it only after reviewing what changed.
7. **Publish.** Copy the whole folder into the server's `C:\ProgramData\EndpointAgents\ControlPlane\FileRepository\packages\` (an administrator-only folder). No restart is needed.
8. **Pilot.** Refresh **Packages and files**, check the status is Ready, run it on one test PC, then on a few users. If the agent needs a rule for choosing between packages, edit the `repair-packages` skill.

### Rules for the playbook

- A **BAT** or a **console EXE** may answer screens. A **windowed (GUI) EXE**, which includes most installers, may only run with `steps: []` and its silent switches. A window that stays open past `guiDialogGraceSeconds` (15 by default) stops the run.
- `arguments`: up to 16 fixed values (letters, digits, spaces and `- _ . / \ : = , + @ #`), never passwords or keys.
- `timeoutSeconds`: 30 to 3600. `send` answers are at most 64 characters and never secrets.
- An **EXE** must carry a valid vendor (Authenticode) signature, and `file.publisher` pins the exact signer subject. Endpoints refuse it unless Windows verifies that signature.

## Statuses on the Packages and files page

| Status | Meaning |
| --- | --- |
| Ready | Offered to compatible endpoints. |
| Ready with lint warnings | Offered; read the warnings. |
| Not signed | No trusted key matches the signature. |
| Hash mismatch | The file or a supporting file changed after review. |
| Invalid playbook | The playbook breaks a rule, or changed without a new `playbookSha256`. |
| Rejected by lint | The file requests elevation, runs encoded or embedded code, or downloads code. |
| Invalid package | `package.json` is unreadable, has a `TODO`, or its `id` differs from the folder. |

## Signing

Every package must be signed before the control plane publishes it and before an endpoint runs it. The signature (ECDSA P-256 with SHA-256) covers the package ID, the file hash, the playbook hash, the supporting files and the EXE publisher. Any change after review needs a new signature.

The private key stays on your admin PC and cannot be exported. The control plane and the endpoints only know the public key, so even someone who can change files on the server cannot make an endpoint run them.

1. **Once:** `scripts\new-package-signing-key.ps1` prints a public key (`-Show` prints it again).
2. **Trust it** on the control plane (`FileRepository:TrustedPackageSigningKeys`, or `install-control-plane.ps1 -PackageSigningPublicKey <key>`) and on endpoints (`PackageSigning:TrustedPublicKeys`, or `install-endpoint.ps1 -PackageSigningPublicKey <key>`).
3. **After each review:** run the pin-and-sign step above.

Several keys can be trusted at once, for a second administrator or a replacement key if a PC is lost. General packages always need a valid signature on the endpoint. Activation repairs need one when the endpoint sets `PackageSigning:RequireForActivationRepairs` (installer switch `-RequireSignedActivationRepairs`); turn that on once no legacy manifest entries remain, because they cannot be signed.

## Accepted risks (self-elevating and self-contained BATs)

Vendor BATs often relaunch themselves as administrator (`runas`, `-Verb RunAs`) or carry code encoded inside the file (`-EncodedCommand`, `certutil -decode`). The service is already administrator and code inside the file is covered by its hash and your signature, so you may accept both for one package:

```json
"review": { "reviewedBy": "Your name", "acceptedRisks": ["self-elevation", "embedded-payload"] }
```

Only those two values exist. They are signed with the package, so adding one later without signing again makes the package **Not signed**. **Downloading code at run time is never accepted**; ship the file in `files\` instead. Accepting a risk does not make the file safe, so read it first.

## Legacy manifests

`office-bugfix-managers.json` and `Windows\windows-bugfix-managers.json` still load. If a package and a manifest entry share an ID, the manifest wins and the package shows Invalid. Move the entry into a package to resolve it.

For a full worked example see `docs/bat-packages.md` and `docs/activation-package-walkthrough.md`.

## Requirements, approval and typed installers

An install package can carry a signed `software` section. It is part of what you sign, so
nobody can change it later without breaking the signature.

**What happens when a user asks for an app** (no AI decides any of it):

1. The PC checks the requirements first: Windows build, edition, 32/64-bit, memory, free
   space on C:, processor cores, screen size, domain, runtimes such as .NET or the Visual C++
   runtime, and apps that must not be installed. If one fails, nothing is asked or installed
   and the user is told why in simple words.
2. Already installed: the user is told so.
3. A missing runtime that the catalog has (a package with `provides`) is installed first.
4. **Approval.** Every install waits on the Approvals page, where the card shows each
   requirement the PC passed. An admin can switch a package to "No approval needed" on the
   Packages and files page. Chrome, the Persian fonts, Foxit Reader, Adobe Connect and Photos start on
   that list. Licensed software can never be on it.
5. The PC checks the requirements again, installs silently, and checks it really is
   installed. Exit 3010 or 1641 means "worked, restart needed"; 1618 means another install
   is running, so the user is asked to try again later.

**Make one** on the Package builder page (see "Install packages" above), or with `scripts\new-install-package.ps1`:

```powershell
.\scripts\new-install-package.ps1 -Installer C:\Vendor\app.msi -Id app-install -App "App" `
    -Typed -MinWindowsBuild 19041 -MinMemoryGB 8 -MinFreeDiskGB 4 -Runtimes webview2
```

- `-Typed` runs the MSI, MSIX or EXE itself with the fixed silent switches for its kind
  (no BAT). Without it you get the BAT template as before; the requirements still apply.
- `-Manual` is for an installer that cannot run silently: the user's request becomes a
  helpdesk task on the Approvals page.
- `-Licensed` always asks for approval. `-Provides dotnet-desktop-8` marks a runtime package.
- A ZIP can be a `copy-files` package (fonts or a portable app); paths that leave the
  target folder are refused.

The control plane and the endpoint both refuse a section with quotes or control characters
in switches, a REBOOT property other than ReallySuppress, or a typed installer without a
detection rule.
