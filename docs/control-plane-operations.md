# Control Plane Operations

## Runtime architecture

The control plane is intentionally split into two Windows services:

- `EndpointAgents.ControlPlane.Api` owns endpoint SignalR connections, the command catalogue, RPC dispatch, and the bounded in-memory activity history.
- `EndpointAgents.ControlPlane.Web` serves the exported Next.js operations console and controls the API through Windows Service Control Manager (SCM).

The Web service has no dependency on the API service. It therefore remains available when the API is stopped and can start it again. The first release is unauthenticated and binds the Web service only to `127.0.0.1`; do not expose port 3000 to another machine until authentication and authorization are added.

## Ports and routes

| Address | Purpose |
| --- | --- |
| `http://127.0.0.1:3000` | Production operations console and local management API |
| `http://127.0.0.1:5053` | WebHost management API during local development |
| `http://localhost:5052/agent-hub` | Endpoint-agent SignalR hub |
| `http://localhost:5052/dashboard-hub` | Browser activity SignalR hub |
| `http://localhost:5052/health/live` | API process liveness |
| `http://localhost:5052/health/ready` | Command readiness; returns 503 in maintenance |

`ControlPlane.Api` listens on port 5052 for endpoint agents. `ControlPlane.WebHost` rejects management requests whose remote address is not loopback.

## Local development

Run these in separate terminals from the repository root:

```powershell
dotnet run --project src\ControlPlane.Api\ControlPlane.Api.csproj --launch-profile http
dotnet run --project src\ControlPlane.WebHost\ControlPlane.WebHost.csproj --launch-profile http
Set-Location src\ControlPlane.Web
npm.cmd run dev
```

Open `http://localhost:3000`. In development, the Next.js app calls WebHost on port 5053 and ControlPlane.Api on port 5052.

## Install as Windows services

From an elevated PowerShell session:

```powershell
.\scripts\install-control-plane.ps1
```

The script builds the static Web export, publishes both .NET applications under `Program Files\EndpointAgents\ControlPlane`, registers automatic-start services, configures restart-on-failure, and starts both services.

To uninstall:

```powershell
.\scripts\uninstall-control-plane.ps1
```

Add `-RemovePublishedFiles` to remove the published binaries too. The initial local deployment uses the default service identity so WebHost can call SCM. Before enabling remote access, move WebHost to a dedicated service account granted control rights only over `EndpointAgents.ControlPlane.Api`.

## Lifecycle behavior

- **Start** starts the API Windows service and waits up to 30 seconds for SCM to report `Running`.
- **Stop** asks SCM to stop the API and waits for `Stopped`. Connected agents disconnect and in-flight commands end.
- **Restart** performs a bounded stop followed by start.
- **Maintenance** keeps the API and SignalR connections alive but rejects new endpoint commands with HTTP 503.
- **Resume** enables command execution again.

Only one lifecycle action runs at a time. A competing request receives HTTP 409. Maintenance state is process-local and resets to normal after an API restart.

## Health interpretation

- `healthy`: Windows service is running; liveness and readiness pass.
- `maintenance`: process is live, readiness is 503, and command execution is intentionally paused.
- `degraded`: process responds but readiness fails for a reason other than maintenance.
- `unreachable`: health endpoints cannot be reached or the service is stopped.
- `unknown`: SCM state could not be read.

The dashboard polls service health every five seconds and preserves the last successful snapshot when a later poll fails.

## Troubleshooting

- **Web opens but service status is unavailable:** start `ControlPlane.WebHost` on port 5053 during development, or verify the Web Windows service is running in production.
- **Start/stop reports access denied:** run WebHost under an account with SCM control rights for the API service.
- **Agents cannot connect:** verify port 5052 is reachable and each endpoint uses the server's actual hostname or IP rather than its own `localhost`.
- **Build reports locked assemblies:** stop the running API/WebHost process or build with an isolated `BaseOutputPath`.
- **No old activity after restart:** activity is intentionally in memory and capped at 500 records; no database is used in this phase.
