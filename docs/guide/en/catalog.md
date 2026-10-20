---
title: Common tickets and what handles them
summary: The everyday Windows helpdesk tickets, grouped by area, with the operation that handles each one, what is built, what is planned, and what the agent must never do alone.
order: 4
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
| Wi-Fi not connecting, firewall, network category | `wifi.status`, `firewall.status`, `network.category` | Planned |
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
| Wrong clock breaks HTTPS | `time.status`, `time.resync` | Built |
| Browser slow or old pages | `browser.inventory`, `browser.clear_cache`, the Chrome reset package | Built |
| "Is the site blocked on purpose?", opening a new site | Allowed-sites list and approval request | Planned |

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
| Add-in crashes, Office repair, OST rebuild | `office.addins_list`, `office.quick_repair`, `outlook.rebuild_ost` | Planned |

## Accounts and sign-in

| Ticket | What handles it | State |
| --- | --- | --- |
| Locked or expired account | `check_user_account`, then escalate: the helpdesk unlocks | Built |
| Trust relationship, policy not applied | `domain.health`, `gpo.refresh_computer`, `gpo.refresh_user` | Built |
| Keeps asking for a password | `credman.list`, `credman.remove`, `kerberos.purge` | Built |
| No access after a group change | `whoami.groups`, then sign out and in | Built |

## Performance, disk and updates

| Ticket | What handles it | State |
| --- | --- | --- |
| Slow or frozen PC | `perf.snapshot`, `system.recent_changes`, `events.summary` | Built |
| Disk full | `disk.usage`, `disk.cleanup_safe` | Built |
| Restart pending for days | `restart.pending`, `reboot.schedule` (Disrupt) | Built |
| Windows Update stuck, security worry | `windows_update.status`, `security.status`, `defender.quick_scan` | Built |
| BitLocker status, disk health, update reset, SFC and DISM | New Look and IT operations | Planned |

## Desktop, sound and laptops

| Ticket | What handles it | State |
| --- | --- | --- |
| No sound, muted, wrong output | `audio.status`, `audio.unmute`, `audio.set_default` (undoable) | Built |
| Camera or microphone blocked | `media.status`, `privacy.allow_camera_mic` | Built |
| Persian keyboard missing | `input.languages`, `input.add_layout` | Built |
| Taskbar or Start frozen | `explorer.status`, `explorer.restart` (Disrupt) | Built |
| Battery and power | `power.status` | Built |

## Things the system never does by itself

These always stay with a person, even when an operation exists nearby:

- Giving a **BitLocker recovery key**, resetting a password, unlocking an account, or changing a user's group. The agent only asks; a member decides.
- Running a command, script or file the AI wrote, or downloading anything from the internet.
- Turning off antivirus or the firewall, or changing company security policy.
- Removing software that is not on the **Removable software** list.
- Touching disks, display adapters, keyboards, mice and USB controllers.
- Repairing a PC when a known outage matches the complaint.

## Improving the map from your own tickets

Every site has its own top tickets. Keep a tally of what users really send, write a skill for the top ten, and send the ones the system cannot handle to **Feature requests** (users' requests are logged there by the agent). That list is what decides the next batch of operations.
