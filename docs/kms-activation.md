# Activation with the organisation KMS server

The organisation runs its own KMS server. An admin sets it once in the dashboard
(**Settings** → KMS server: a host name or IPv4 address, and an optional port, 1688 by
default). **Test from server** opens a TCP connection to it from the control-plane server
before you save. The values are stored as the environment settings
`activation.kmsHost` and `activation.kmsPort` (`GET/PUT /api/settings/kms`,
`POST /api/settings/kms/test`, admin only for changes).

## Catalogued actions (area `activation`)

| Action | Tier | What it does |
| --- | --- | --- |
| `kms.check` | Look | Is the KMS server set, does this PC reach it on its port, are Windows and Office volume (KMS client) editions, are they activated, do they already point at the organisation server |
| `windows.kms_activate` | SafeFix | Checks the server answers, points the Windows license at it and activates; NotNeeded when already activated by it; Blocked when not set, not reachable or not a volume edition |
| `office.kms_activate` | SafeFix | The same for every volume Office license (2016 and later, Click-to-Run volume included); Office apps can stay open |

Both fixes run without a Tray card and are checked with `kms.check` before and after.
They use the Software Protection WMI classes (`SoftwareLicensingProduct`
`SetKeyManagementServiceMachine`, `SetKeyManagementServicePort`, `Activate`, then
`SoftwareLicensingService.RefreshLicenseStatus`), the same calls `slmgr /skms /ato` and
`ospp.vbs /sethst /act` make. No shell, script or product key is involved. The host and
port come only from the control-plane setting, are validated on the server and again on
the endpoint (`KmsSettings.TryRead`: host name or IPv4 only, port 1-65535), and are passed
as typed WMI arguments, never as command text. The agent never sees or chooses the address.
This works the same on Windows 10 and 11.

## Agent flow

The `windows-support` and `office-support` skills try the organisation KMS first: after the
status check shows a problem, the agent runs `kms.check`; when the server is set, answers and
the product is a volume edition, it runs the matching `*.kms_activate` once and checks the
status again. When the server is not set or does not answer, or the activation fails, it falls
back to the signed activation packages in the skill's try order, then escalates to the helpdesk.
