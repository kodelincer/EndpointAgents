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
      "officeProductReleaseIds": ["ProPlus2021Volume"],
      "minWindowsVersion": "10.0.22000",
      "maxWindowsVersionExclusive": "10.0.30000",
      "additionalCompatibility": [
        {
          "minOfficeVersion": "16.0",
          "maxOfficeVersionExclusive": "17.0",
          "officeProductReleaseIds": ["ProPlus2019Volume"],
          "minWindowsVersion": "10.0.19041",
          "maxWindowsVersionExclusive": "10.0.22000"
        },
        {
          "minOfficeVersion": "16.0",
          "maxOfficeVersionExclusive": "17.0",
          "officeProductReleaseIds": ["ProPlus2024Volume"],
          "minWindowsVersion": "10.0.26000",
          "maxWindowsVersionExclusive": "10.0.30000"
        }
      ],
      "supportFiles": [],
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
timeouts must be 5–300 seconds; the first prompt also gets 30 seconds for cmd and
antivirus start-up. Responses cannot contain control characters, credentials, tokens,
secrets, or product keys.

### Playbook version 2

Version 1 is strictly linear. Version 2 (`"version": 2`) keeps every version 1 rule and
adds, all with literal matching only:

| Field | Where | Meaning |
| --- | --- | --- |
| `alternatives` | step | Up to 8 more `{ expect, send, submit }` prompts the same step may show; the one that appears first is answered. |
| `optional` | step | The step is skipped when a later step's prompt (or the end of the BAT) comes first. |
| `maxRepeats` | step | 1–20. The step is answered each time its prompt reappears, until the next step's prompt shows; one more repeat stops the run as `RepeatLimitExceeded`. |
| `successExitCodes` | playbook | Accepted exit codes (default `[0]`, at most 16). |
| `successMarker` | playbook | Text that must appear in the output, or the run is `SuccessMarkerMissing`. |
| `failureMarkers` | playbook | Up to 16 texts that stop the run immediately as `FailureMarkerDetected`, including a known unexpected prompt or a printed failure with exit code 0. |
| `guiDialogGraceSeconds` | playbook | 5–600, default 15. A window from the BAT that stays visible this long stops the run as `GuiDialogBlocked`; see [bat-packages.md](bat-packages.md#5-windows-that-need-a-mouse). |

At most 64 responses are sent per run. The playbook hash covers every field, so any
change needs fresh approval. Version 1 hashes are unchanged. A prompt that is neither
expected nor listed in `failureMarkers` cannot be told apart from slow output; it ends
as `PromptTimedOut` after the step timeout. A full example is in
`deploy/samples/menu-scenarios-sample.playbook.json`.

When a run does not complete, the result carries `OutputTail`: at most the last 4 KB of
normalized output, with credentials, bearer tokens, product keys, and long opaque
tokens redacted. It appears in the endpoint response for operators and is never
forwarded to the model.

### Publish-time lint

The control plane lints every catalog BAT. It rejects BATs that request elevation
(`runas`, `-Verb RunAs`), run encoded PowerShell, or download or decode code at run time
(`certutil -decode`/`-urlcache`, `bitsadmin /transfer`, `Invoke-WebRequest`,
`DownloadString`/`DownloadFile`), because the pinned hash cannot cover that code. It
reports warnings, without rejecting, for `timeout /t`, `start`, `mshta`, `msg`, and
`%TEMP%` scripts. Findings appear in the catalog issues.

Office 2016, 2019, 2021, 2024, and Microsoft 365 all report version `16.0`, so a
`16.0`–`17.0` range alone does **not** identify a release year. For a release-specific
BAT, list its reviewed Click-to-Run `ProductReleaseIds` in
`officeProductReleaseIds`. Every installed Click-to-Run ID must be in that list;
missing IDs or a mixed installation with an unlisted ID make the BAT incompatible.
Leave the list empty only for a repair reviewed for every product in the numeric range.
The optional Windows bounds use the endpoint OS version, including its build number;
the minimum is included and the maximum is excluded. Omit both for all supported
Windows builds. `additionalCompatibility` adds up to 31 more Office/Windows target
rules to the primary rule above it. The rules have OR semantics: one complete rule
must match the installed Office version, all detected Click-to-Run product IDs, and
the Windows build. All rules use the same BAT, support files, and playbook; no BAT
copy is needed per Office release. The example product IDs and bounds are illustrative,
not a claim that any particular reseller BAT supports them. Catalog entries are
listed by their earliest minimum Office version, then artifact ID. The endpoint
checks the rules again immediately before downloading the BAT.

When several Office releases have identical Windows bounds and menu behavior, put
all their `ProductReleaseIds` in the primary `officeProductReleaseIds` array instead
of repeating equivalent rules.

Keep the first rule for an older endpoint during rollout. Older binaries ignore
`additionalCompatibility` and can run only the primary range; deploy updated
endpoint binaries before expecting the extra ranges to be available.

For a BAT with adjacent files, list each flat filename and SHA-256 in `supportFiles`
and place it under `<artifactId>.files/<fileName>` in the same repository. The
endpoint downloads each declared file beside the BAT, verifies every hash, and pins
the combined bundle revision. Nested directories are not supported.

The control plane normalizes and hashes the complete ordered playbook. Action approval
pins the filename, BAT SHA-256, and playbook SHA-256, so a meaningful prompt, response,
submission mode, timeout, order, or version change requires fresh authorization. The
endpoint verifies both hashes independently before launch.

The endpoint starts hidden `System32\cmd.exe` with redirected input, output, and error
streams. It reads output in chunks because prompts need not end in a newline, removes
ANSI formatting and nonsemantic control characters, and matches each step only against
output received after the prior response. Status text is retained but ignored unless it
contains the approved literal prompt. Each response and its Enter go to the BAT in one
pipe write with BOM-free input encoding. Output is capped at 1 MiB. The process must
finish all required steps and exit with an approved code; the service stops it after
eight minutes, before the endpoint command's ten-minute outer timeout. A stopped BAT is
killed before its input is closed, so it never receives an empty answer.

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
8. For a standalone BAT, the service opens the protected source first, impersonates the
   verified interactive user's Windows token, and writes a convenience copy to that
   user's Desktop. Bundles with supporting files stay together in protected staging
   and are not copied to the Desktop.
9. The LocalSystem service runs only the protected staged copy in session 0. It waits for
   and answers each approved playbook prompt, never executes the user-writable Desktop
   copy, and never requests a UAC password.
10. After a `Completed` playbook result, the endpoint immediately verifies healthy KMS
    with OSPP.
    Hermes calls `office_activation_status` again even when execution reported an error,
    unless an Office app reopened and blocked execution. Exit code `0` alone is not
    proof that activation is healthy.

## Adding or changing a repair

1. Review the BAT and copy it into the protected repository using the artifact ID as its
   filename.
2. In a disposable VM, write the playbook with the playbook lab. It hashes and lints the
   BAT, validates the playbook, runs the BAT through the real endpoint runner with
   `-Execute`, shows the screen after your last step with `-Discover`, and prints a
   manifest entry draft:

   ```powershell
   .\scripts\test-bat-playbook.ps1 -Bat .\my-repair.bat -Playbook .\my-repair.playbook.json `
       -Execute -Discover
   ```

   Add one step per screen until the run completes. Use stable literal prompt fragments.
   Do not include credentials, tokens, or product keys.
3. Copy the reported SHA-256 into the manifest entry (`Get-FileHash -Algorithm SHA256`
   gives the same value).
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
processes are unsupported. ConPTY, regular expressions, full transcript storage, live
input, and model-chosen responses are intentionally not supported.

Both inline `set /p "answer=Prompt: "` and `<nul set /p "=Prompt: "` followed by
`set /p "answer="` work, and no pause is needed. Verify each menu transition with the
playbook lab before deploying a BAT.

The current `tmpfiles/office_bug_fixer.bat` and
`tmpfiles/windows_bug_fixer.bat` are identical copies of a large `KMS Suite` BAT.
Their wrapper requests elevation, extracts an embedded PowerShell payload, and
starts another BAT from `%TEMP%`. They remain excluded from the catalog until the
payload, provenance, behavior, licensing purpose, and unattended menu path are
independently reviewed. This runner cannot safely automate that wrapper as-is.

## Test the harmless menu BAT locally

From the repository root, run `dotnet test` on
`src\EndpointAgents.Platform.Windows.Tests\EndpointAgents.Platform.Windows.Tests.csproj`
with `--filter FullyQualifiedName~CheckedInSampleBatCompletesApprovedMenuPlaybook`.
This executes the checked-in sample's `set /p` and `choice` prompts through the
endpoint's hidden runner. It does not change Office settings.

To show the sample in the **development** Control Plane catalog, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\seed-office-bugfix-samples.ps1 -Development
```

The script writes a hash-pinned manifest to the development FileRepository and pins
the host's detected Click-to-Run product IDs when available. It refuses to replace an
existing manifest unless explicitly run with `-Force` (and backs that manifest up
first). The sample is only for testing Office `16.x` menus; it does not repair
activation or establish compatibility for any reseller BAT. Production seeding still
requires an elevated PowerShell session and a protected repository.

The privileged path is available only after the elevated production installers have
created the protected repository and installed `EndpointAgents.Service` as LocalSystem.
Running the API, Service, or Tray directly as an ordinary development process cannot
pass the production ACL and privilege gates. For local end-to-end testing only, the
Service's `appsettings.Development.json` sets `Diagnostics:OfficeRemediation` to stage
under `.local\OfficeRemediation` with `RequireProtectedAcl` and `RequireAdministrator`
set to `false`; the BAT then runs with the current user's token. Filename, SHA-256,
playbook-hash, and staging-path checks still apply. The Service refuses to start with
these relaxations outside the Development environment, so production gates are unchanged.

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
