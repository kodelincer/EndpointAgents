# Office diagnosis and approved remediation

`software.office_activation_status` is read-only. It checks Microsoft 365 Apps in the
active user's Tray session with the installed `vnextdiag.ps1` and checks volume Office
with the installed `OSPP.VBS`. Results exclude email addresses, tenant and license IDs,
activation tokens, raw command output, and product-key fragments.

The runtime artifact directory is:

```text
C:\ProgramData\EndpointAgents\ControlPlane\FileRepository\Office
```

The control-plane installer creates an empty `office-artifacts.json`. Administrators may
place reviewed artifacts beside it. The catalog is read-only over HTTP: there is no
remote upload operation.

Only entries with `kind` set to `MicrosoftSignedExecutable` can be executed. The
endpoint downloads the file into the content-addressed path
`C:\ProgramData\EndpointAgents\OfficeRemediation\<artifactId>\<SHA-256>\<fileName>`,
checks the manifest SHA-256 again, requires a valid Microsoft Authenticode signer,
asks the signed-in user for consent, and launches the executable through the Tray.
That protected tree is SYSTEM-owned and grants ordinary users read/execute but never
write, delete, ownership, or ACL-change access.
The RPC never accepts a filename, path, script body, or command-line arguments.

## Manifest example

```json
{
  "version": 1,
  "artifacts": [
    {
      "artifactId": "example-m365-remediation",
      "displayName": "Example Microsoft 365 remediation",
      "description": "Replace this example with an administrator-reviewed remediation.",
      "fileName": "MicrosoftOfficeRepairTool.exe",
      "sha256": "REPLACE_WITH_64_HEXADECIMAL_CHARACTERS",
      "kind": "MicrosoftSignedExecutable",
      "supportedProductFamilies": ["Microsoft365Apps"],
      "minOfficeVersion": "16.0",
      "maxOfficeVersion": null,
      "activationStates": ["Unlicensed", "Notification"],
      "errorCodes": [],
      "requiresInteractiveUser": true,
      "requiresOfficeAppsClosed": true,
      "requiresRestart": false
    }
  ]
}
```

Catalog-only kinds are `MicrosoftScript`, `PowerShellScript`, and
`OfficeConfiguration`; the agent does not execute them. The only executable kind is
`MicrosoftSignedExecutable`, and it must reference an `.exe`. An entry is omitted from
the catalog if its filename, compatibility fields, size, or computed SHA-256 does not
match the manifest. Batch files such as `.bat` and `.cmd` are rejected.

Licensing bypasses, KMS emulators, key generators, and similar tools must not be placed
in the manifest. For example, `KMS_Suite.v9.8.EN.bat` is intentionally ineligible and
cannot be downloaded through the approved Office catalog or executed by the endpoint.

## AI helpdesk workflow

The reviewed `office-support` skill uses the version-aware BAT catalog for Office
activation requests:

1. Run `software.office_inventory` to get the actual Office version, architecture,
   Click-to-Run product IDs, installed products, and application versions.
2. Run `software.office_activation_status`. Its `compatibleBugfixArtifactIds` contains
   only administrator-approved BAT repairs whose lower-inclusive, upper-exclusive
   version range matches the installed Office version.
3. Select exactly one ID returned in `compatibleBugfixArtifactIds`, following the
   administrator selection policy in the skill. If the list is empty or multiple entries
   are compatible without a selection rule, stop for administrator review instead of
   guessing.
4. Run `software.office_runtime_status` for the user session bound to the conversation.
   If an Office application is open, Hermes names it, asks the user to close it, and
   stops. The endpoint never terminates applications.
5. When the check is clear, call the dedicated `run_office_activation_repair` MCP tool
   with only the selected artifact ID. This path does not create an approval card. The
   endpoint repeats the process check immediately before execution.
6. The LocalSystem endpoint service downloads the BAT with its enrolled-device bearer
   token, verifies the pinned SHA-256, places an identical convenience copy on the user's
   Desktop while impersonating that verified user, and runs only its content-addressed
   protected staged copy. Hermes never receives an administrator password or types into
   UAC.
7. After the repair attempt returns, run `software.office_activation_status` again even
   if execution reported an error (unless execution was blocked because an Office app
   reopened). Report success only if this fresh result says activation is healthy.

The approved endpoint action is restricted to this request shape:

```json
{
  "jsonRpc": "2.0",
  "id": "office-bugfix-1",
  "method": "software.office_bugfix_run",
  "params": {
    "artifactId": "office-activation-repair"
  }
}
```

The BAT catalog and administrator update procedure are documented in
`office-bugfix.md`.

### Microsoft-signed remediation catalog

`compatibleArtifactIds` belongs to the separate `office-artifacts.json` catalog. It is
computed from the current product family, Office version, activation state, and error
code. An operator or another explicitly approved workflow may invoke
`software.office_remediation_run` only with an ID returned in that list; IDs from
`compatibleBugfixArtifactIds` are not valid for this method.

Example signed-tool execution request:

```json
{
  "jsonRpc": "2.0",
  "id": "office-remediation-1",
  "method": "software.office_remediation_run",
  "params": {
    "artifactId": "example-m365-remediation"
  }
}
```

If the manifest requires Office applications to be closed, the command returns the
running application names instead of terminating them. A successful response means
the signed tool was launched and includes its process ID; the agent does not wait for
the interactive repair tool to finish.

## Local test

After restarting the API, Tray, and endpoint service, choose **Get installed Office
information** and then **Check Office activation** in the dashboard. The Files page
shows only validated artifacts and any rejected-manifest warnings. Office downloads are
marked endpoint-only because their routes require an enrolled-device bearer token.

For the helpdesk BAT flow, the dedicated MCP tool authorizes
`software.office_bugfix_run` only when the selected ID is listed in
`compatibleBugfixArtifactIds`. For the Microsoft-signed flow, run
`software.office_remediation_run` only when the selected ID is listed in
`compatibleArtifactIds`.
