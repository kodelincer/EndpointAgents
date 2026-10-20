---
name: vpn-support
description: VPN does not connect, connects but internal sites or drives do not open, VPN keeps dropping, "the VPN client is not working", working from home cannot reach company systems. VPN وصل نمی‌شود، از خانه به سامانه‌ها دسترسی ندارم، VPN قطع می‌شود.
---

# VPN

Use with the endpoint-actions skill. Call `note_skill_used` with `vpn-support`.
Never ask for, show or accept a VPN password, one-time code or certificate.

## 1. Which VPN, and is it shared?

1. `list_environment_targets` with kind `vpn`. If there is more than one, pick the one the
   user names; otherwise ask one short question.
2. `check_known_incidents`. If an incident matches, say it is known and being fixed; stop.

## 2. Look

1. `network.status`: without a working internet connection the VPN cannot connect. If the
   PC has no internet, follow network-support first.
2. `vpn.status` with `{ "target": "<vpn id>" }`.

## 3. Decide

| Fact | Next step |
| --- | --- |
| `serverReachable` false | `probe_from_server` with the VPN id. Down from the server too: escalate once. Up from the server: the user's own network or provider blocks it; tell them and escalate. |
| Built-in VPN: `profilePresent` false | The VPN connection is not set up on this PC: escalate (the helpdesk deploys it). |
| Client VPN: service stopped or not answering | `vpn.client_restart` with the VPN id, then ask the user to connect again. |
| Built-in VPN: `rasManState` not running | `service.restart` with `RasMan`, then ask the user to connect again. |
| Connected, but internal names do not open | `dns.flush`, then `network.triage` with the internal app's id. Still failing: escalate with the VPN adapter facts. |
| `recentErrors` show a sign-in or certificate error | Escalate: the account or certificate must be fixed by the helpdesk. |
| Keeps dropping | `network.adapters` for Wi-Fi signal and driver age; escalate with the facts. |

## 4. Close

Ask the user to connect and say whether internal systems open now. If not and nothing
untried fits, escalate with the VPN label and what was checked.
