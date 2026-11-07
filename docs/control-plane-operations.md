# Control Plane Operations

## Runtime architecture

The control plane has two independent processes:

- `EndpointAgents.ControlPlane.Api` is the ASP.NET Core Windows service. It owns endpoint connections, authenticated chat, SQLite persistence, Hermes orchestration, the REST API, both SignalR hubs, command dispatch, health endpoints, file delivery, and activity history.
- `ControlPlane.Web` is the Next.js dashboard. It runs as a Node process on loopback port 3000 and calls the API on port 5052. It is never exported or copied into the API's `wwwroot`.

Only the API is installed as a Windows service. The Next.js process is started manually in this phase, so it does not restart automatically after logout or reboot.

The dashboard and operator APIs remain loopback-only. Enrolled endpoint agents
authenticate to the agent hub with device tokens and can remotely reach only
explicit endpoint routes. Production endpoint traffic requires HTTPS. See
[hermes-support-agent.md](hermes-support-agent.md) for enrollment, chat
isolation, Hermes, MCP, and rollout operations.

## Ports and routes

| Address | Access | Purpose |
| --- | --- | --- |
| `http://127.0.0.1:3000/` | Server only | Next.js operations dashboard |
| `http://127.0.0.1:5052/` | Server only | API identity/status JSON |
| `https://<server>/agent-hub` | Remote | Authenticated endpoint-agent SignalR hub |
| `http://127.0.0.1:5052/dashboard-hub` | Server only | Browser SignalR hub |
| `http://127.0.0.1:5052/internal/mcp` | Hermes only | Loopback bearer-protected MCP tools |
| `http://127.0.0.1:8642/v1/runs` | ControlPlane only | Loopback bearer-protected Hermes run API |
| `https://<server>/health/live` | Remote | API liveness |
| `https://<server>/health/ready` | Remote | Command readiness; returns 503 during maintenance |
| `https://<server>/api/files/ibsng-dialer` | Remote | Managed artifact download |
| `https://<server>/api/files/office/catalog` | Remote | Hash-verified Office remediation catalog |
| `https://<server>/api/files/office/{artifactId}` | Remote | One catalog-approved Office artifact |

Operator routes for general actions (loopback dashboard only): `/api/capabilities`,
`/api/actions/*` (settings, runs, undo, missing-action requests), `/api/environment/*`,
`/api/incidents` and `/api/skills/*`. See `docs/endpoint-actions.md`.

Port 5052 never serves dashboard HTML. Unknown frontend routes on the API return HTTP 404, while non-loopback requests to operator routes return HTTP 403.

## Dashboard sign-in and roles

Every operator route and the `dashboard-hub` need a signed-in operator (an HttpOnly, SameSite=Strict
cookie from `POST /api/auth/sign-in`); anonymous calls get 401. Endpoint agents (device token), Hermes MCP
(its own key), health checks and the remote artifact routes are unchanged. The dashboard calls the API on
port 5052 of the same host name it was opened on, which keeps the cookie same-site.

| Role | Can |
| --- | --- |
| Member | View everything, answer helpdesk conversations, take over, run read-only diagnostics |
| Reviewer | Member, plus change feature-request status (and publish skills once the skill editor exists) |
| Admin | Everything, plus endpoint commands, maintenance mode, device revoke, enrollment codes and the Operators page |

First start: when the database has no operators, the API creates `admin` with a one-time password and
writes it to `initial-admin-password.txt` next to the database (`.local\` in development,
`C:\ProgramData\EndpointAgents\ControlPlane\` in production). The password is never logged. Signing in with
it forces a new password; delete the file afterwards. Admins add operators on the Operators page, which shows
each one-time password once.

### Two kinds of accounts: local and Active Directory

- **Local accounts** are created on the Operators page by an admin (one-time password). They always work,
  even when the domain controller is down, so keep at least one local admin.
- **Active Directory accounts** sign in with their normal domain password (`sara`, `CORP\sara` or
  `sara@corp.example`). The control plane checks the password with the domain controller and never stores it.
  An admin can add a domain user by name on the Operators page (choose "Active Directory user"), or members of
  mapped groups are created automatically at their first sign-in. A name that has a local account is always
  checked locally. A domain user's role is the one an admin gives on the Operators page; the group only
  decides the starting role. Disable the account there to revoke access.

Off by default. To turn it on, set in `appsettings.Production.json` (no password needed; the service's own
domain identity is used for lookups):

```json
"ActiveDirectory": { "Server": "dc01.corp.example", "BaseDn": "DC=corp,DC=example" },
"OperatorDirectory": { "Enabled": true, "Domain": "CORP",
  "AdminGroup": "", "ReviewerGroup": "", "MemberGroup": "HelpdeskStaff" }
```

Empty groups mean only domain users added by name can sign in. Leave `ActiveDirectory:Enabled` as it is: that
switch is for the agent's read-only account check, a separate feature using the same server and search base.

Safety: PBKDF2-SHA256 (600k iterations) hashes; five wrong passwords for one name from one address lock it for
15 minutes; disabling an account, changing its role or resetting its password ends its sessions at once.
`POST /api/agent-enrollments` still works without sign-in for local scripts (loopback, no browser `Origin`),
so `scripts\start-dev-stack.ps1` keeps enrolling; from a browser it needs an admin.

## Local development

Run the API from the repository root:

```powershell
dotnet run --project .\src\ControlPlane.Api\ControlPlane.Api.csproj --launch-profile http
```

The Development profile reads artifacts directly from
`deploy\control-plane\FileRepository` and does not require protected ACLs. To test a
Windows repair BAT without administrator rights, replace only the fixed development
file from the repository root:

```powershell
Copy-Item -LiteralPath 'C:\path\to\reviewed-test.bat' `
  -Destination '.\deploy\control-plane\FileRepository\Windows\windowsbugfixer.bat'
```

The development endpoint stages the downloaded BAT in its local build directory and
runs it with the current user's token. Exact path, fixed artifact ID, and SHA-256 checks
still apply. Production ignores these development relaxations and continues to require
the protected ProgramData repository, protected endpoint staging, and LocalSystem.

Run the frontend in a second terminal:

```powershell
Set-Location .\src\ControlPlane.Web
npm.cmd run dev
```

Open `http://localhost:3000`. The frontend defaults to `http://localhost:5052`; set `NEXT_PUBLIC_CONTROL_PLANE_URL` before starting or building only when the API address differs.

Run the complete endpoint agent from one PowerShell prompt when testing the
connection path:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\run-endpoint-dev.ps1
```

The command builds and starts both `EndpointAgents.Service` and
the WebView2 Tray (`EndpointAgents.TrayWeb`; add `-LegacyTray` for the WinUI one); press Ctrl+C to stop both. Stop an installed endpoint
service and exit any existing Tray instance before using the development host.

In Visual Studio, select the shared `Endpoint Agent - Service + Tray` launch
profile and press F5 to start and debug both projects together. The service owns
SignalR and background commands, while the Tray remains in the logged-in user's
interactive Windows session and communicates with the service through named
pipes.

The Tray recognizes a service started interactively by the development command
or Visual Studio and labels it `Interactive`; Windows-service Start and Stop
controls remain disabled in this mode.

To build a Release-flavored local development host instead:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\run-endpoint-dev.ps1 -Configuration Release
```

## Production build and startup

Install or update the API Windows service from an elevated PowerShell session:

```powershell
.\scripts\install-control-plane.ps1
```

The installer publishes only `ControlPlane.Api`, registers `EndpointAgents.ControlPlane.Api`, configures restart-on-failure, and removes the retired `EndpointAgents.ControlPlane.Web` service when upgrading an older installation.

Install and configure the pinned Hermes service next:

```powershell
.\scripts\install-hermes-service.ps1 <required parameters>
```

See [hermes-support-agent.md](hermes-support-agent.md) for the complete command,
TLS setup, and staged device enrollment.

Build and start the separate Next.js server manually:

```powershell
Set-Location .\src\ControlPlane.Web
npm.cmd ci
npm.cmd run build
npm.cmd run start
```

`npm.cmd run start` binds to `127.0.0.1:3000` and must remain running for the dashboard to be available.

To uninstall the API service:

```powershell
.\scripts\uninstall-control-plane.ps1
```

Add `-RemovePublishedFiles` to remove the published API binaries. The uninstaller also cleans up the retired web service if an older installation still contains it.

Install the endpoint Windows Service and per-user Tray startup registration from
an elevated prompt on an endpoint machine:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\install-endpoint.ps1 `
  -ControlPlaneHubUrl 'https://CONTROL-PLANE-SERVER/agent-hub' `
  -EnrollmentCode 'ONE-TIME-ENROLLMENT-CODE'
```

This is one installation action: the background service starts immediately and
the Tray starts for each user at their next interactive logon. The WebView2 Tray needs the
Microsoft Edge WebView2 Runtime; the script installs it from Microsoft's signed offline installer
when missing (see `docs/tray.md`). Remove both
registrations with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\uninstall-endpoint.ps1
```

## Lifecycle behavior

- **Maintenance** keeps the API and agent connections alive but rejects new endpoint commands with HTTP 503.
- **Resume** enables command execution again.
- **API crash recovery** is handled by Windows Service Control Manager.
- **Web process recovery** is manual in this phase; restart it with `npm.cmd run start`.

Maintenance state is process-local and resets to normal after an API restart.
Chat conversations, messages, agent runs, action proposals, and tool audit
events are durable in SQLite under
`C:\ProgramData\EndpointAgents\ControlPlane`; completed conversation data is
retained for 30 days by default.

## Troubleshooting

- **API reports a missing `wwwroot` directory:** rebuild after these separation changes; the API no longer requires `wwwroot` or `ControlPlane.Web\out`.
- **Dashboard reports SignalR negotiation failure:** verify the API is listening on port 5052 and that `GET http://localhost:5052/health/live` returns 200.
- **Port 5052 returns JSON instead of the dashboard:** expected; open `http://localhost:3000`.
- **A remote browser receives HTTP 403:** expected in the unauthenticated phase; open the dashboard on the control-plane server.
- **Agents cannot connect:** use the control-plane server's hostname or IP in endpoint configuration instead of the endpoint machine's `localhost`.
- **Readiness returns HTTP 503:** maintenance mode is enabled.
- **Build reports locked assemblies:** stop the running API process or build with an isolated `BaseOutputPath`.
