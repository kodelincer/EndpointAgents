# Safe operation catalog for Windows helpdesk work

This is the master list of what the support system should be able to do on a user's PC,
so that a helpdesk team mostly writes or edits **skills** (the order in which the agent
checks and fixes things) instead of asking developers for new code. It covers the tickets
that almost every company on Windows 10/11 and desktop Office receives, maps each one to
small, fixed, safe operations, and marks what is already built and what is still missing.
**Microsoft 365 is out of scope** (Reza, 2026-10-20): no Teams, OneDrive, Exchange Online,
Entra ID or Microsoft 365 sign-in operations are planned.

It extends, and does not replace, the existing pieces:

- **Actions**: compiled operations in `EndpointActionCatalog` (see `docs/endpoint-actions.md`).
- **Packages**: administrator-signed, hash-pinned BAT/EXE files run by the package runner
  (see `docs/bat-packages.md`), including install packages (`docs/software-install.md` on
  branch `claude/chrome-install-package`).
- **Server tools**: things the control plane checks itself (incidents, AD account state,
  server-side probe, site knowledge).

## How to read the tables

| Column | Values |
| --- | --- |
| **Op id** | The operation's id. New ones follow the existing `area.verb` style. |
| **Kind** | `A` compiled action, `P` signed package, `S` server-side tool, `L` an admin-maintained list the operation reads |
| **Tier** | `Look` reads only. `Fix` = SafeFix: small and reversible, runs without asking. `Disrupt` = interrupts the user, runs after they say yes in chat. `IT` = **new tier**: waits until a helpdesk member approves it in the dashboard. `Op` = helpdesk only, never offered to the agent. |
| **Status** | `Built` exists in code (branches `claude/integration` and `claude/ops-catalog`). `Pkg` exists as a package. `New` must be built. `Batch n` = planned implementation batch, see the end. |

## The safety model, and what this catalog adds to it

Everything already agreed still holds: the endpoint runs only catalogued operations the
control plane sends; no shell, no agent-chosen paths, URLs, commands or addresses; typed
parameters only; kill switch and hourly limit per operation; before/after verification;
undo for user settings; masking before anything reaches the cloud model; full audit. Two
additions are needed for the requests in this catalog:

1. **IT approval tier (`IT`).** For operations that are not dangerous but that the company
   wants a person to decide: removing software, rolling back a driver, adding a user to a
   local group, resetting a whole network stack, BitLocker recovery, and anything an admin
   marks "needs approval". The agent asks for it; the control plane stores an *approval
   request* (who, which PC, what, why, the conversation); a helpdesk member approves or
   declines it on the dashboard **Approvals** page; on approval the control plane runs it
   through the same checks as every other action and tells the user the result in their
   Tray chat. An admin can move any operation into or out of this tier on the
   Capabilities page (for example "require approval for every install of licensed
   software").
2. **Admin-maintained lists (`L`).** Wherever a value cannot be a free choice, the admin
   keeps a list in the dashboard and the agent may only pick an id from it:
   - **Software catalog**: what users may ask to install (one install package each), with
     a "licensed / needs approval" flag.
   - **Removable software**: which installed applications may be removed, matched by name
     and publisher, and whether removal needs approval (default: yes).
   - **Driver catalog**: device models the company owns (printers, scanners, docks,
     card readers, signature pads, ...), their hardware IDs and the signed driver package
     for each Windows version.
   - **VPN profiles**: the company's VPN connections (built-in Windows VPN settings, or the
     client app and service name of FortiClient, Cisco Secure Client, GlobalProtect,
     OpenVPN, WireGuard, ...).
   - **Wi-Fi profiles**: company SSIDs and their profile (no shared passwords for 802.1X;
     PSK networks are kept server-side and never shown to the model).
   - **Environment map** (built): internal web apps, shares, printers, servers, proxy
     standard, time zone, root CAs, internal DNS suffixes.
   - **Allowed sites** (category list): which internet sites/apps users need for work, so
     the agent can tell "blocked on purpose" from "broken".

Rule of thumb for new operations: pick the lowest tier that fits, prefer a compiled action
when the step is small and generic, and prefer a signed package when the step is
vendor-specific or changes often (an installer, a vendor's repair tool).

---

## 1. Network and internet

The most frequent ticket in every company. The agent should always split it into
"this PC", "this site/floor" (others report the same, the gateway does not answer) and
"the internet link / an upstream service" before fixing anything.

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| "No internet", "no network" | `network.status`, `network.triage` | A | Look | Built |
| Cable unplugged, adapter disabled | `network.adapters` (link, speed, duplex, enabled, driver date, media state) | A | Look | Built |
| Adapter disabled by the user | `network.adapter_enable` (only a physical adapter the PC reported) | A | Fix | Built |
| Adapter stuck after sleep | `network.adapter_restart` (disable+enable one reported adapter) | A | Disrupt | Built |
| 169.254 address, no DHCP | `dhcp.renew` | A | Fix | Built |
| DNS cache stale, site moved | `dns.flush`, `dns.resolve` | A | Fix/Look | Built |
| DNS servers set by hand to wrong values | `dns.reset_to_dhcp` (only when the environment map says DHCP gives DNS) | A | Fix | Built |
| Static IP set by hand on a DHCP network | `ip.reset_to_dhcp` (stores the old values for undo) | A | Fix | Batch 2 |
| IP address conflict | `network.status` + `events.summary network` | A | Look | Built |
| PC not registered in DNS | `net.register_dns` | A | Fix | Built |
| Stale neighbour cache | `arp.clear` | A | Fix | Built |
| Wrong or broken proxy | `proxy.get`, `proxy.reset_to_standard`, undo `proxy.restore` | A | Fix | Built |
| WinHTTP (system) proxy wrong: Windows Update or Office updates fail but browser works | `proxy.system_get`, `proxy.system_reset_to_standard` | A | Look/Fix | Built |
| hosts file hijacked | `hosts.check`, `hosts.reset_default` (keeps a copy for undo) | A | Look/Fix | Built/Batch 2 |
| Winsock / TCP-IP stack broken (malware leftovers, VPN clients) | `network.stack_reset` (winsock + ip reset, needs restart) | A | IT | Batch 2 |
| Wi-Fi not connecting, wrong network | `wifi.status` (SSID, signal, band, auth, profile list), `wifi.connect_company` (profile from the Wi-Fi list) | A+L | Look/Fix | Batch 2 |
| Forgotten or corrupted Wi-Fi profile | `wifi.forget_profile` (only a company or user-named SSID), then `wifi.connect_company` | A+L | Fix | Batch 2 |
| Wi-Fi turned off / airplane mode | `radio.status`, `radio.wifi_on` | A | Look/Fix | Batch 2 |
| Slow internet | `network.adapters` (speed), `net.ping` gateway and target, `web.probe` timing, `recent_reports_from_others` | A+S | Look | Built |
| Firewall blocks an internal app | `firewall.status` (profile, on/off, domain profile active), `firewall.reset_profile_to_domain` | A | Look/IT | Batch 2 |
| Network profile is Public on the office LAN | `network.category` (Public/Private/Domain), `network.set_private` (not on Domain networks) | A | Look/Fix | Batch 2 |
| Captive portal / hotel Wi-Fi (laptops) | `web.probe` to the captive-check target, guide the user | A | Look | Built |
| Outage at the site or upstream | `check_known_incidents`, `recent_reports_from_others`, `probe_from_server` | S | Look | Built |

### VPN (remote users, branch offices, or companies that reach the internet through VPN)

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| "VPN does not connect" | `vpn.status`: listed profiles present, connected or not, last error from RasClient / the client's service, server reachable on its port | A+L | Look | Built |
| Built-in Windows VPN profile missing or broken | `vpn.ensure_profile` (creates or repairs the profile exactly as the VPN list defines it; no credentials) | A+L | Fix | Batch 2 |
| Built-in VPN: connect / disconnect | `vpn.connect`, `vpn.disconnect` (listed profile only; the user types any password in Windows' own prompt) | A+L | Fix | Batch 2 |
| Third-party VPN client stuck (FortiClient, Cisco, GlobalProtect, OpenVPN, WireGuard) | `vpn.client_restart`: restarts only the service named in the VPN list | A+L | Fix | Built |
| VPN client missing or outdated | install package from the software catalog | P | Fix | Pkg pattern exists |
| RAS service stopped | `service.restart` (`RasMan` is in the look list; add to restartable) | A | Fix | Built |
| Connected but no internal names | `vpn.status` (DNS suffix and DNS servers on the VPN adapter), `dns.resolve`, `network.triage` | A | Look | Built |
| IBSng dialer (this organisation) | existing IBSng tools | A | Look/Fix | Built |

## 2. Access to websites, internal apps and the internet

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Internal web app does not open | `network.triage`, `web.probe` | A | Look | Built |
| "Is this site blocked on purpose?" | `site.check_access`: compares the site with the **Allowed sites** list and the proxy/firewall policy, then probes it | A+L | Look | Batch 2 |
| User needs a new site opened | not a PC fix: `request_site_access` creates an approval request for the network team | S | IT | Batch 2 |
| Certificate warning on an internal site | `cert.check_trust` (org root CAs present), `cert.install_org_roots` (only thumbprints in the environment map, from the control plane) | A | Look/Fix | Built/Batch 2 |
| Wrong date/time breaks HTTPS | `time.status`, `time.resync` | A | Fix | Built |
| Keeps asking for a password on an intranet site | `zones.status` (is the site in Local intranet / Trusted), `zones.add_company_site` (environment-map web apps only) | A | Look/Fix | Batch 2 |
| Old app needs IE mode / compatibility | `edge.ie_mode_status`; the site list itself stays in Group Policy | A | Look | Batch 3 |
| Browser broken, slow, pop-ups | `browser.inventory`, `browser.clear_cache` | A | Fix | Built |
| Browser settings hijacked, extensions | `browser.extensions_list`, `browser.reset_settings` (keeps bookmarks, passwords, history; per browser) | A/P | Look/Disrupt | `chrome-reset` package on chrome-install-package; Batch 2 for Edge |
| Default browser / PDF app wrong | `defaults.status`, guide the user (Windows blocks silent changes since Windows 10) | A | Look | Batch 3 |
| Saved password for a site is wrong | `credman.list`, `credman.remove` | A | Fix | Built |

## 3. Shared folders and network drives

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Drive letter missing or red X | `drives.list`, `drive.remap` | A | Fix | Built |
| "Network path not found" | `share.test`, `network.triage` | A | Look | Built |
| Access denied on a share | `share.test` + `check_user_account` + `whoami.groups` (group membership as seen by the PC, masked) | A+S | Look | Built |
| New group membership not applied yet | `kerberos.purge`, then sign out/in | A | Fix | Built |
| Offline Files out of sync | `offline_files.status`, `offline_files.sync_now` | A | Look/Fix | Batch 3 |
| Deleted or overwritten a file on a share | `previous_versions.list` (shadow copies of that file's folder, a share from the map) — restore stays with the helpdesk | A | Look | Batch 3 |
| Profile or desktop looks empty (temp profile) | `profile.status` | A | Look | Built |

## 4. Printers and scanners

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Cannot print, job stuck | `printer.list`, `spooler.restart_and_clear` | A | Fix | Built |
| Printer missing / add an office printer | `printer.add` (environment-map printers) | A | Fix | Built |
| Wrong default printer | `printer.set_default` | A | Fix | Built |
| Printer offline | `network.triage` to the printer, `printer.status` (port, driver, offline flag, paper/toner when the driver reports it) | A | Look | Built |
| Remove an old printer | `printer.remove` | A | Fix | Built |
| Test page | `printer.test_page` | A | Fix | Built |
| Printer driver missing or wrong for the model | `drivers.inventory` catalog match + driver package (see section 6) | A+L | Fix | Built |
| Print queue of a shared print server stuck | not a PC fix: escalate with the queue name | S | — | — |
| Scanner not found | `services.status stisvc`, `devices.problems`, the scanner's driver package | A+L | Fix | Built |
| Scan to folder / scan to email fails | `share.test` to the scan share; SMTP is on the device side, escalate | A | Look | Built |
| USB printer not detected | `drivers.inventory`, `devices.rescan`, `usb.status` | A | Fix | Built (`usb.status`: Batch 2) |

## 5. Peripherals and devices

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| A device has a yellow mark / not working | `devices.problems` (problem code, hardware IDs, model) | A | Look | Built |
| Device not detected | `devices.rescan` | A | Fix | Built |
| Device disabled | `device.enable` (only a device the PC reported as disabled) | A | Fix | Built |
| Device hung (webcam, card reader, dock NIC) | `device.restart` (disable+enable one reported device; never storage, display or input devices in use) | A | Disrupt | Built |
| USB ports dead, USB power saving | `usb.status` (hubs, selective suspend), `power.usb_suspend_off` | A | Look/Fix | Batch 2 |
| Mouse/keyboard (Bluetooth) drops | `bluetooth.status`, `service.restart bthserv` | A | Look/Fix | Batch 2 / Built |
| Webcam or microphone not working in meetings | `media.status`, `privacy.allow_camera_mic` | A | Fix | Built |
| Headset / speakers: no sound, wrong output | `audio.status` (default devices, mute, volume), `audio.set_default` (a reported device), `service.restart Audiosrv` | A | Look/Fix | Built |
| External monitor / projector not detected | `display.status`, `display.detect` | A | Look/Fix | Built/Batch 2 |
| Docking station issues | `devices.problems`, driver/firmware package from the driver catalog | A+L | Fix | Built |
| Smart card / token / signature pad / barcode scanner (company specific) | driver catalog entry + vendor package; `smartcard.status` (readers, certificate present) | L+P/A | Fix/Look | Batch 2 |
| Mobile phone / USB storage blocked | policy, not a fault: explain, escalate if the user needs an exception | — | — | — |

## 6. Drivers

Drivers are where a "free" agent could do the most damage, so the agent never picks a
driver file. It only says "this device needs its driver"; the control plane decides which
signed package that is from the **Driver catalog** and the device's hardware ID.

| Need | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Which devices lack a driver, with hardware IDs and model | `drivers.inventory` (device, hardware/compatible IDs, current driver, version, date, provider, signer) | A | Look | Built |
| Which catalog driver fits this PC | built into `drivers.inventory`: the server marks each device whose hardware ID is in the Devices list (`catalogMatch` with the model and its driver package) | S+L | Look | Built |
| Install a catalog driver | the device's signed driver package through `run_repair_package` (hash and admin signature checked; the BAT from `deploy/package-templates/install-driver.bat` runs `pnputil /add-driver <inf> /install`, and Windows checks the driver's own signature) | P+L | Fix | Built |
| Install a printer driver for an office printer | same, then `printer.add` | P+A | Fix | Built |
| A new driver broke the device | `driver.rollback` (to the previous driver in the driver store for that device) | A | IT | Batch 2 |
| Remove a bad third-party driver | `driver.remove_package` (only an oem*.inf the inventory reported, not in use by a boot device) | A | IT | Batch 3 |
| An unknown device appears (new model) | escalate with model and hardware IDs so an admin adds it to the driver catalog; logged as a feature request | S | — | Built |

## 7. Software: install, update, repair, remove

| Need | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| What is installed | `apps.find` (search by name) | A | Look | Built |
| Full installed list for the helpdesk | `software.inventory` (name, version, publisher, scope, install date; MSI product code when present) | A | Look | Built |
| Install an approved application | install package from the **Software catalog** (Chrome, Adobe Connect, Foxit, Photos, fonts exist) | P+L | Fix (or IT when the catalog entry says licensed) | Pkg |
| User asks for software not in the catalog | feature request ("save for the dev team") or approval request "add this to the catalog" | S | — | Built (feature requests) |
| Remove software | `software.uninstall`: only an application in the **Removable software** list that the inventory found; MSI via its product code (`msiexec /x {code} /qn`), others via the uninstall package the list names | A/P+L | IT | Built |
| Update an application | same install package (installers upgrade in place) or a separate update package | P | Fix | Pkg pattern |
| Repair an application (Office, Visual C++ runtimes, .NET) | `software.repair_msi` (`msiexec /fa {code} /qn`, removable/repairable list only), Office Quick Repair package | A/P | Fix | Batch 2 |
| Store apps broken (Calculator, Photos, Snipping Tool) | `appx.reregister` (built-in Windows apps from a fixed list, for the user) | A | Fix | Batch 2 |
| winget available (Windows 10 1809+ / 11) | a winget-source package type: `winget install --id <catalog id> --source <company source>` from a pinned, company-hosted source only | P+L | Fix | Batch 3 (optional) |

## 8. Desktop Office and Outlook (no Microsoft 365)

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Office not activated / unlicensed | Office activation tools and packages | A/P | Fix | Built |
| Office app crashes, add-in problems | `office.addins_list` (per app, load behaviour, crashes), `office.addin_disable` (one reported add-in, for the user, undo kept) | A | Look/Fix | Batch 2 |
| Office repair | `office.quick_repair` (Click-to-Run `OfficeClickToRun.exe scenario=Repair` quick, silent) | A | Disrupt (closes Office) | Batch 2 |
| Office updates stuck | `office.update_status`, `office.update_now` | A | Look/Fix | Batch 3 |
| Outlook not connecting | `outlook.status` | A | Look | Built |
| Outlook slow, OST huge or corrupted | `outlook.data_files` (OST/PST sizes, last write), `outlook.rebuild_ost` (renames the OST after Outlook is closed; rebuilt from the server) | A | Look/Disrupt | Batch 2 |
| Outlook profile broken | `outlook.new_profile` (creates a fresh profile with autodiscover, keeps the old one) | A | Disrupt | Batch 3 |
| Shared mailbox / calendar missing | escalate (Exchange permission, not a PC fault) | S | — | — |

Teams, OneDrive and Microsoft 365 sign-in are not part of this system. `teams.clear_cache`
and `onedrive.status` were built earlier; the skills no longer use them, and an admin can
switch them off on the Capabilities page.

## 9. Accounts, sign-in and passwords

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Account locked / password expired / disabled | `check_user_account` (read-only) | S | Look | Built |
| Password reset / unlock | **never on the PC by the agent**. Optional later: a self-service reset through a separate verified flow, or an approval request to the helpdesk with the AD action done by a person | S | IT | Batch 3 (design only) |
| "Trust relationship failed" | `domain.health` (secure channel); repair needs a domain admin: approval request | A+S | Look/IT | Built/Batch 3 |
| Cached credentials wrong after password change | `credman.list`, `credman.remove`, `kerberos.purge` | A | Fix | Built |
| Group Policy not applied | `gpo.refresh_computer`, `gpo.refresh_user`, `gpo.result_summary` (applied GPO names, last refresh, errors) | A | Fix/Look | Built/Batch 2 |
| Windows Hello / PIN broken | `hello.status`, `hello.reset_pin_container` (user re-enrolls) | A | Look/Disrupt | Batch 3 |
| Local admin rights request | not automated: approval request, time-limited group membership done by IT (or LAPS) | S | IT | Batch 3 |
| Who am I / which groups | `whoami.groups` | A | Look | Built |

## 10. Performance, disk, startup, crashes

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| PC is slow | `perf.snapshot`, `system.recent_changes`, `events.summary` | A | Look | Built |
| Disk full | `disk.usage` (free space per drive, largest known folders: temp, downloads size only, recycle bin, Windows Update cache, user profile size), `disk.cleanup_safe` | A | Look/Fix | Built |
| Empty the recycle bin | `recyclebin.empty` (for the user, after a chat yes) | A | Disrupt | Batch 2 |
| Too many startup apps | `startup.list`, `startup.disable` (an entry the PC reported, for the user; undo kept) | A | Look/Fix | Batch 2 |
| An app hangs | `app.close` | A | Disrupt | Built |
| Process eats CPU/RAM | `perf.snapshot` (top processes) | A | Look | Built |
| System files corrupted | `system.file_check` (`sfc /scannow`, then `DISM /RestoreHealth` against Windows Update or the company source) | A | Fix (long) | Batch 2 |
| Disk errors | `disk.health` (SMART predict-failure, chkdsk dirty bit, media type), schedule `chkdsk` on restart | A | Look/IT | Batch 2 |
| Blue screens / unexpected restarts | `events.summary bluescreen`, `crash.summary` (bugcheck codes, faulting driver names from minidumps' metadata) | A | Look | Built/Batch 3 |
| Search not finding files | `service.restart WSearch`, `search.rebuild_index` | A | Fix | Built/Batch 2 |
| Pending restart for days | `restart.pending` (CBS, Windows Update, file renames, uptime), `reboot.schedule` | A | Look/Disrupt | Built |

## 11. Windows Update, security and encryption

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Updates failing or stuck | `windows_update.status`, `windows_update.reset_components` (stops BITS/wuauserv, renames SoftwareDistribution and catroot2, starts again) | A | Look/Fix | Built/Batch 2 |
| Check for updates now | `windows_update.scan_now` | A | Fix | Batch 2 |
| Antivirus off / outdated | `security.status`, `defender.update_signatures`, `defender.quick_scan` | A | Look/Fix | Built/Batch 2 (`update_signatures`) |
| "I think I have a virus" | `defender.quick_scan`, `defender.threats` (recent detections), `defender.full_scan` (long, user agrees) | A | Fix/Disrupt | Built/Batch 2 |
| A threat was found | `defender.threats`; removal follows Defender's own action; escalate always | A+S | Look | Batch 2 |
| BitLocker recovery screen | **key is never given by the agent**: approval request; a person verifies the user and reads the key from AD | S | IT | Batch 3 |
| BitLocker status for audits | `bitlocker.status` (protection on, method; no keys) | A | Look | Batch 2 |
| Phishing email reported | escalate to security with the message details the user gives | S | — | — |

## 12. Desktop, display, sound, language and region

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Taskbar or Start frozen | `explorer.status`, `explorer.restart` | A | Disrupt | Built |
| Persian keyboard missing | `input.languages`, `input.add_layout` | A | Fix | Built |
| Persian text shows as boxes (fonts) | install package `persian-fonts-install` | P | Fix | Pkg on chrome-install-package |
| Date, calendar or number format wrong | `region.status`, `region.set_standard` (company standard formats, for the user) | A | Look/Fix | Built/Batch 2 |
| Wrong time zone | `timezone.set_standard` | A | Fix | Built |
| Screen too small/large, resolution, scaling | `display.status`, guide the user (per-user scaling changes need a sign-out) | A | Look | Built |
| Second monitor not shown | `display.detect` (re-enumerate), `display.status` | A | Fix | Batch 2 |
| No sound | `audio.status`, `audio.set_default`, `audio.unmute` | A | Look/Fix | Built |
| Desktop icons or wallpaper lost | `profile.status` (temp profile), escalate | A | Look | Built |
| File opens in the wrong app | `defaults.status` and guide | A | Look | Batch 3 |
| Notifications / Focus assist hides app alerts | `notifications.status` | A | Look | Batch 3 |

## 13. Power, battery and laptops

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Laptop battery drains, not charging | `battery.report` (design vs full charge capacity, cycle count, charging state) | A | Look | Batch 2 |
| PC sleeps too fast / does not sleep | `power.status`, `power.set_plan_standard` (company power plan) | A | Look/Fix | Built/Batch 2 |
| Wakes up by itself | `power.wake_sources` (last wake, wake timers, devices allowed to wake) | A | Look | Batch 3 |
| Fast startup causes stale state | `power.fast_startup_off` | A | Fix | Batch 3 |

## 14. Remote work and collaboration

| Common ticket | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| Remote Desktop to my office PC fails | `rdp.status` (enabled, listening, firewall rule, user in Remote Desktop Users) | A | Look | Batch 3 |
| The helpdesk wants to see my screen | start the company's remote-support tool from the software catalog (Quick Assist, AnyDesk, ...) — the user accepts in that tool | P | Disrupt | Batch 3 |
| Clock or time wrong | `time.status`, `time.resync` | A | Fix | Built |

## 15. Information the user (or the helpdesk) asks for

| Request | Op id | Kind | Tier | Status |
| --- | --- | --- | --- | --- |
| PC name, IP, Windows version, serial, model, uptime | `endpoint_system_info` (masked for the model) | A | Look | Built |
| Warranty / asset tag | system info (BIOS serial, model) + site knowledge | A+S | Look | Built |
| Activation state | Windows/Office activation status | A | Look | Built |
| Full inventory for an asset report | `software.inventory`, `drivers.inventory`, `disk.health`, `battery.report` | A | Look | Built/Batch 2 |

## 16. Things the system should never do by itself

These stay with people, even when an operation could technically do them. The agent's job
is to recognise them quickly, collect the facts and hand over (escalation or an approval
request), so the helpdesk does not have to ask the user again.

- Reset or reveal any password, PIN, BitLocker key or Wi-Fi PSK.
- Add anyone to Administrators or any privileged group, or turn off UAC.
- Turn off Defender, the firewall, BitLocker, Windows Update or any security policy.
- Install anything not in the software catalog, from the internet or from a file the user
  points at; run any file not signed by the company's package key.
- Change Group Policy, the registry outside a catalogued operation, or another user's
  profile.
- Delete user files (cleanup only touches caches and temp folders it owns).
- Format, partition or repair disks; flash BIOS/firmware unless a signed vendor package
  from the driver catalog says so and IT approved it.
- Hardware faults (broken screen, keyboard, dead battery, no power): escalate with model,
  serial and the symptom.

---

## Gap list against the current code

What already exists (branch `claude/integration`, which carries PR #2, the package runner,
system info, org knowledge and the docs tab): 65 catalogue actions over network, domain,
printers, drives, browsers, Office, health and desktop; signed hash-pinned BAT/EXE packages
with playbooks and fallbacks; install packages and a software-install skill on the separate
branch `claude/chrome-install-package` (not yet merged anywhere); escalation, feature
requests, incidents, server probe, AD account check, masking, kill switches and limits.

Missing, in order of how often companies hit them:

1. **IT approval tier and Approvals page.** Needed before anything that removes or rolls
   back. Nothing in the code today lets a helpdesk member approve a step that then runs on
   the user's PC and reports back into their chat.
2. **Software catalog and removal.** Install packages exist (unmerged); there is no
   inventory of installed apps beyond `apps.find`, no removable-software list, and no
   uninstall at all.
3. **Driver catalog.** No list of company device models, no hardware-ID matching, no
   driver install path (`pnputil`), no rollback. `devices.problems` does not report
   hardware IDs yet.
4. **VPN.** Only IBSng. No status for built-in VPN profiles or third-party clients, no VPN
   list, `RasMan` cannot be restarted.
5. **WinHTTP proxy, adapters, DNS reset.** Common reasons for "browser works, Windows
   Update doesn't" and for "no internet" after a user changes settings.
6. **Audio, devices enable/restart, printer status, disk usage, pending restart, group
   membership.** Small Look/Fix actions that close many tickets.
7. **Wi-Fi, firewall profile, network category, Allowed sites.**
8. **Office add-ins, OST rebuild, Windows Update reset, SFC/DISM, Defender
   signatures/threats, BitLocker status, startup apps.**
9. **Approval flows for people-only work** (local admin, BitLocker key, site access, AD
   unlock) — the approval request record is the same as in point 1, just with "done by a
   person" instead of "run on the PC".

## Implementation order

**Batch 1 — the four requests plus the cheapest high-volume fixes (built on `claude/ops-catalog`)**

- IT approval tier: `ActionTier.ItApproval`, approval store and REST, dashboard Approvals
  page (members approve or decline; admins choose which operations need approval), result
  delivered back into the user's Tray chat and the helpdesk timeline.
- Software: merge the install packages and skill from `claude/chrome-install-package`;
  `software.inventory`; Removable software list; `software.uninstall` (IT tier).
- Drivers: `drivers.inventory` (with hardware IDs), Devices list in the Environment page (models and
  hardware IDs, each naming its signed driver package), catalog match inside `drivers.inventory`,
  driver packages through `run_repair_package`.
- Internet/VPN: `network.adapters`, `network.adapter_enable`, `network.adapter_restart`,
  `dns.reset_to_dhcp`, `proxy.system_get`, `proxy.system_reset_to_standard`, VPN list,
  `vpn.status`, `vpn.client_restart`, `RasMan` restartable.
- Small wins: `audio.status`, `audio.set_default`, `device.enable`, `device.restart`,
  `printer.status`, `disk.usage`, `restart.pending`, `whoami.groups`.
- Skills: `software-install` (with removal), new `drivers-devices`, `vpn-support`, and
  updates to `network-support`, `printing-support`, `desktop-apps`, `slow-pc-health`.
- Signed EXE/BAT: already enforced; driver and uninstall packages are ordinary signed
  packages; a package whose problem starts with `uninstall-` (or with `requiresApproval: true`)
  waits for IT approval.

**Batch 2** — Wi-Fi, firewall and network category, Allowed sites and site-access
requests, org root CA install, intranet zones, Edge reset, Office add-ins and Quick Repair,
OST rebuild, Windows Update reset and scan,
SFC/DISM, Defender signatures and threats, BitLocker status, disk health, startup apps,
recycle bin, search index, battery report, power plan, display detect, region standard,
driver rollback, network stack reset (IT).

**Batch 3** — Offline Files, previous versions, IE mode status, default apps, Outlook new
profile, Office updates, Windows Hello reset, crash summary, RDP status, remote-support
launch, wake sources, fast startup, driver package removal, winget company source, and the
people-only approval flows (local admin, BitLocker key, trust repair, unlock).

## What the helpdesk team configures (no code)

| Page | They maintain |
| --- | --- |
| Environment | web apps, shares, printers, servers, proxy standard, time zone, root CAs, DNS suffixes (built) |
| Software catalog | install packages, licensed/needs-approval flag |
| Removable software | name/publisher patterns that may be removed, approval yes/no |
| Driver catalog | device models, hardware IDs, driver package per Windows version |
| VPN profiles | built-in VPN settings or client app + service name |
| Wi-Fi profiles | company SSIDs (batch 2) |
| Allowed sites | work sites by category (batch 2) |
| Capabilities | kill switch, hourly limit, approval requirement per operation |
| Approvals | approve or decline pending requests |
| Skills | the order of checks and fixes for each kind of ticket |

With these in place, a new kind of ticket is usually: write a skill that orders existing
operations, add a list entry (a printer, a driver model, an application), or sign a new
package. Only a genuinely new kind of step needs a developer.
