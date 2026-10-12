# IBSng endpoint support

The first supported slice provides reliable diagnostics and controlled dialer
delivery. It deliberately does not send passwords or automate the desktop from
the Windows Service.

## What is available

- `network.ibsng_diagnose` verifies the exact `IBSngDialer-pap(1).exe` file on the
  active user's desktop, including its pinned SHA-256 and executable signature.
- It reports adapter addresses, DHCP configuration, default gateway, DNS
  resolution, the selected route to the IBSng server, protocol-aware endpoint
  checks, the configured portal, and Windows RAS/VPN state. A failed gateway
  ping is a warning because ICMP may be blocked.
- The active user's Tray attempts to read the visible **Connection Log** through
  Windows accessibility APIs. Log text is bounded and sanitized. Windows RAS is
  still authoritative when the dialer's custom skin does not expose that control.
- `network.ibsng_ensure_dialer` installs the approved dialer when it is missing.
  Before installation it closes extra IBSng processes and moves duplicate,
  renamed, or modified desktop copies to the ProgramData quarantine folder.
- `network.ibsng_launch` asks the active user's Tray to launch the verified file.
  It keeps one approved canonical process, closes extra dialer instances, and
  never accepts or forwards a username or password.
- Duplicate cleanup runs when Ensure or Launch is requested. Status reports
  duplicates without changing the desktop.

## Configure the file repository

1. On the control-plane machine, create:

   `C:\ProgramData\EndpointAgents\ControlPlane\FileRepository`

2. Put the approved `IBSngDialer-pap(1).exe` file there. Other similarly named
   executables are ignored.
3. Get the approved hash in PowerShell:

   ```powershell
   Get-FileHash 'C:\ProgramData\EndpointAgents\ControlPlane\FileRepository\IBSNGdialer-pap(1).exe' -Algorithm SHA256
   ```

4. Set that exact hash in the endpoint service's `appsettings.json`:

   ```json
   {
     "Diagnostics": {
       "Ibsng": {
         "ServerHost": "192.168.10.4",
         "PortalUrl": "",
         "DialerFileName": "IBSngDialer-pap(1).exe",
         "DialerSearchPattern": "IBSngDialer*.exe",
         "TrustedSha256": "D3E25EDB352BD2C61E521DB363660EB272EA48C765F22B40818D97907D99F080"
       }
     }
   }
   ```

5. Restart the control-plane and endpoint services. The dashboard command menu
   will show **Diagnose IBSng connection**, **Ensure IBSng dialer**, and
   **Launch IBSng dialer**.

The trusted hash is stored on the endpoint so the endpoint can independently
prove that the downloaded executable is exactly the administrator-approved
binary. A hash returned only by the download server would not be sufficient: a
server or network path that replaced the executable could also replace that
server-provided hash. The endpoint refuses delivery when its trusted hash is
blank or different from the downloaded file's calculated SHA-256.

The control plane exposes read-only metadata at
`GET /api/files/ibsng-dialer/metadata` and the executable at
`GET /api/files/ibsng-dialer`. There is intentionally no remote upload endpoint
while the system has no authentication or authorization.

## Launch and credential boundary

A Windows Service runs in Session 0 and cannot safely interact with the logged-in
user's dialer window. The service verifies the executable and sends the launch
task down the Tray channel attached for that user's session (see `docs/tray.md`), and the
Tray launches it unelevated in the interactive user session. The user enters username and password directly in the
dialer; credentials never enter Control Plane requests or activity history.

The supplied IBSng executable is unsigned and uses an old skinnable interface.
The Tray therefore uses UI Automation first and a bounded Win32 text-control
fallback. It never uses screen coordinates, screenshots, OCR, the clipboard, or
password controls. `LogControlNotAccessible` is an expected result for a dialer
version whose custom log is entirely owner-drawn.

## Helpdesk workflow

1. Confirm EndpointAgents Tray is running in the active user's session.
2. Run `endpoint.get_info`.
3. Enter the server/domain and protocol in the Tray's IBSng guided workflow, then
   run `network.ibsng_diagnose` locally or from the command catalog.
4. Run `network.ibsng_ensure_dialer` when the artifact is missing, duplicated,
   or untrusted.
5. Run `network.ibsng_launch`.
6. Tell the user to enter credentials directly and click Connect.
7. Re-run endpoint info and IBSng diagnosis after the user confirms connection.
