# Running the whole system on one PC (development)

Run every command from the repository root (for example `D:\EndpointAgents-runner`) in
Windows PowerShell. Each long-running part gets its own window; close the window or press
Ctrl+C to stop it. Stop an installed `EndpointAgents.Service` and exit any running Tray
first.

| Part | Port | Check |
| --- | --- | --- |
| Control plane API | 5052 | http://127.0.0.1:5052/health/ready returns 200 |
| Hermes gateway (profile `endpointagents`) | 8642 | `Get-NetTCPConnection -State Listen -LocalPort 8642` |
| Dashboard | 3000 | http://127.0.0.1:3000 opens |
| Endpoint service + Tray | - | the dashboard (or `Invoke-RestMethod http://127.0.0.1:5052/api/agents`) lists this PC |

## One-time setup

```powershell
# 1. Build once and install the dashboard's packages.
dotnet build EndpointAgents.slnx
Set-Location src\ControlPlane.Web; npm.cmd ci; Set-Location ..\..

# 2. Copy the reviewed Hermes skills, SOUL.md and tool list into the endpointagents profile.
#    Run it again after every change in deploy\hermes.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\sync-hermes-profile.ps1

# 3. Your package signing key (only on the PC where you approve packages).
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-package-signing-key.ps1
```

Development already trusts the dev key that signed the sample packages. To use your own
key in development, add its public key to `FileRepository:TrustedPackageSigningKeys` in
`src\ControlPlane.Api\appsettings.Development.json` and to `PackageSigning:TrustedPublicKeys`
in `src\EndpointAgents.Service\appsettings.Development.json`, then sign packages with
`.\scripts\test-bat-playbook.ps1 -Package <folder> -Fix -Sign`.

## Start everything

The easy way, which opens one window per part in the right order and enrolls this PC the
first time:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-dev-stack.ps1
```

Add `-Panes` to keep all four parts in one Windows Terminal window named `endpointagents`, split
in four. Close the window to stop everything, or a pane to stop that part.

Or by hand, each in its own PowerShell window, in this order:

```powershell
# 1. Control plane API (port 5052)
dotnet run --project .\src\ControlPlane.Api\ControlPlane.Api.csproj --launch-profile http

# 2. Hermes gateway (port 8642)
hermes -p endpointagents gateway run

# 3. Dashboard (port 3000)
Set-Location .\src\ControlPlane.Web; npm.cmd run dev
```

```powershell
# 4. Endpoint service + Tray. The first time, enroll this PC with a one-time code;
#    the device credential is then reused on every later start.
$cred = Join-Path $env:LOCALAPPDATA "EndpointAgents\dev-device-credential.bin"
$env:ControlPlane__DeviceCredentialPath = $cred
if (-not (Test-Path $cred)) {
    $env:ControlPlane__EnrollmentCode =
        (Invoke-RestMethod -Method Post http://127.0.0.1:5052/api/agent-enrollments).enrollmentCode
}
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-endpoint-dev.ps1
```

## Quick checks

```powershell
Invoke-WebRequest http://127.0.0.1:5052/health/ready -UseBasicParsing | Select StatusCode
Invoke-RestMethod http://127.0.0.1:5052/api/agents
(Invoke-RestMethod http://127.0.0.1:5052/api/files/packages).packages | Select id, status
```

Every package should show `Ready`. `Not signed` means it needs `-Sign` or its key is not
trusted; see `docs\bat-packages.md`.

## Troubleshooting

- **Build fails with "file is locked by ControlPlane.Api" or "EndpointAgents.Service":**
  close the API or endpoint window first, build, then start it again.
- **The PC does not appear in the dashboard:** the control plane database was reset, so the
  saved device credential is no longer valid. Delete
  `%LOCALAPPDATA%\EndpointAgents\dev-device-credential.bin` and start the endpoint again;
  it enrolls with a new code.
- **Repairs say "artifact downloads need an enrolled device credential":** the endpoint was
  started without enrolling; use step 4 above.
- **Hermes does not answer:** check port 8642, run `sync-hermes-profile.ps1 -Check`, and
  restart the gateway after any skill change.
- **The agent says a tool failed right after you restarted the control plane:** Hermes
  loses its connection to the control plane's tools and waits up to 5 minutes before
  retrying. Restart Hermes properly: `hermes -p endpointagents gateway stop`, then
  `hermes -p endpointagents gateway run`. Closing the window alone can leave the old
  gateway running, and a second `gateway run` is then refused.
- **`npm run dev` does nothing in Windows PowerShell:** use `npm.cmd run dev`.
