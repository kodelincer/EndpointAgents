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
endpoint downloads the file into `C:\ProgramData\EndpointAgents\OfficeRemediation`,
checks the manifest SHA-256 again, requires a valid Microsoft Authenticode signer,
asks the signed-in user for consent, and launches the executable through the Tray.
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

The helpdesk skill uses a fixed three-step flow:

1. Run `software.office_inventory` to get the actual Office version, architecture,
   Click-to-Run product IDs, installed products, and application versions.
2. Run `software.office_activation_status`. Its `compatibleArtifactIds` are computed
   from the current product family, Office version, activation state, and error code.
3. If the skill selects one of those IDs, call `software.office_remediation_run` with
   only that ID. The endpoint repeats the compatibility check before downloading.

Example execution request:

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
shows only validated artifacts and any rejected-manifest warnings. Run remediation
only after the status response lists the selected ID in `compatibleArtifactIds`.
