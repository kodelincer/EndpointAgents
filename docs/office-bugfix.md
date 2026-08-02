# Single Office activation repair

The control plane exposes exactly one administrator-managed Office repair file:

```text
C:\ProgramData\EndpointAgents\ControlPlane\FileRepository\office-activation-repair.bat
```

`scripts\install-control-plane.ps1` copies a harmless sample into that location
when the file does not already exist. Replace it with the reviewed production BAT
and restart `EndpointAgents.ControlPlane.Api`. No manifest or MD5 sidecar is used;
the API computes SHA-256 from the current file and the endpoint verifies that exact
hash after downloading it.

The BAT must expose **Office activation problem as menu item 1**, accept `1` from standard
input, perform its work without a desktop UI, and return exit code `0` on success.

## Runtime flow

1. Hermes calls `office_inventory` and `office_activation_status` when the user
   reports Product Unlicensed, an activation failure, or the red activation warning.
2. If Office is installed and its version is readable, Hermes proposes
   `software.office_bugfix_run` with artifact ID `office-activation-repair`.
3. The endpoint user confirms the privileged repair in the Tray.
4. The LocalSystem endpoint service downloads `office-activation-repair.bat`, verifies its
   SHA-256, rejects reparse points and unexpected paths, and runs it through the
   system `cmd.exe` with menu input `1`.
5. Exit code `0` is returned as `Completed`; other exit codes are failures.

Hermes cannot supply a filename, path, URL, menu selection, script, or command-line
argument. Only an administrator who can write the protected control-plane repository
can replace the approved BAT.

Example RPC:

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
