---
title: Writing skills: a practical guide
summary: How the helpdesk team teaches the agent a new problem without code: the shape of a skill, the tools and actions you can name, worked examples and the review steps.
order: 4
---

# Writing skills: a practical guide

The goal of this system is that, for most new problems, the helpdesk team only **writes or edits a skill**. The code underneath (actions, packages, lists) already covers the common Windows tickets; a skill tells the agent which of them to use, in which order, for which complaint.

A skill grants nothing. It cannot add a tool, an action or a permission, and it cannot make the agent run a command. If a skill names something that does not exist or that the agent may not use, the **Check** button refuses it.

## What you need to know first

| You want to know | Where to look |
| --- | --- |
| Every action the agent can run, its tier and its parameters | **Agent actions → Actions** |
| The tools the agent can call | **Agent actions → Agent tools** |
| Which common tickets already have an action | [Common tickets and what handles them](/docs/catalog/) |
| The lists the agent may pick from (servers, printers, apps to remove, device models, VPNs) | **Company resources**, see [Setting up your site](/docs/setup/) |
| How the agent behaves in general | [How the agent uses skills, tools and packages](/docs/agent/) |

## The shape of a skill

A skill has three parts you fill in on the **Agent skills** page:

1. **Name**: lowercase English letters, digits and dashes, for example `vpn-after-password-change`. It is published as `hd-<name>`.
2. **When to use** (one line, up to 400 characters): the problems it is for, written the way users write them, in Persian too. Hermes picks a skill by this line, so a vague line means the skill is never chosen.
3. **Steps**: the instructions. Do not write the `---` header; the dashboard adds it.

A good set of steps has four sections:

```text
# Printer shows an error

Use with the endpoint-actions skill. Call note_skill_used with this skill's name.

## 1. Is it shared?
Call check_known_incidents. If an outage matches, say it is known and stop.

## 2. Look
1. printer.list, then printer.status with the printer's name.
2. If the printer is a mapped one, probe_from_server with its target id.

## 3. Decide
| Fact | Next step |
| --- | --- |
| Paper, toner or door error | Tell the user what the printer reports. Nothing to repair on the PC. |
| Jobs stuck or spooler stopped | spooler.restart_and_clear |
| Printer down from the server too | Escalate once. Do not change the PC. |

## 4. Close
Ask whether printing works now. If not and nothing untried fits, escalate_to_helpdesk with the printer label and what was tried.
```

The reviewed skills in `deploy/hermes/skills` use the same shape; read `network-support`, `printing-support` and `drivers-devices` as models.

## Rules that make a skill work

- **Look before fixing.** Read facts with Look actions first. The result decides the fix, not a guess.
- **One fix per reply.** Run the one fix the facts point to, then ask whether it worked. Never repeat a fix that already ran in the conversation.
- **Name things by id.** Write actions by their exact id, like `dns.flush`, and servers, printers, shares and VPNs only by their environment-map id. Never write an IP address, a UNC path, a URL or a command.
- **Always give the way out.** After a fix, ask whether the problem is solved. Say when to `escalate_to_helpdesk` (nothing untried fits, a person must decide, the problem is on the server side).
- **Say what the user should do themselves** when the PC cannot fix it (check the cable, sign out and in, press Ctrl+Alt+Del).
- **Keep replies simple.** The agent answers in Persian, in at most three short sentences. Do not ask it to quote tool names, ids or file paths.
- **Do not collect secrets.** Never ask for a password, product key or token.

## How each tier behaves in a skill

| Tier | What to write | What happens |
| --- | --- | --- |
| **Look** | Name it freely. | Runs at once. |
| **SafeFix** | Name it as the fix for a specific fact. | Runs without asking, is checked before and after, and is limited per hour. |
| **Disruptive** | Tell the agent to offer "now or later" first. | Runs only after the user said yes in the chat. |
| **ItApproval** (and any action an admin marked "Needs IT approval") | Tell the agent to run it once and tell the user the helpdesk was asked. | The result is `AwaitingApproval`. A member approves it on **Approvals**; the user then sees the result in the chat. Do not tell the agent to wait or retry. |
| **OperatorOnly** | Never name it. | Undo steps for the helpdesk only; **Check** rejects them. |

## Worked examples

### Removing software (needs approval)

> When to use: "remove a program", "uninstall Foxit", "برنامه را پاک کنید"
>
> Steps: run `software.inventory` with a short part of the name; if the app has no `removable` value, say the helpdesk does not allow removing it and follow the feature-requests skill; otherwise run `software.uninstall` with the app's id and a short English reason, tell the user the helpdesk was asked, and stop.

### A device with a missing driver

> When to use: "scanner not recognised", "driver missing", "اسکنر را نمی‌شناسد"
>
> Steps: `drivers.inventory`; if the device has a `catalogMatch.packageId`, run that package with `run_repair_package`; if there is no match, say the model is not in the driver list and record a feature request so the helpdesk adds it.

### VPN connects but internal sites do not open

> Steps: `network.status`; `vpn.status` with the VPN id; if connected, `dns.flush`, then `network.triage` with the internal app's id; escalate with the VPN adapter facts if it still fails.

The skills `software-install`, `drivers-devices` and `vpn-support` in `deploy/hermes/skills` contain full versions of these.

## Check, review and publish

1. **Check** lints the draft. Errors block submission; warnings are advice.

   | Message | Meaning |
   | --- | --- |
   | Unknown tool or action | The name is wrong or does not exist. Copy the exact id from the Agent actions page. |
   | Helpdesk only | You named an OperatorOnly (undo) action. |
   | Looks like a command | The agent cannot run commands. Use actions instead. |
   | Reads like a permission | Skill text never grants permission. Reword it as an instruction. |
   | No step asks if it is solved / no escalation | Add both. |
   | IP address or UNC path | Use an environment-map id so the cloud model never sees internal addresses. |

2. **Submit** for review. Members draft; **reviewers** (a senior member or an admin) publish, send back, retire or roll back.
3. **Publish** writes the skill into the Hermes skills folder when `HelpdeskSkills:PublishPath` is set; otherwise download `SKILL.md` and copy it into the folder by hand, then restart the Hermes gateway.
4. **Watch the results.** The Agent skills page shows how often each skill was used. Read escalated conversations on **User chats**: they show which skill should be better or which action is missing.

## When the action you need does not exist

Do not work around it in the skill. Record it: the user-facing path is the **Feature requests** page (the agent logs requests users make) and the **Missing an action?** form on **Agent actions → Actions**. Developers add actions; you then update the skill to use them.

Common tickets that are planned but not built yet are listed in [Common tickets and what handles them](/docs/catalog/).

## Changing the reviewed skills in the repository

The skills under `deploy/hermes/skills` and `SOUL.md` belong to the project, not the dashboard. After editing them run `scripts\sync-hermes-profile.ps1` (`-Check` shows whether Hermes is current) and restart the gateway.
