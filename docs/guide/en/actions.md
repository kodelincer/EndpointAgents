---
title: Endpoint actions and tiers
summary: The catalogue of small typed fixes, the four tiers, typed parameters, proof and undo, switches and limits.
order: 7
---

# Endpoint actions and tiers

The agent fixes everyday problems (network, internal web apps, printers, shared drives, sign-in, slow PCs, desktop and apps) by combining small **actions**. An action is fixed code that ships with the product. The agent only picks an action ID and values from lists; it never writes a command.

The live list, with every switch and limit, is the **Capabilities** page. This topic explains the rules behind it.

## One catalogue

`EndpointActionCatalog` (in `src/EndpointAgents.Shared/Actions`) defines every action: ID, area, tier, where it runs, parameters, limit, verification and undo. The agent's tools, the Capabilities page, the kill switches and the endpoint's own validation all read the same catalogue, so they cannot disagree.

## Tiers

| Tier | Meaning | Rules |
| --- | --- | --- |
| **Look** | Reads facts only | Runs freely. Default limit 30 per hour per PC. |
| **SafeFix** | Small, reversible fix | Runs without asking, with no Tray card. Default 3 per hour. Checked before and after. |
| **Disruptive** | Interrupts the user (restart the desktop or PC, close an app) | Needs `userConfirmed=true`, which the agent sets only after the user said yes in the chat ("now" or "later"). Default 2 per hour. |
| **ItApproval** | Needs a helpdesk member's yes (removing software) | The request waits on the **Approvals** page; it runs as soon as a member approves it, and the user sees the result in the chat. Admins can also mark any SafeFix or Disruptive action "Needs IT approval" on the Capabilities page. Default 3 per hour. |
| **OperatorOnly** | Undo steps | Never offered to the agent. The helpdesk runs them from Recent runs. |

Pick the lowest tier that fits when you add an action. A fix that changes a user setting should say how to undo itself.

## Typed parameters only

Nothing the agent supplies is ever a path, URL, command or address. Parameters are one of:

| Type | Example |
| --- | --- |
| Choice | A value from a fixed list |
| Target | An environment-map ID that the server resolves to a real address |
| LocalItem | A name the PC itself reported, such as a printer |
| Integer | A number inside a range |
| SearchText | Letters, digits and spaces only |

## No shell

Actions use Windows APIs, WMI, the service controller and event logs. The few external programs are a fixed allow-list of System32 tools (`ipconfig`, `w32tm`, `gpupdate`, `klist`, `netsh`, `tzutil`, `shutdown`, `powercfg`) with fixed arguments.

## Who runs it

- **System actions** run in the endpoint service as LocalSystem.
- **User actions** (proxy, printers, mapped drives, browser caches, keyboard layouts, saved passwords) run in the signed-in user's Tray, through the one service pipe, so they act on the right profile.

## Proof and undo

- A fix with a verification check runs its Look action before and after, and the result shows both. The agent then asks the user whether the problem is solved.
- Fixes that change a user setting store the previous values. A reviewer can undo them from **Capabilities, Recent runs**.
- Every run is stored, appears on the conversation timeline and in the tool audit. Fixes are also recorded as repair attempts, so the agent never repeats one in the same conversation.

## Switches and limits

Admins can switch any action off, or set another hourly limit, on the Capabilities page. It takes effect at once, with no release. Switched-off actions disappear from the list the agent sees. The control plane re-checks every call: tier, switch, hourly limit, user confirmation, parameters and targets.

## Environment map

The **Environment** page tells the agent about your site by ID and label: internal web apps, shared folders (with their standard drive letter), office printers and key servers. Optional ports and expected IP addresses let it test the right port and spot wrong DNS answers. It also holds the standards (internal DNS suffixes, proxy, time zone, root CA thumbprints) and the incidents.

Three more lists drive helpdesk requests: **Removable software** (Apps & features names, with `*`, that users may ask to remove, optionally with an uninstall package), the **Driver catalog** (device models by hardware ID, each with its signed driver package) and **Company VPNs** (server, built-in or client, connection or service name). Software users may install is the set of install packages.

Only admins edit it. The model sees labels, never addresses.

## Adding a new action (development)

1. Add the definition to `EndpointActionCatalog`.
2. Implement `IEndpointAction` in `Platform.Windows/Actions/SystemSide` or `UserSide` and register it in `EndpointActionSet`.
3. Mention it in the matching symptom skill, run the tests (catalogue consistency, skill lint, Hermes allow-list), then run `scripts\sync-hermes-profile.ps1`.

No new MCP tool or hash-pinned script is needed. `run_endpoint_action` is the one tool and the action is compiled code. If the fix is an existing vendor file instead, build a [package](/docs/packages/).
