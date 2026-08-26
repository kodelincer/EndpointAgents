# Managed Windows activation repair

`system.windows_activation_status` reads sanitized Windows version, edition, build,
license status, grace state, and activation channel. It never returns product keys,
partial keys, licensing IDs, device IDs, or raw PowerShell output.

After an explicit Windows activation-problem request, Hermes first checks this status.
If Windows is already licensed or the status cannot be verified, it stops without
changing the endpoint. Otherwise, the dedicated `run_windows_activation_repair` tool
dispatches the fixed administrator-approved artifact automatically; it does not create
an approval card. Direct operator dispatch retains its Tray consent check.

Administrators provide the reviewed repair at:

```text
C:\ProgramData\EndpointAgents\ControlPlane\FileRepository\Windows\windowsbugfixer.bat
```

The file is not installed automatically. The endpoint must be enrolled and remote
downloads require HTTPS. ControlPlane binds automatic chat execution to the explicit
request's endpoint and user, pins the current filename and SHA-256, and supplies a
request-scoped authorization marker. The endpoint downloads only the fixed artifact ID,
verifies its SHA-256, rejects unexpected paths and reparse points, and runs it as
LocalSystem with no arguments. A zero exit code is followed by another activation probe;
only a licensed post-check is reported as `Resolved`.

Keep the seller-provided BAT outside Git. After reviewing it and recording its
SHA-256 through an independent handoff, install it from an elevated PowerShell
session with:

```powershell
.\scripts\install-windows-activation-repair.ps1 `
  -SourcePath 'C:\ApprovedArtifacts\seller-windows-repair.bat' `
  -ExpectedSha256 '64_HEXADECIMAL_CHARACTERS'
```

The installer rejects a mismatched hash, reparse point, unprotected repository,
or non-BAT input. It stages the file under a protected ACL and atomically replaces
the fixed runtime filename. Its output is the deployed SHA-256 to record with the
change approval. If the ControlPlane installer finds an exact copy of the development
sample at the runtime filename, it moves that copy out of the catalog before starting
the service.

A conservative development sample is stored at
`deploy\control-plane\FileRepository\Windows\windowsbugfixer.bat`. It must not be
promoted as the seller artifact. The sample only starts the Windows licensing service
and asks Microsoft's built-in `slmgr.vbs /ato` to activate the license already
installed on the machine.

This workflow is for legitimate repair of an existing Windows license. Licensing
bypasses, unauthorized keys, KMS emulators, and activation circumvention are prohibited.

## Development-only non-administrator test

When both ControlPlane and the endpoint run with the Development environment, the
ControlPlane repository is the workspace folder
`deploy\control-plane\FileRepository`, and endpoint staging relaxes only the
administrator and protected-ACL requirements. The fixed filename, artifact ID, download
route, path containment, reparse-point rejection, SHA-256 verification, pre-check, and
post-check remain enforced. This permits an ordinary developer account to test a reviewed
BAT; it does not enable the relaxation in production.

A licensed development machine still refuses the activation repair because no repair is
needed. Use the harmless Windows Hello workflow to test execution on a licensed machine,
or use a legitimate unlicensed lab VM to exercise the activation-repair branch.
