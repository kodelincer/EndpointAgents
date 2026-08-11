# Managed Windows activation repair

`system.windows_activation_status` reads sanitized Windows version, edition, build,
license status, grace state, and activation channel. It never returns product keys,
partial keys, licensing IDs, device IDs, or raw PowerShell output.

Administrators provide the reviewed repair at:

```text
C:\ProgramData\EndpointAgents\ControlPlane\FileRepository\Windows\windowsbugfixer.bat
```

The file is not installed automatically. The endpoint must be enrolled and remote
downloads require HTTPS. The endpoint obtains local user consent, downloads only the
fixed artifact ID, verifies its SHA-256, rejects unexpected paths and reparse points,
and runs it as LocalSystem with no arguments. A zero exit code is followed by another
activation probe; only a licensed post-check is reported as `Resolved`.

A conservative sample is stored at
`deploy\control-plane\FileRepository\Windows\windowsbugfixer.bat`. Review it, run
`windowsbugfixer.bat --self-test`, and copy it to the protected runtime path manually.
The sample only starts the Windows licensing service and asks Microsoft's built-in
`slmgr.vbs /ato` to activate the license already installed on the machine.

This workflow is for legitimate repair of an existing Windows license. Licensing
bypasses, unauthorized keys, KMS emulators, and activation circumvention are prohibited.
