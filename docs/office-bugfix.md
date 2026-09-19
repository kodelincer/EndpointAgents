# Version-aware Office activation repairs

Office activation BAT files are administrator-managed in:

```text
C:\ProgramData\EndpointAgents\ControlPlane\FileRepository
```

Execution authority comes from the protected manifest:

```text
C:\ProgramData\EndpointAgents\ControlPlane\FileRepository\office-bugfix-managers.json
```

Fresh installations create an empty manifest and do not install a sample repair. An
artifact is available only when its BAT exists, its manifest entry is valid, and its
current SHA-256 equals the pinned manifest hash.

## Manifest

The artifact ID must contain lowercase letters, numbers, dots, underscores, or hyphens.
The filename must be that exact ID plus `.bat`. Version ranges include the minimum and
exclude the maximum.

```json
{
  "version": 1,
  "artifacts": [
    {
      "artifactId": "office-activation-repair",
      "displayName": "Office activation repair",
      "description": "Administrator-reviewed Office activation repair.",
      "fileName": "office-activation-repair.bat",
      "sha256": "REPLACE_WITH_64_HEXADECIMAL_CHARACTERS",
      "minOfficeVersion": "16.0",
      "maxOfficeVersionExclusive": "17.0",
      "playbook": {
        "version": 1,
        "steps": [
          {
            "expect": "Select an item",
            "send": "1",
            "submit": "line",
            "timeoutSeconds": 30
          },
          {
            "expect": "Continue with harmless sample? [Y/N]",
            "send": "Y",
            "submit": "key",
            "timeoutSeconds": 30
          }
        ]
      }
    }
  ]
}
```

The BAT contract is deliberately narrow. Its playbook contains 1–32 ordered steps. Each
step waits for a literal prompt fragment and sends only its administrator-approved fixed
response. `submit: "key"` requires exactly one printable character and does not send
Enter; `submit: "line"` sends up to 64 printable characters followed by Enter. Step
timeouts must be 5–300 seconds. Responses cannot contain control characters,
credentials, tokens, secrets, or product keys.

The control plane normalizes and hashes the complete ordered playbook. Action approval
pins the filename, BAT SHA-256, and playbook SHA-256, so a meaningful prompt, response,
submission mode, timeout, order, or version change requires fresh authorization. The
endpoint verifies both hashes independently before launch.

The endpoint starts hidden `System32\cmd.exe` with redirected input, output, and error
streams. It reads output in chunks because prompts need not end in a newline, removes
ANSI formatting and nonsemantic control characters, and matches each step only against
output received after the prior response. Status text is retained but ignored unless it
contains the approved literal prompt. Output is capped at 1 MiB. The process must finish
all steps and terminate with exit code `0`; the service stops it after eight minutes,
before the endpoint command's ten-minute outer timeout.

## Runtime flow

1. Hermes calls `office_inventory` and the KMS-only `office_activation_status`.
2. If OSPP reports that every Office product is licensed through `Volume|KMS` with no
   error, Hermes reports that Office is healthy and stops. No repair ID is returned.
3. Otherwise, activation status returns `compatibleBugfixArtifactIds`, computed from
   the installed Office version and the current protected manifest.
4. The reviewed `office-support` skill selects only an ID in that returned list and
   calls `office_runtime_status` for the Windows session bound to the conversation.
5. If Word, Excel, PowerPoint, Outlook, OneNote, Access, or Publisher is open, Hermes
   names the running applications and stops. The endpoint never closes them. After the
   user closes them, Hermes checks again in the next turn.
6. When none is open, Hermes calls `run_office_activation_repair` with the compatible
   artifact ID. This dedicated workflow does not create an approval card. Direct operator
   dispatch still uses the Tray consent path.
7. The endpoint service performs final session-scoped process and OSPP checks. It
   refuses to run the BAT if Office is already healthy KMS, then downloads the
   selected BAT with its enrolled-device bearer token,
   verifies SHA-256, and stages it in the content-addressed path
   `<artifactId>\<SHA-256>\<artifactId>.bat` under the protected remediation tree.
   Different approved revisions therefore never share a runnable pathname.
8. The service opens the protected source first, impersonates the verified interactive
   user's Windows token, and writes a verified convenience copy to that user's Desktop.
9. The LocalSystem service runs only the protected staged copy in session 0. It waits for
   and answers each approved playbook prompt, never executes the user-writable Desktop
   copy, and never requests a UAC password.
10. After a `Completed` playbook result with exit code `0`, the endpoint immediately
    verifies healthy KMS with OSPP.
    Hermes calls `office_activation_status` again even when execution reported an error,
    unless an Office app reopened and blocked execution. Exit code `0` alone is not
    proof that activation is healthy.

## Adding or changing a repair

1. Review the BAT and copy it into the protected repository using the artifact ID as its
   filename.
2. Compute its hash with `Get-FileHash -Algorithm SHA256`.
3. Manually run the BAT in a disposable VM and transcribe stable literal prompt fragments
   and fixed responses into a version 1 playbook. Do not include credentials, tokens, or
   product keys.
4. Add or update its manifest entry, playbook, and intended Office version range; avoid
   accidental overlaps unless the skill contains an explicit rule for choosing between
   them.
5. Update the administrator selection policy in
   `deploy\hermes\skills\office-support\SKILL.md` if more than one compatible ID can be
   returned.
6. Promote the reviewed skill to the active Hermes profile and reload that profile.

No endpoint code change is needed for another valid manifest entry. Replacing BAT content
without updating its pinned hash makes the artifact unavailable.

Deploy the playbook-aware control plane and endpoint binaries before publishing a
playbook-enabled production artifact, and confirm endpoints report the updated version.
Pilot the harmless sample first, then an independently reviewed legitimate reseller BAT
in a disposable VM, and finally one test endpoint. BATs that require UAC, credentials,
GUI interaction, arrow keys, a signed-in user's profile, new console windows, or detached
processes are unsupported. ConPTY, branching, transcript storage, live input, and
automatic response selection are intentionally not part of this release.

`KMS_mng.bat` remains excluded until its embedded payload, provenance, behavior, and
licensing purpose are independently approved.

The privileged path is available only after the elevated production installers have
created the protected repository and installed `EndpointAgents.Service` as LocalSystem.
Running the API, Service, or Tray directly as an ordinary development process is useful
for diagnostics, but it deliberately cannot pass the production ACL and privilege gates.
Do not weaken those gates to make a development sample run.

Both the Control Plane repository and endpoint staging roots must retain their installed,
SYSTEM-owned protected ACLs. Non-admin users never receive write, delete, ownership, or
ACL-change rights. The Office staging tree grants read/execute only because the separate
Microsoft-signed remediation flow launches a verified executable through the user's Tray.
The API and endpoint runtime independently reject a user-writable, replaceable, or
redirected trust path even if a manifest hash otherwise matches.

## Privilege policy

Do not put an Active Directory administrator username or password in Hermes, a skill,
9router, ControlPlane parameters, or endpoint IPC. The installed endpoint service already
runs as LocalSystem for local machine repairs. If a future repair needs authenticated
domain network access, use a least-privilege gMSA or the machine account and deny
interactive logon; never distribute a reusable password to agents.
