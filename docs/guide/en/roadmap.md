---
title: How the system was born and where it is going
summary: The story so far, how a new kind of ticket turns into a skill, a list entry, a package or a new operation, and what is planned next.
order: 2
---

# How the system was born and where it is going

This page is the project's diary and plan in one place. Read it to understand why the system looks the way it does, how it grows without becoming dangerous, and what comes next.

## The idea

Users in the organisation describe a PC problem in Persian, in a small chat window next to the Windows clock. An AI agent works out what is wrong and fixes what it is allowed to fix. Everything it may do is a fixed, tested operation that the IT team switched on. What it may not do goes to a person, with the facts already collected.

Three promises shape every decision:

- **The AI never types commands.** It picks an operation id from a catalogue. The endpoint service runs only that code, with checked arguments. No PowerShell, cmd or AI-written script, ever.
- **The control plane is the boss, not the AI.** Identity, permissions, limits, approvals and the audit trail live in the control plane. The agent never learns which PC it is talking to.
- **The IT team grows the system without code.** New tickets mostly need a skill or a list entry, not a developer.

## The story so far

| When | What happened |
| --- | --- |
| July to September 2026 | The first version is written with Codex: the Windows service, a WinUI Tray, the control plane with SignalR and SQLite, Office and Windows activation repairs, IBSng dial-up checks. |
| 1 October | Decision: everything in .NET, except the AI agent, which is Hermes. |
| 2 to 3 October | The existing code is reviewed and becomes the reference. Work moves to Claude Code. |
| 4 to 5 October | Generic BAT packages with playbooks: the administrator writes the menu answers, the AI never chooses them. The Tray becomes a Telegram-style messenger named «دستیار پشتیبانی». First pull request. |
| 6 to 7 October | Two standing rules: every skill change is copied into Hermes, and the endpoint may only run catalogued operations. The escalation design and the system information tool. |
| 10 to 11 October | The package catalog with signing, the fallback loop (try the next package, ask, then escalate), the Helpdesk escalated cases and the Feature requests page. Catalogued operations run without asking the user. |
| 12 to 14 October | The Tray is rebuilt as a .NET window with a web interface (WebView2) and one secure pipe to the service. It becomes the main branch. |
| 17 October | Endpoint actions: about 65 small typed operations with tiers, limits, undo and an environment map. Dashboard sign-in with roles. This user manual. The first full test: a Persian chat through the Tray, Hermes, two checks and a reply. |
| 20 October, batch 1 | The operation catalog of common tickets. IT approvals, software removal from an admin list, the driver catalog, company VPNs, proxy and DNS resets, sound, devices, printer status. Microsoft 365 is left out on purpose. |
| 20 October, batch 2 | Wi-Fi, network type, work sites and intranet zones, Windows Update reset, Defender, BitLocker status, disk health, system file repair, battery and power plan, startup apps, Office add-ins and repair, Outlook mailbox rebuild. **Helpdesk tasks** for people-only work. The dashboard moves to a right-hand sidebar and the sign-in page gets its picture. |

## How the system grows

A new kind of ticket climbs this ladder only as far as it must. Most stop at the first or second step.

| Step | When it is enough | Who does it | Where |
| --- | --- | --- | --- |
| 1. Write or edit a skill | The operations exist; the agent needs the right order and the way out | Helpdesk member, a reviewer publishes | **Agent skills** ([Writing skills](/docs/skills/)) |
| 2. Add a list entry | The skill exists but needs your printer, share, app, device model, VPN, Wi-Fi or site | Admin | **Company resources** ([Setting up your site](/docs/setup/)) |
| 3. Add a signed package | The fix is a vendor installer, a driver or a BAT the organisation already trusts | Admin with the signing key | [Adding and signing packages](/docs/packages/) |
| 4. Ask for a new operation | Nothing above can do it safely | Anyone: the agent logs users' wishes, the team adds its own | **Feature requests**, **Missing an action?** on Capabilities |
| 5. A developer builds it | The request is common and can be made a small fixed step | Developer | Code, in the next batch |

Every new operation must arrive complete: the code on the PC with no shell, a catalogue entry with its tier and limits, an undo step if it changes something, tests, a skill that uses it, and a line in this manual. Anything that would hand out a secret or need judgement becomes a Helpdesk task instead of an operation.

## How a batch is chosen

The order comes from real tickets, not from what is easy to build:

1. **Feature requests** from users and the helpdesk, counted.
2. Escalated conversations on **User chats** that no operation could solve.
3. The common-ticket map ([Common tickets and what handles them](/docs/catalog/)), which marks each ticket Built or Planned.

A batch is a group of operations that close the most of those tickets with the least risk. Each batch ends with tests on a real PC before it is used.

## What is planned next

**Batch 3** (from the catalog, not yet built): firewall profile reset, forgetting a Wi-Fi profile, built-in VPN profiles and connect, installing organisation root certificates, Edge reset, hosts file reset, USB, Bluetooth and smart card status, display detect, search index rebuild, region standard, driver rollback and removal, MSI repair, re-registering built-in Store apps, a group policy summary, Defender full scan, scheduled disk check, Offline Files, previous versions of a file, default apps, a new Outlook profile, Office updates, Windows Hello reset, crash summary, Remote Desktop status, starting the company remote-support tool, wake sources and fast startup.

Longer-term ideas, kept so none are forgotten, are on [Ideas and future features](/docs/ideas/).

**Before a pilot:**

- Merge the open branches into one tested version and install it on 5 to 10 PCs.
- Keep agent replies short and simple, as the rules ask; the first test showed they were too long.
- Re-enroll a PC automatically when its saved credential is rejected.
- Decide what may leave the network: user text goes to a cloud AI model, so internal names and addresses are masked, and IP addresses in system information are still an open decision.
- Clean the development database and test credential out of the repository before sharing it.

## What will not change

- Microsoft 365 services (Teams, OneDrive, Exchange Online, Entra) are out of scope. Desktop Office and Outlook stay.
- The system installs and runs on both Windows 10 and Windows 11.
- No ticket system or deployment tool is required: only the control plane server, which also serves the packages.
- Passwords, recovery keys and group membership stay with people.
