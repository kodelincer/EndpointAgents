---
title: Setting up your site
summary: What the helpdesk fills in so the agent can work: the Environment lists, approvals, install and driver packages, and a first-week checklist.
order: 6
---

# Setting up your site

The agent only picks from lists you keep. A skill says "use the printer", and the **Environment** page says which printers exist. This page lists what to fill in, in a sensible order.

## The Environment page

| List | What you enter | What the agent can then do |
| --- | --- | --- |
| **Servers and web apps** | Id, label, address, optional ports | Test an internal app, spot a wrong DNS answer, compare the PC with the server's view |
| **Shared folders** | Id, label, `\\server\share`, standard drive letter | Re-map a missing drive, test access |
| **Printers** | Id, label, address | Add the office printer for a user, tell an offline printer from a PC fault |
| **Removable software** | An Apps & features name, with `*` as a wildcard (for example `Foxit PDF Reader*`), an optional publisher, and an optional uninstall package | Software the user may ask to remove. Anything not on the list is refused |
| **Driver catalog** | A model label, its hardware IDs separated by `;`, and the id of its signed driver package | Match a device on a user's PC to the right driver package |
| **Company VPNs** | Host name of the VPN server; whether it is a Windows VPN or a client; the connection name or the client's Windows service | `vpn.status` and `vpn.client_restart` for that VPN |
| **Company Wi-Fi** | The network name (SSID); the Windows profile name only when your policy names it differently | See which company networks a laptop has, connect to one |
| **Allowed work sites** | Internet sites people need for work, as a host name or `*.domain`, with an optional category (banking, government) | Check whether a work site answers or the company filter blocks it |
| **Standards** | DNS suffixes, proxy standard, time zone, root CA thumbprints, whether DHCP hands out the right DNS, whether PCs get their IP address from DHCP, the standard power plan | Reset a wrong proxy, DNS, hand-typed IP address or power plan, check certificates |
| **Incidents** | Declare an outage | The agent tells users it is known instead of repairing PCs |

Only admins edit the lists. The agent sees labels and ids, never addresses.

Where to find a hardware ID: on a PC with the device attached, Device Manager → the device → Properties → Details → **Hardware Ids**. Use the most specific line, for example `USB\VID_04F9&PID_0321`.

## Approvals

Some requests should never run without a person. They wait on the **Approvals** page:

- Software removal (`software.uninstall`) and any package whose `problem` starts with `uninstall-` or whose manifest has `requiresApproval: true`.
- Any other fix an admin switched to **Needs IT approval** on **Capabilities → Actions**.

Each request shows the user, the PC, the item and the agent's reason. **Approve and run** runs it at once through the same checks as any other action (kill switch, hourly limit, before and after). **Decline** takes a note that the user sees. Either way the user gets a short message in their chat, and the decision is recorded with your name.

Plan who watches the page. A request does not expire by itself, and the user is told only that the helpdesk was asked.

### Helpdesk tasks

Some work only a person may do. The agent sends it as a **Helpdesk task**: unlock an account, reset a password, temporary administrator rights, a BitLocker recovery key, open a blocked work site, or rejoin a PC to the domain. Nothing runs on the PC. Do the work yourself (in Active Directory, the network filter, or by phone for a key), then press **Mark done** with an optional note the user sees.

Never put a password or a recovery key in the note. The chat is also read by the AI model, so the dashboard refuses a note that looks like one. Give those to the user in person or by phone.

## Packages: install, uninstall and drivers

Packages are folders with a BAT or EXE and the files it needs. They are hash-pinned and signed by an administrator; the agent only picks one by its id. You create them with the scripts in the repository, as described in [Adding and signing packages](/docs/packages/).

| Kind | How to make it | How the agent finds it |
| --- | --- | --- |
| **Install an application** | `scripts\new-install-package.ps1` with the vendor's MSI or EXE. The BAT checks whether the app is installed first. | `problem` starts with `install-`; the `software-install` skill has a table of applications and try order |
| **Uninstall an application** | A no-input package that runs the vendor's silent uninstaller, with `problem` starting `uninstall-`; put its id on the **Removable software** entry | The agent runs it through the same approval |
| **Install a driver** | Copy `deploy\package-templates\install-driver.bat`, fill in the model and the `.inf` name, then `new-package.ps1 -NoInput` and `set-package-files.ps1` with the driver's files | The id on the **Driver catalog** entry; the `drivers-devices` skill runs it |

Windows still refuses a driver whose own signature is invalid, on top of the package hash and your signature. Windows 10 and 11 are both supported; test a package on your own PC before you publish it.

## First-week checklist

1. Sign in as `admin`, set a new password, delete `initial-admin-password.txt`, and create personal accounts on **Operators**.
2. Fill in **Environment**: your top servers and web apps, printers and shares, then the standards.
3. Add your VPN or VPNs, your company Wi-Fi networks, and the internet sites people need for work.
4. Add the software people may ask to remove (start with two or three) and the device models you own (printers, scanners, card readers).
5. Create install packages for the five applications users ask for most, and a driver package for your most common printer.
6. Run `scripts\sync-hermes-profile.ps1`, restart Hermes, and open a chat from the Tray on your own PC.
7. Try the three scenarios end to end: an internal site that does not open, a printer problem, a software removal request (watch it arrive on **Approvals**).
8. Read the first escalated conversations on **Helpdesk** and write or edit skills for what the agent missed, using [Writing skills](/docs/skills/).

## Keeping it healthy

- Review **Feature requests** weekly. They show which operations to ask for next.
- Look at **Capabilities → Recent runs** after changes; reviewers can undo a fix from there.
- Declare an **Incident** as soon as a server or the link is down, so users are told it is known.
- After any change to a reviewed skill or `SOUL.md` in the repository, run `scripts\sync-hermes-profile.ps1` again.
- Read [Security and hardening](/docs/security/) before you move from testing to production.
