# EndpointAgents

Self-service IT helpdesk for Windows. A user describes a problem (in Persian) in the Tray app; the
control plane hands it to an isolated Hermes agent, which uses ControlPlane MCP tools to diagnose and,
within strict allow-lists, repair the user's PC through the endpoint service.

## Architecture

| Project | Role |
| --- | --- |
| `src/EndpointAgents.Shared` | Contracts used by both endpoint and server (RpcRequest/Response/Error, AgentRegistration, command names, DTOs). |
| `src/EndpointAgents.Core` | Endpoint engine: `IEndpointCommandHandler`, `CommandDispatcher`, execution context, result pattern, validation. |
| `src/EndpointAgents.Platform.Windows` | Real Windows features (endpoint info, internet, IBSng, printers, services, registry, software, VPN, Office/Windows activation and repairs). Most feature work goes here. |
| `src/EndpointAgents.Service` | Windows service (LocalSystem): owns the control-plane connection (SignalR + device token), reconnect, RPC -> `CommandDispatcher`, the only named pipe (Tray calls plus the attached Tray channel it pushes user-session tasks down). |
| `src/EndpointAgents.TrayWeb` | The Tray (default): WPF host + WebView2 + React UI in `ui/` for the interactive user session: helpdesk chat, Status & Control, Reports, in-app consent. Page reaches the PC only through the allow-listed `TrayBridge`. |
| `src/EndpointAgents.Tray.Runtime` | Shared Tray pieces: service channel (`tray.attach`), catalogued user-session task handlers, consent prompt, status, logging, tray icon. |
| `src/EndpointAgents.Tray` | Previous WinUI 3 Tray, kept until the WebView2 Tray is accepted (`-LegacyTray`). |
| `src/ControlPlane.Api` | ASP.NET Core Windows service on 5052: `AgentHub` (remote, authenticated) and `dashboard-hub` (loopback), REST, SQLite (WAL) chat/runs/audit, Hermes orchestration (`Hermes/`), loopback MCP at `/internal/mcp` (`Tools/`), hash-verified file repository (`Files/`). |
| `src/ControlPlane.Web` | Next.js operator dashboard on 127.0.0.1:3000; loopback-only, never served by the API. |
| `deploy/hermes/` | `SOUL.md` (agent identity/rules) and reviewed skills (`office-support`, `windows-support`, `ibsng-support`), promoted manually into the Hermes profile. |
| `deploy/control-plane/FileRepository` | Development copy of repair artifacts and their manifests. |

Flows:
- Chat: Tray -> service pipe -> Service -> `AgentHub` -> ControlPlane.Api (SQLite) -> Hermes `/v1/runs` (127.0.0.1:8642) -> MCP tools -> allow-listed RPC -> Service -> handler.
- Operator dispatch: dashboard -> API -> `IHubContext<AgentHub>` -> Service (Tray consent gate).

Authority boundary: ControlPlane.Api, not Hermes, owns device/user identity, consent, command allow-listing
and audit. Hermes never gets an `agentId`; a per-run capability resolves server-side to one device + user SID.
Read-only diagnostics run automatically; Windows/Office activation repairs run automatically only via their
dedicated pinned tools; administrator-reviewed catalog packages (schema 2, BAT or EXE) run automatically via
`run_repair_package` when their symptoms match; general catalogued actions (`EndpointActionCatalog`: Look, SafeFix,
Disruptive tiers) run via `run_endpoint_action` with kill switches, hourly limits and before/after checks, and Disruptive
ones only after the user agreed in chat; anything else goes through `propose_endpoint_action` and needs the user's Tray
approval.

## Build, run, test (no Visual Studio needed: .NET 10 SDK + Node)

```powershell
dotnet build EndpointAgents.slnx
dotnet test EndpointAgents.slnx
dotnet run --project src\ControlPlane.Api\ControlPlane.Api.csproj --launch-profile http   # API on :5052
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-endpoint-dev.ps1        # Service + Tray
```

Dashboard: `cd src/ControlPlane.Web; npm install; npm run dev` (http://127.0.0.1:3000), `npm test` for vitest.
Stop any installed `EndpointAgents.Service` and running Tray before the dev script.

## Rules

- Nothing Windows-specific in `Shared`; put something there only if both endpoint and server must understand it.
- `EndpointAgents.Service` is rarely edited; add features in `Platform.Windows` (and `Core` only for reusable engine pieces).
- The Tray never accepts credentials from the control-plane channel; the Service owns runtime state.
- A new general action is a catalogue entry in `Shared/Actions` plus an `IEndpointAction` in `Platform.Windows/Actions`
  (no shell, no agent-chosen paths or addresses), a symptom-skill mention and tests; see `docs/endpoint-actions.md`.
- Every other new fix (a script or package) ships as all of: an MCP tool in `ControlPlane.Api/Tools`, a hash-pinned (SHA-256) script in the
  file-repository manifest, a Hermes skill update in `deploy/hermes/skills`, and tests. Skill text never grants a
  tool permission; the ControlPlane and Hermes MCP allow-lists must agree.
- After any change to `deploy/hermes/SOUL.md`, a skill or the Hermes tool list, run `scripts\sync-hermes-profile.ps1`
  (`-Check` reports drift) so the local Hermes profile matches the repo.
- User-facing agent replies: simple Persian, at most three short sentences, no tool names, IDs, paths or JSON (see `deploy/hermes/SOUL.md`).
- Development keys in `appsettings.Development.json` are localhost-only test values; never commit real or production secrets, and never log run capabilities, device tokens or LLM keys.
- UI: follow `DESIGN.md` (dashboard: dense, light-first, OKLCH tokens; Tray: Persian RTL messenger in WebView2, metro-line motif, follows Windows light/dark) and `PRODUCT.md`.
- The Tray and its web page run only catalogued operations; never add a bridge or channel method that runs arbitrary commands or files.

## Docs

- `docs/endpoint-actions.md` - general action catalogue, tiers, environment map, agent tools, helpdesk skills
- `docs/control-plane-operations.md` - ports, routes, dev and production install, troubleshooting
- `docs/tray.md` - Tray architecture, one-pipe service channel, WebView2 bridge, preview and WebView2 install
- `docs/hermes-support-agent.md` - enrollment, Hermes install, MCP tools, consent lifecycle, verification checklist
- `docs/windows-activation.md`, `docs/office-activation.md`, `docs/office-bugfix.md`, `docs/office-inventory.md`, `docs/ibsng-support.md` - feature specifics
