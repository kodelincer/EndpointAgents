# KMS-only Office diagnosis and repair

`software.office_activation_status` is read-only. For every detected Click-to-Run or
MSI Office installation, it runs the locally installed Microsoft `OSPP.VBS` with
`/dstatusall`. It never runs `vnextdiag.ps1` and does not require an interactive user
session.

The response excludes raw command output, product-key fragments, credentials, and
activation tokens. It reports only sanitized product name, license type, activation
state, error code, and the `OSPP` source.

## KMS health

Office is healthy only when the OSPP command is available and successful, returns at
least one product, and every returned product:

- uses `Volume|KMS` licensing;
- has state `Licensed`; and
- has no activation error code.

Licensed MAK or Retail products are not healthy for this environment. Grace,
Notification, Unlicensed, and Error states are unhealthy. A missing, failed, timed-out,
or unparsable OSPP result remains `ToolUnavailable`, `Error`, or `Unknown`; it is never
reported as licensed.

A healthy KMS result returns `NoAction` and no `compatibleBugfixArtifactIds`. An
unhealthy or inconclusive result may return version-compatible BAT IDs from the
protected Office bugfix catalog.

## Approved BAT repair workflow

1. Run `software.office_inventory` and stop if Office is absent or its version is
   ambiguous.
2. Run `software.office_activation_status`. If every product is healthy KMS, report
   that result and stop.
3. Select one ID returned in `compatibleBugfixArtifactIds` according to the approved
   `office-support` skill policy.
4. Run `software.office_runtime_status`. The endpoint never closes Office applications;
   the user must close any listed applications before continuing.
5. Call `run_office_activation_repair` with only the selected artifact ID.
6. The endpoint repeats the Office process check and OSPP health check. It refuses to
   run the BAT if KMS is already healthy.
7. The LocalSystem service downloads the BAT with its enrolled-device credential,
   verifies the pinned SHA-256, stages it under the protected content-addressed Office
   remediation tree, and creates an identical convenience copy on the bound user's
   Desktop.
8. The service runs only the protected staged copy, supplies fixed menu choice `1`,
   passes no arguments or credentials, waits up to eight minutes, and requires exit
   code `0`.
9. The endpoint runs OSPP again. BAT completion is not considered successful unless
   this fresh result is healthy KMS. Hermes also performs its normal fresh status call
   and reports both execution and activation results.

The approved request shape remains:

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

## Security policy

Only an administrator-reviewed BAT for the organization's authorized Microsoft KMS
licensing may be registered. The exact file must be pinned in
`office-bugfix-managers.json` by SHA-256. KMS emulators, key generators, licensing
bypasses, arbitrary scripts, paths, arguments, product keys, and credentials are not
accepted by the endpoint command.

The BAT catalog and administrator update procedure are documented in
`office-bugfix.md`.
