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

## Steps

You need the repository and the .NET 10 SDK on your admin PC. Run these from the repository root.

1. **Create the folder.** `scripts\new-package.ps1 -File C:\Vendor\Fix.exe -Id spooler-fix -NoInput` (leave out `-NoInput` for a file with menus). It copies the file, records its SHA-256, and writes a draft `package.json` full of `TODO`. The file is not run.
2. **Discover the screens** (only for files with menus). `scripts\test-bat-playbook.ps1 -Package <folder> -Execute -Discover` really runs the file as you and stops at the next prompt. Add one playbook step per screen: a short stable `expect` piece, a fixed `send` answer, `submit: line` or `key`, and a timeout.
3. **Describe it.** Fill in `displayName`, `description`, `symptoms` (1 to 16 phrases in the user's words, Persian is fine), `problem` and `order` if it is one of several alternatives, `compatibility`, and `review` (who reviewed it, when, and where the file came from). Remove every `TODO`.
4. **Set the finish rules.** `successExitCodes`, a `successMarker` text that only appears on success, `failureMarkers`, and `restartExitCodes` such as `[3010]` (the agent asks the user to restart; nothing restarts automatically).
5. **Test.** `scripts\test-bat-playbook.ps1 -Package <folder> -Execute` should end with `Status: Ready` and `Run state: Completed`.
6. **Pin and sign.** `scripts\test-bat-playbook.ps1 -Package <folder> -Fix -Sign`. `-Fix` writes the current hashes into `package.json`; use it only after reviewing what changed.
7. **Publish.** Copy the whole folder into the server's `C:\ProgramData\EndpointAgents\ControlPlane\FileRepository\packages\` (an administrator-only folder). No restart is needed.
8. **Pilot.** Refresh **Packages**, check the status is Ready, run it on one test PC, then on a few users. If the agent needs a rule for choosing between packages, edit the `repair-packages` skill.

### Rules for the playbook

- A **BAT** or a **console EXE** may answer screens. A **windowed (GUI) EXE**, which includes most installers, may only run with `steps: []` and its silent switches. A window that stays open past `guiDialogGraceSeconds` (15 by default) stops the run.
- `arguments`: up to 16 fixed values (letters, digits, spaces and `- _ . / \ : = , + @ #`), never passwords or keys.
- `timeoutSeconds`: 30 to 3600. `send` answers are at most 64 characters and never secrets.
- An **EXE** must carry a valid vendor (Authenticode) signature, and `file.publisher` pins the exact signer subject. Endpoints refuse it unless Windows verifies that signature.

## Statuses on the Packages page

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
