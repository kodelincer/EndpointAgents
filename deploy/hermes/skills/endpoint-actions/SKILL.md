---
name: endpoint-actions
description: How to use the general action catalogue on the user's PC (run_endpoint_action), the environment map, known incidents and fleet checks. Read it together with any symptom skill (network, printing, shared drives, sign-in, slow PC, desktop and apps).
---

# General Endpoint Actions

The PC runs only catalogued actions. You choose which action to run and with which
listed values; you never write commands, paths or addresses.

## Tools

- `mcp_controlplane_list_endpoint_actions` `{ runCapability, area? }`: the catalogue
  (id, tier, parameters, what it returns). Call it once per conversation with the area
  you need: network, domain, printers, drives, browsers, office, health, desktop.
- `mcp_controlplane_list_environment_targets` `{ runCapability, kind? }`: the
  organisation's servers, web apps, printers and shares, by id and label.
- `mcp_controlplane_run_endpoint_action`
  `{ runCapability, actionId, arguments?, userConfirmed? }`: runs one action.
  `arguments` is an object of strings, e.g. `{ "target": "hr-portal" }`.
- `mcp_controlplane_check_known_incidents` `{ runCapability }`: outages the helpdesk declared.
- `mcp_controlplane_recent_reports_from_others` `{ runCapability, hours? }`: what other users
  reported recently.
- `mcp_controlplane_probe_from_server` `{ runCapability, targetId }`: is the target up when
  checked from the server.
- `mcp_controlplane_check_user_account` `{ runCapability }`: read-only domain account state.
- `mcp_controlplane_note_skill_used` `{ runCapability, skillName }`: call once with the
  symptom skill you follow.

## Tiers

- **Look**: only reads. Run freely.
- **SafeFix**: a small fix (clear a cache, renew an address, re-map a drive). Runs without
  asking. The server checks before and after; read `verification.after` to see if it worked.
- **Disruptive**: interrupts the user (restart the desktop, close an app, restart the PC).
  First ask one short question offering "now" or "later". Run it with
  `userConfirmed: true` only after the user said yes in this chat. A PC restart is
  `reboot.schedule` with `minutes` 5 for "now" (Windows warns them to save) or 15 or 60
  for "later"; for other actions "later" means waiting until the user says they are ready.

## Order of work

1. Shared problem? (a server, web app, printer, share, the network) Call
   `check_known_incidents`. If an incident matches, say it is known and being fixed; stop.
2. Look before fixing. Use the symptom skill's Look actions and read the facts.
3. If a target fails from the PC, call `probe_from_server` for it. If it is down from the
   server too (`verdict` not `ReachableFromServer`), the PC is fine: do not repair it;
   check `recent_reports_from_others`, then escalate once.
4. Run at most one SafeFix per reply, the one the facts point to, in the order the
   symptom skill gives. Never repeat the same fix in a conversation (see
   `list_repair_attempts`).
5. After a fix, tell the user in one short sentence what was done and ask whether the
   problem is solved.
6. If not solved and no untried fix fits, `escalate_to_helpdesk` with what was checked.

## Reading results

- `status`: `Ok` (facts read), `Done` (fixed), `NotNeeded` (nothing to change),
  `Blocked` (something prevents it now, read `summary`), `Failed`.
- An error saying the action is switched off, over its hourly limit, or needs
  `userConfirmed` is a rule, not a fault: pick another step or escalate.
- "older endpoint version": this PC cannot run general actions yet; use the other skills
  or escalate.
- Names in square brackets like `[HR portal]` or `[internal-host]` are masked on purpose.
  Never ask the user for addresses or server names.

## Missing abilities

If the needed action is not in the catalogue, do not improvise. Follow the
feature-requests skill, or escalate when it is a fault the helpdesk must fix.
