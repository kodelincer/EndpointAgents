# Tray

The Tray is the user's window into the helpdesk. It runs in each signed-in user's session, shows
the chat with the support assistant, the PC's connection status, and a log viewer, and carries out
the few things the service cannot do from session 0 (open the IBSng dialer, ask the user to approve
a repair, run the Windows Hello sample).

There are two front ends during the transition:

| Project | What it is | Status |
| --- | --- | --- |
| `src/EndpointAgents.TrayWeb` | WPF host + WebView2 + React UI (`ui/`) | Default |
| `src/EndpointAgents.Tray` | WinUI 3 window | Kept until the WebView2 Tray is accepted, then removed |

Both share `src/EndpointAgents.Tray.Runtime`: the service channel, the catalogued task handlers,
status resolution, event logging and the notification-area icon. They use the same single-instance
names, so only one Tray of either kind runs per session.

## Service channel (one pipe)

The service hosts the only pipe, `EndpointAgents.Service.Status.v1`. The Tray never listens.

- Short calls (status, chat, pause/resume, IBSng diagnose) are one request and one response.
- At start the Tray also opens a long-lived connection with `tray.attach`. The service reads the
  caller's Windows session from the kernel (`GetNamedPipeClientSessionId`) and the caller's user
  from its token, and accepts the attach only if that user is the one signed in to that session
  (`WTSQueryUserToken`). Session 0 is refused. A service started interactively for development
  accepts only its own user.
- The service then writes catalogued `TrayIpcRequest` lines down that connection and the Tray
  answers each with a `TrayIpcResponse` carrying the same id. The newest attach for a session wins.
  If the Tray disconnects, every pending task fails immediately with `TrayUnavailable`; a task sent
  while no Tray is attached waits up to `TrayConnectTimeoutSeconds` for one.
- The Tray runs only the methods in `TrayIpcProtocol` and refuses anything else. There is no generic
  command channel in either direction.

## WebView2 host

`EndpointAgents.TrayWeb` is a frameless WPF window filled by WebView2. The page draws its own title
bar; the host handles minimize, maximize, close-to-tray and dragging.

- The UI is served from the app folder through a virtual host (`https://tray.endpointagents.internal/`).
  Navigation elsewhere, new windows, downloads and permission requests are refused, and a CSP limits
  scripts to the bundle. DevTools and browser shortcuts are off unless `ENDPOINTAGENTS_TRAY_DEVTOOLS=1`.
- The page reaches the PC only through `TrayBridge`, a fixed list of methods (chat, status, service
  control, logs, clipboard, consent answers, window commands). Unknown methods are refused.
- Service consent requests appear as an approval sheet in the window. If the window cannot render,
  the Tray falls back to a Windows message box. Anything other than an explicit approval is "no",
  and a request the service withdraws (timeout or disconnect) closes its sheet.
- WebView2 keeps its profile in `%LOCALAPPDATA%\EndpointAgents\Tray\WebView2`.

## Building and previewing

Node.js is a build-time dependency only: `dotnet build` runs `npm ci` (first time) and `npm run build`
in `ui/` and copies `ui/dist` to `wwwroot`. Pass `-p:SkipTrayUi=true` to use an existing `ui/dist`.

```powershell
cd src\EndpointAgents.TrayWeb\ui
npm run dev        # http://127.0.0.1:5174 with sample data
npm test           # vitest
```

Review the real window with sample data (no service needed):

```powershell
EndpointAgents.TrayWeb.exe --ui-preview [--dark|--light] [--section chat|status|logs] [--size 760x640] [--snapshot out.png]
```

`scripts\run-endpoint-dev.ps1` starts the service and the WebView2 Tray; add `-LegacyTray` for the
WinUI one.

## Installing

`scripts\install-endpoint.ps1` publishes the WebView2 Tray unless `-LegacyTray` is given. It first
checks for the Microsoft Edge WebView2 Runtime (built in on Windows 11, usually present on Windows 10
through Windows Update). When it is missing, the script runs Microsoft's x64 Evergreen Standalone
Installer from `deploy\endpoint\MicrosoftEdgeWebView2RuntimeInstallerX64.exe` (or
`-WebView2InstallerPath`) after verifying its Microsoft Authenticode signature. The installer is not
committed; download it from https://developer.microsoft.com/microsoft-edge/webview2/.
