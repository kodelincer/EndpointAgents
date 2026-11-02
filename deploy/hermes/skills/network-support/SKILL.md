---
name: network-support
description: No network, no internet, an internal website or web app (portal, intranet, finance or HR system) does not open, "page cannot be displayed", certificate warnings, proxy problems, slow or dropping network, Wi-Fi not connecting, network shown as Public, an internet site for work blocked. وای‌فای وصل نمی‌شود، سایت بانک یا سامانهٔ دولتی باز نمی‌شود، اینترنت قطع است، سایت داخلی باز نمی‌شود، سامانه بالا نمی‌آید، شبکه وصل نیست.
---

# Network and Internal Web Apps

Use with the endpoint-actions skill. Call `note_skill_used` with `network-support`.
IBSng dial-up problems belong to the ibsng-support skill, and VPN problems to vpn-support.
The checks below speak in gateway, DNS, port and proxy terms; those words stay here. Tell the
user only what it means, in everyday Persian: «اینترنت از برنامه‌ای مثل فیلترشکن رد می‌شود»,
not the proxy address or a program's internal name.

## 1. Is it shared?

Call `check_known_incidents`. If the user names an internal app, find its id with
`list_environment_targets` (kind `webapp`).

## 2. Look

- One app does not open: `network.triage` with `{ "target": "<id>" }`. It reports link,
  address, gateway, DNS answer from each DNS server, port and web response in one call.
- Nothing works: `network.status` (no target needed), then `network.adapters` when no
  adapter is connected.
- Certificate or "not secure" warning: `cert.check_trust`, then `web.probe` on the app.
- Only in one browser or after a proxy change: `proxy.get`.
- Browsers work but Windows Update or Office updates do not: `proxy.system_get`.
- Wi-Fi: `wifi.status` (radio, current network, signal, which company networks are saved).
- Shares or printers fail on a working network, or the firewall acts like a public place:
  `network.category`.
- A work site on the internet (bank, tax, government portal): find its id with
  `list_environment_targets` (kind `site`), then `site.check_access`.
- An internal app asks for a password again and again in the browser: `zones.status`.

## 3. Decide

| Fact | Next step |
| --- | --- |
| An adapter is disabled (`network.adapters`) | `network.adapter_enable` with its name. |
| No connected adapter, all enabled | Ask the user to check the cable or Wi-Fi; if still down, offer `network.adapter_restart` (Disruptive: ask now or later). |
| APIPA address (169.254) or no gateway answer | `dhcp.renew` (SafeFix). |
| Name does not resolve, other names do | `dns.flush`, then `network.triage` again. |
| DNS servers typed in by hand on a DHCP adapter | `dns.reset_to_dhcp` (SafeFix, undoable by the helpdesk; Blocked when the standard is not set). |
| DNS servers disagree, or wrong address for the target | `probe_from_server`; if the server sees the right address, `dns.flush`; else escalate (DNS record). |
| Port closed from the PC but open from the server | Escalate (firewall or network path), say which app. |
| Down from the server too | Escalate once; do not repair the PC. |
| Proxy differs from the standard | `proxy.reset_to_standard` (SafeFix, undoable by the helpdesk). |
| System proxy differs from the standard | `proxy.system_reset_to_standard` (SafeFix, undoable by the helpdesk). |
| `hosts.check` shows an entry for an internal name | Escalate; never edit it. |
| Certificate root missing | Escalate; certificates are pushed by policy, try `gpo.refresh_computer` once first. |
| Web app answers 5xx | Escalate: the app itself is failing. |
| Only one browser fails | `browser.clear_cache` for that browser. |
| Wi-Fi radio off | Ask the user to turn Wi-Fi on (key or airplane mode); nothing to run. |
| Not on a company Wi-Fi, a company profile is saved | `wifi.connect_company` with its target id. |
| Company Wi-Fi profile not saved | `gpo.refresh_computer` once (the profile comes by policy); if still missing, escalate. |
| Weak signal (under 30%) | Tell the user to move closer to the access point; escalate if the whole area is weak. |
| A network shows as Public and the PC is on the company network | `network.set_private` (SafeFix, undoable by the helpdesk). If it comes back Public, `service.restart` with `NlaSvc`. |
| IPv4 address typed by hand and the standard says DHCP | `ip.reset_to_dhcp` (SafeFix, undoable by the helpdesk; Blocked when the standard is not set). |
| Every check passes but nothing loads, after malware cleanup or a VPN uninstall | `network.stack_reset` (it waits for helpdesk approval and needs a restart afterwards). |
| `site.check_access` says LooksBlocked | The company filter blocks it on purpose: `request_helpdesk_action` with kind `site-access` and the site's name. |
| `site.check_access` cannot reach the site, other sites work | `probe_from_server`; if it is down from the server too, say the site itself is down. |
| `zones.status` shows the internal app in the Internet zone, not set by policy | `zones.add_company_site` with its target id (SafeFix, undoable by the helpdesk). If the zone list is set by policy, escalate. |

Optional after renewing an address: `net.register_dns`. `arp.clear` only when the gateway
answers ping but nothing else works.

## 4. Close

Ask whether it works now. If not, run the next fix from the table that has not run, or
escalate with the facts (which step failed, from the PC and from the server).
