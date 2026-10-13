# Installing Google Chrome

When a user asks for Chrome ("کروم روی سیستم نصب نیست"), the support agent runs the
`chrome-install` package through the `software-install` skill. It is an ordinary catalogued
package (see
[bat-packages.md](bat-packages.md)): a reviewed BAT plus Google's enterprise MSI, both
hash-pinned and signed, run by the endpoint service with no Tray approval prompt.

What the BAT does, on Windows 10 and 11 (64-bit):

1. Looks for a system-wide `chrome.exe` (Program Files and the `App Paths` registry key).
   If Chrome is there it changes nothing and exits with code 10.
2. Otherwise runs `msiexec /i GoogleChromeStandaloneEnterprise64.msi /qn /norestart` and
   checks again that `chrome.exe` exists. Exit 0 means installed; 3010 means installed and
   the PC wants a restart (the agent asks the user; nothing restarts automatically).
3. Anything else prints a line starting with `ERROR:` and the run counts as failed. The
   MSI log is `%ProgramData%\EndpointAgents\Logs\chrome-install.log` on the user's PC.

After the run the agent asks whether the problem is solved. On "no", or a failed run, it
escalates the conversation to the helpdesk, which lists it as unsolved. The rule lives in
the `software-install` skill (see [software-install.md](software-install.md)).

## Putting the MSI in place

The MSI (about 170 MB) is not in git. On the admin PC, with your signing key:

```powershell
# Development FileRepository in this repository:
.\scripts\update-chrome-package.ps1 -Development -Sign
# The server's FileRepository, after copying the package folder there:
.\scripts\update-chrome-package.ps1 -Sign
```

The script downloads
`https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise64.msi`, refuses it
unless Windows verifies Google LLC's signature, copies it to
`packages\chrome-install\files\`, pins the new hashes and signs the package. If the admin PC
cannot reach Google, download the MSI on another PC and pass it with
`-Msi <path>\GoogleChromeStandaloneEnterprise64.msi`.

Run it again to move endpoints to a newer Chrome. Chrome updates itself afterwards, so this
only matters for new installs.

## Size limits

The MSI is larger than the old 100 MB file limit. The control plane now serves files up to
`FileRepository:MaxFileSizeBytes` = 500 MB (an existing server keeps the value in its own
`appsettings.json`, so raise it there), and endpoints accept supporting files up to 500 MB
with a 10-minute download timeout. The whole run, download included, must finish within
the 10-minute repair limit.

## Not covered

- 32-bit Windows: the BAT stops with an error; the user is escalated.
- Per-user Chrome in the user's own profile is not detected, because the package runs as
  LocalSystem. The MSI then adds a system-wide copy, which is harmless.

## Resetting Chrome settings

When a user asks to reset Chrome, or describes a hijacked homepage or search engine or
unwanted pop-ups, the agent explains what will change, waits for the user's "yes" in the
chat, and runs the `chrome-reset` package (rule: the `chrome-reset-settings` entry in
`deploy/hermes/skills/repair-packages/SKILL.md`). It works like Chrome's own
Settings > Reset settings > "Restore settings to their original defaults":

1. Checks that Chrome is installed (system-wide or in the user's profile); otherwise exit 20
   and the agent offers to install it.
2. Closes the user's Chrome windows.
3. For every Chrome profile of that user, sets `Preferences` and `Secure Preferences`
   aside as `*.before-reset`. Chrome recreates them with default startup pages, search
   engine, new tab page, theme, site settings and extensions.

Bookmarks, history, saved passwords and cookies are kept. Unlike Chrome's own reset,
extensions are removed rather than disabled, and the user may have to sign in to Chrome
again (sync then brings back synced settings). To undo, close Chrome and rename the
`*.before-reset` files back.

The package runs as LocalSystem like every package. The endpoint tells it which user asked
through `ENDPOINTAGENTS_USER_SID`, `ENDPOINTAGENTS_USER_PROFILE` and
`ENDPOINTAGENTS_USER_LOCALAPPDATA` (see [bat-packages.md](bat-packages.md)); without them the
package refuses to run.
