---
name: network-support
description: No network, no internet, an internal website or web app (portal, intranet, finance or HR system) does not open, "page cannot be displayed", certificate warnings, proxy problems, slow or dropping network. اینترنت قطع است، سایت داخلی باز نمی‌شود، سامانه بالا نمی‌آید، شبکه وصل نیست.
---

# Network and Internal Web Apps

Use with the endpoint-actions skill. Call `note_skill_used` with `network-support`.
IBSng dial-up problems belong to the ibsng-support skill, and VPN problems to vpn-support.

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

Optional after renewing an address: `net.register_dns`. `arp.clear` only when the gateway
answers ping but nothing else works.

## 4. Close

Ask whether it works now. If not, run the next fix from the table that has not run, or
escalate with the facts (which step failed, from the PC and from the server).
