---
title: Common tickets and what handles them
summary: The everyday Windows helpdesk tickets, grouped by area, with the operation that handles each one, what is built, what is planned, and what the agent must never do alone.
order: 5
---

# Common tickets and what handles them

This is the map between what users complain about and the safe, fixed operations the system has. Use it when you write a skill: find the ticket, take the operation ids, order them into steps. The full list, with every operation id, kind and tier, is `docs/operation-catalog.md` in the repository.

Microsoft 365 services (Teams, OneDrive, Exchange Online, Entra sign-in) are **not** part of this system. Desktop Office and Outlook are.

## How to read it

| Word | Meaning |
| --- | --- |
| **Look** | Reads facts only. Runs freely. |
| **Fix** | A SafeFix: small, reversible, runs without asking and is checked before and after. |
| **Disrupt** | Interrupts the user (restart a device, the desktop or the PC). Runs only after the user says yes in the chat. |
| **IT** | Waits on the **Approvals** page until a helpdesk member approves it. |
| **Person** | Only a helpdesk member may do it. The agent sends a **Helpdesk task** to **Approvals** with `request_helpdesk_action`; nothing runs on the PC. |
| **Built / Planned** | Built means it exists in the code. Planned means it is on the list for a later batch; until then the agent escalates or logs a feature request. |

An admin can move any Fix or Disrupt operation to **IT** on **Capabilities → Actions** ("Needs IT approval").

## Network and internet

| Ticket | What handles it | State |
| --- | --- | --- |
| No internet or network | `network.status`, `network.triage`, then the fix the facts point to | Built |
| Cable out, adapter disabled | `network.adapters`, `network.adapter_enable` (Fix), `network.adapter_restart` (Disrupt) | Built |
| 169.254 address, no DHCP | `dhcp.renew` | Built |
| Stale DNS, wrong hand-typed DNS | `dns.flush`, `dns.reset_to_dhcp` (undoable) | Built |
| Wrong browser or system (WinHTTP) proxy | `proxy.get`, `proxy.reset_to_standard`, `proxy.system_get`, `proxy.system_reset_to_standard` (all undoable) | Built |
| Wi-Fi not connecting | `wifi.status`, `wifi.connect_company` (only a network on your **Company Wi-Fi** list) | Built |
| Shares and printers fail because the network shows as Public | `network.category`, `network.set_private` (Fix, undoable), `service.restart` with `NlaSvc` | Built |
| IPv4 address typed in by hand | `ip.reset_to_dhcp` (Fix, undoable, only when your standard says DHCP) | Built |
| Nothing loads although every check passes (after malware or a VPN uninstall) | `network.stack_reset` (IT, restart needed) | Built |
| Firewall profile wrong | `security.status` shows it; resetting it is planned | Planned |
| Outage affecting many people | `check_known_incidents`, `recent_reports_from_others`, `probe_from_server` | Built |

## VPN

| Ticket | What handles it | State |
| --- | --- | --- |
| VPN does not connect | `vpn.status` for a VPN from your **Company VPNs** list | Built |
| Client VPN stuck (FortiClient, Cisco, GlobalProtect, OpenVPN, WireGuard) | `vpn.client_restart` (only the service named in the list) | Built |
| Built-in VPN service stopped | `service.restart` with `RasMan` | Built |
| Connected but internal names fail | `vpn.status`, `dns.flush`, `network.triage` | Built |
| Missing built-in VPN profile, connect or disconnect | `vpn.ensure_profile`, `vpn.connect` | Planned |

## Websites and internal apps

| Ticket | What handles it | State |
| --- | --- | --- |
| Internal web app does not open | `network.triage`, `web.probe` | Built |
| Certificate warning | `cert.check_trust` | Built |
| Wrong clock breaks HTTPS | `time.status`, `time.resync`, `time.set_ntp` | Built |
| Browser slow or old pages | `browser.inventory`, `browser.clear_cache`, the Chrome reset package | Built |
| A work site on the internet (bank, tax, government) does not open | `site.check_access` for a site on your **Allowed work sites** list | Built |
| The site is blocked by the company filter | `request_helpdesk_action` kind `site-access` (Person) | Built |
| An internal app keeps asking for a password in the browser | `zones.status`, `zones.add_company_site` (Fix, undoable) | Built |

## Shared folders, printers and scanners

| Ticket | What handles it | State |
| --- | --- | --- |
| Drive missing or red X | `drives.list`, `share.test`, `drive.remap` | Built |
| Access denied on a share | `share.test`, `check_user_account`, `whoami.groups`, `credman.list`, `credman.remove` | Built |
| Cannot print, jobs stuck | `printer.list`, `printer.status`, `spooler.restart_and_clear` | Built |
| Printer missing or wrong default | `printer.add`, `printer.remove`, `printer.set_default` | Built |
| Printer driver missing for the model | `drivers.inventory` plus the driver package from your **Driver catalog** | Built |

## Devices and drivers

| Ticket | What handles it | State |
| --- | --- | --- |
| Device has a yellow mark or is not recognised | `drivers.inventory` (problem code, hardware IDs, current driver) | Built |
| Device turned off | `device.enable` | Built |
| Webcam, card reader, Bluetooth hung | `device.restart` (Disrupt; disks, display, keyboard, mouse and USB controllers are never touched) | Built |
| Install a driver | The signed driver package named in the Driver catalog, run by the agent | Built |
| New driver broke the device, remove a bad driver | `driver.rollback`, `driver.remove_package` | Planned |

## Software

| Ticket | What handles it | State |
| --- | --- | --- |
| Install an approved application | An install package (Chrome, Foxit, Adobe Connect, Photos, Persian fonts exist; add your own) | Built |
| Remove an application | `software.uninstall` (IT) for apps on **Removable software**, or an uninstall package | Built |
| What is installed | `software.inventory`, `apps.find` | Built |

## Office and Outlook

| Ticket | What handles it | State |
| --- | --- | --- |
| Office not activated | Activation tools and packages | Built |
| Outlook not connecting | `outlook.status` | Built |
| Word, Excel or Outlook crash at start or are slow | `office.addins_list`, `office.addin_disable` (Fix, undoable) | Built |
| Office itself is damaged | `office.quick_repair` (Disrupt: closes every Office app) | Built |
| Outlook mailbox file huge or damaged | `outlook.status`, `outlook.rebuild_ost` (Disrupt: Outlook closed, mail downloads again) | Built |

## Accounts and sign-in

| Ticket | What handles it | State |
| --- | --- | --- |
| Locked account or forgotten password | `check_user_account`, then `request_helpdesk_action` kind `unlock-account` or `password-reset` (Person) | Built |
| An approved install needs administrator rights | `request_helpdesk_action` kind `local-admin` (Person) | Built |
| Policy not applied | `domain.health`, `gpo.refresh_computer`, `gpo.refresh_user` | Built |
| "Trust relationship failed" | `domain.health`, then `request_helpdesk_action` kind `trust-repair` (Person) | Built |
| Keeps asking for a password | `credman.list`, `credman.remove`, `kerberos.purge` | Built |
| No access after a group change | `whoami.groups`, then sign out and in | Built |

## Performance, disk and updates

| Ticket | What handles it | State |
| --- | --- | --- |
| Slow or frozen PC | `perf.snapshot`, `system.recent_changes`, `events.summary` | Built |
| Disk full | `disk.usage`, `disk.cleanup_safe` | Built |
| Restart pending for days | `restart.pending`, `reboot.schedule` (Disrupt) | Built |
| Windows Update stuck, security worry | `windows_update.status`, `security.status`, `defender.quick_scan` | Built |
| Updates keep failing | `windows_update.reset_components`, `windows_update.scan_now` | Built |
| Antivirus out of date, a threat was found | `defender.update_signatures`, `defender.threats` (a threat that is not resolved is always escalated) | Built |
| Windows files damaged | `system.image_repair`, then `system.file_check` (both run in the background, about half an hour), `system.repair_status` for the result | Built |
| Disk warning | `disk.health` (back up, then escalate) | Built |
| BitLocker status, BitLocker recovery screen | `bitlocker.status` (never a key); the key itself: `request_helpdesk_action` kind `bitlocker-key` (Person) | Built |
| Slow start, too many startup apps | `startup.list`, `startup.disable` (Fix, undoable; only the user's own apps) | Built |
| Recycle bin full | `recyclebin.empty` (Disrupt: cannot be undone) | Built |

## Desktop, sound and laptops

| Ticket | What handles it | State |
| --- | --- | --- |
| No sound, muted, wrong output | `audio.status`, `audio.unmute`, `audio.set_default` (undoable) | Built |
| Camera or microphone blocked | `media.status`, `privacy.allow_camera_mic` | Built |
| Persian keyboard missing | `input.languages`, `input.add_layout` | Built |
| Taskbar or Start frozen | `explorer.status`, `explorer.restart` (Disrupt) | Built |
| Battery drains, PC sleeps too soon | `power.status`, `battery.report`, `power.set_plan_standard` (Fix, undoable) | Built |

## Things the system never does by itself

These always stay with a person, even when an operation exists nearby:

- Giving a **BitLocker recovery key**, resetting a password, unlocking an account, or changing a user's group. The agent only sends a Helpdesk task; a member decides and does it. The member never writes a key or password in the note, because the chat is read by the model.
- Running a command, script or file the AI wrote, or downloading anything from the internet.
- Turning off antivirus or the firewall, or changing company security policy.
- Removing software that is not on the **Removable software** list.
- Touching disks, display adapters, keyboards, mice and USB controllers.
- Repairing a PC when a known outage matches the complaint.

## Improving the map from your own tickets

Every site has its own top tickets. Keep a tally of what users really send, write a skill for the top ten, and send the ones the system cannot handle to **Feature requests** (users' requests are logged there by the agent). That list is what decides the next batch of operations.
