---
title: How the system fits together
summary: The Tray, the endpoint service, the control plane, Hermes and the dashboard, and who is allowed to decide what.
order: 1
---

# How the system fits together

EndpointAgents lets a Windows user describe a problem in the Tray app, in Persian, instead of calling the helpdesk. An AI agent diagnoses the PC and, inside strict allow-lists, repairs it. When it cannot, a person takes over from this dashboard.

## The five pieces

| Piece | Where it runs | What it does |
| --- | --- | --- |
| **Tray** | The user's PC, in their Windows session | Chat window, Status and Control, Reports, and the consent card. It reaches the PC only through a fixed list of catalogued operations. |
| **Endpoint service** | The user's PC, as LocalSystem | Keeps one authenticated connection to the control plane, receives allow-listed commands and runs the matching handler. It owns the only named pipe the Tray talks to. |
| **Control plane (API)** | The server, port 5052 | Owns identity, conversations, consent, the command allow-list and the audit trail. Stores everything in SQLite. |
| **Hermes** | The server, loopback only (127.0.0.1:8642) | The AI agent. It reads the conversation, picks skills and calls control-plane tools. It has no direct line to any PC. |
| **Dashboard** | The server, loopback only (127.0.0.1:3000) | The helpdesk's console: conversations, packages, skills, capabilities, environment and operators. |

## A conversation, step by step

```text
User types in the Tray
  -> endpoint service (named pipe)
  -> control plane (authenticated SignalR, stored in SQLite)
  -> Hermes run  (masked prompt goes to the cloud LLM)
  -> Hermes calls a control-plane tool (MCP, loopback, run-scoped key)
  -> control plane checks the rules, then sends one allow-listed command
  -> endpoint service runs the fixed handler
  -> sanitised result goes back to Hermes, then a short Persian answer to the Tray
```

Three rules hold at every step:

1. **The control plane decides, not the AI.** Hermes never learns a device ID. Each run gets a random capability that the server resolves to one PC and one Windows user. The AI cannot address another PC.
2. **Only catalogued operations exist.** There is no tool that runs a command, script or file the AI wrote. The agent picks an action or a package by its ID; the code or file behind it was reviewed by a person.
3. **Everything is recorded.** Conversations, tool calls, action runs and consent decisions are stored and shown on the dashboard.

## What the agent may do on its own

| Kind of work | Needs the user's approval? |
| --- | --- |
| Read-only diagnostics (Look actions, system information) | No |
| Small reversible fixes (SafeFix actions) | No, but limited per hour and verified before and after |
| Activation repairs for Windows and Office | No, through their dedicated tools, after safety checks |
| Reviewed repair packages (BAT or EXE) whose symptoms match | No, because an administrator reviewed and signed them |
| Disruptive actions (restart the desktop or PC, close an app) | Yes, the user must say yes in the chat |
| Anything else | The agent proposes it and the user approves a card in the Tray |

## The dashboard at a glance

| Page | Use it to |
| --- | --- |
| **Overview** and **Agents** | See which PCs are online. Admins can send catalogued commands. |
| **Packages** | See repair packages, their status and exactly what to fix. |
| **Helpdesk** | Read live conversations, take over, see escalated cases. |
| **Approvals** | Approve or decline the requests that wait for a helpdesk member, such as removing software. |
| **Feature requests** | Triage things users asked for that the system cannot do yet. |
| **Capabilities** | The live list of everything the agent can do, with switches and limits. |
| **Skills** | Write, check, submit and publish the instructions the agent follows. |
| **Environment** | Describe your servers, printers, shares and standards, and declare outages. |
| **Operators** | Admins manage accounts and roles. |
| **Documentation** | This guide. |

## Where to start as a helpdesk member

1. [Setting up your site](/docs/setup/): fill in the lists the agent works from.
2. [Common tickets and what handles them](/docs/catalog/): see what the system already does.
3. [Writing skills](/docs/skills/): teach the agent new problems without code.

## Where the details live

The Markdown files behind this guide are in `docs/guide` in the repository. The reference documents next to them (`docs/control-plane-operations.md`, `docs/hermes-support-agent.md`, `docs/endpoint-actions.md`, `docs/bat-packages.md`, `docs/tray.md`) cover installation and each feature in more depth.
