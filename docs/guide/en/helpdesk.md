---
title: Daily helpdesk work
summary: Reading conversations, escalated cases, taking over from the agent, feature requests, outages and skills.
order: 3
---

# Daily helpdesk work

Most conversations are solved by the agent without you. Your job is the ones it hands over, the ones that look wrong, and improving what it knows.

## Reading conversations

Open **Helpdesk**. Each row is one conversation between one Windows user on one PC and the agent. Statuses:

| Status | Meaning |
| --- | --- |
| New / Open | The agent is working or waiting for the user. |
| Escalated | The agent could not fix it, or the user asked for a person. These sort first and are never deleted until someone resolves them. |
| Resolved | The user confirmed that the problem is solved. A new message from the user reopens it. |

A conversation shows the messages, every tool call and repair attempt in a timeline, and the PC's connection state. Completed conversations are deleted after 30 days by default (`Chat:RetentionDays`).

## Escalated cases

When the agent hands over, the row shows a one-line **reason**, a short **summary** (what was checked and tried, what the user said) and every attempt. Work like this:

1. Read the summary and the attempts so you do not repeat a repair that already failed.
2. Check **Recent runs** on the Capabilities page if a fix changed a user setting; reviewers can undo some of them.
3. Take over (below) and talk to the user, or run a read-only diagnostic from the conversation.
4. Set the status to **Resolved** when the problem is gone.

Escalation changes nothing on the PC. It only moves the conversation to you.

## Taking over

Press **Take over** on a conversation. From that moment:

- The running agent turn is cancelled and the agent stops answering; the composer opens for you.
- Your message goes to the user's Tray like a normal reply.
- **Return to Hermes** gives the conversation back to the agent.

Take over only when you intend to answer; while it is on, the user waits for you.

You can also run **read-only diagnostics** from a conversation: PC information, system information, Windows and Office activation and Office inventory, and the IBSng connection check. Nothing else runs from here, and the parameters cannot contain credentials. Diagnostics pause during maintenance mode.

## Feature requests

When a user asks for something the system does not support at all, the agent says so and offers to save a request. It saves only after the user says yes. Reviewers and admins see them on **Feature requests** and move each one through **New, Planned, Done or Declined**. That is how the team decides what to build next.

Do not confuse the two hand-overs:

- **Escalation**: the system supports this, but the agent could not fix it. A person helps the user now.
- **Feature request**: the system cannot do this yet. It is saved for the development team.

If you see a repeated need while working, use **Missing an action?** on the Capabilities page. It records the gap for the development team.

## Outages (incidents)

When something is down for everyone, declare an **incident** on the Environment page. The agent checks incidents before repairing anything that could affect more than one PC (network, server, internal web app, printer, shared folder). If one matches, it tells the user the outage is known and does not touch the PC. Close the incident when it is over.

## Skills

The **Skills** page is where helpdesk knowledge becomes agent behaviour.

1. A member writes a skill: a name, a one-line description with the phrases users write, and the steps. **From conversation** starts a draft from a solved or escalated chat.
2. **Check** lints the draft: unknown tools or actions, helpdesk-only actions, command-like text, wording that reads like a permission, missing "ask the user" or escalation steps, IP addresses and UNC paths.
3. The member submits it. A reviewer publishes it, sends it back, retires it or rolls back to an older version.
4. Usage statistics show which skills guide conversations.

A skill never grants a power. If it needs one the agent does not have, ask for it through **Missing an action?**.

## Who can do what

| Role | Persian name | Can |
| --- | --- | --- |
| Member | کارشناس پشتیبانی | Answer and take over conversations, run read-only diagnostics, declare incidents, draft and submit skills, read the Capabilities page |
| Reviewer | کارشناس ارشد | Everything above, plus triage feature requests, publish, send back, retire and roll back skills, and undo fixes from Recent runs |
| Admin | مدیر سیستم | Everything, plus the environment map, action switches and limits, endpoint commands, maintenance mode, device revoke, enrollment codes and operator accounts |

Use the lowest role that fits the person's work. Roles are explained further in [Security and hardening](/docs/security/).
