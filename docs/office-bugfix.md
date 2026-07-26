# Office bugfix managers

The endpoint exposes a two-step general Office support workflow:

1. Run `software.office_inventory`.
2. Select a compatible catalog artifact ID and run `software.office_bugfix_run`.

The control-plane repository stores `office-bugfix-managers.json` and its executable
files directly under:

```text
C:\ProgramData\EndpointAgents\ControlPlane\FileRepository
```

Only filenames matching `officebugfixmanager_<version>.exe` are accepted. The
manifest is the source of truth for the executable SHA-256 and its inclusive minimum
and exclusive maximum Office versions. The endpoint accepts only an artifact ID,
rechecks compatibility, downloads into
`C:\ProgramData\EndpointAgents\OfficeRemediation`, verifies the hash, asks the
signed-in user for consent, and launches without arguments.

Retailer bugfix managers use administrator-approved SHA-256 trust. Existing
Microsoft Office remediation continues to require a trusted Microsoft Authenticode
signature. Raw `.bat` and `.cmd` files are not downloadable or executable.

## Manifest example

```json
{
  "version": 1,
  "artifacts": [
    {
      "artifactId": "office-bugfix-16",
      "displayName": "Office 16 bugfix manager",
      "description": "Administrator-reviewed retailer Office repair.",
      "fileName": "officebugfixmanager_16.0.exe",
      "sha256": "REPLACE_WITH_64_HEXADECIMAL_CHARACTERS",
      "minOfficeVersion": "16.0",
      "maxOfficeVersionExclusive": "17.0"
    }
  ]
}
```

The sample seeder copies harmless Notepad executables and clearly marks them as
non-repair samples:

```powershell
.\scripts\seed-office-bugfix-samples.ps1
```

Example RPC:

```json
{
  "jsonRpc": "2.0",
  "id": "office-bugfix-1",
  "method": "software.office_bugfix_run",
  "params": {
    "artifactId": "sample-office-bugfix-16"
  }
}
```

A successful response means the Tray launched the executable and includes its
process ID. The endpoint does not elevate, pass arguments, terminate Office
applications, or wait for the tool to finish.
