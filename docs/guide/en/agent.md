---
title: How the agent uses skills, tools and packages
summary: What a skill is, which tools Hermes can call, how packages are chosen, and what the agent can never do.
order: 2
---

# How the agent uses skills, tools and packages

The agent has three kinds of building blocks. They have different jobs, and none of them can grant itself more power.

## 1. Skills: what to do and in which order

A **skill** is a short text file (`SKILL.md`) that tells the agent how to handle one kind of problem: what to check, what to try first, when to ask the user, when to escalate. Hermes picks a skill by its one-line description, so descriptions should contain the phrases users really write.

- Reviewed skills for Office, Windows, network, printing, shared drives, sign-in, slow PCs, desktop apps and repair packages are in `deploy/hermes/skills`.
- Helpdesk-written skills are created on the **Skills** page and published as `hd-<name>` skills.
- A skill only **orders** existing tools. It cannot add a tool, an action or a permission. The control plane and the Hermes allow-list decide what is possible; if a skill says otherwise, the call is refused.

## 2. Tools: what the agent can call

Tools are served by the control plane over a loopback-only MCP endpoint and are the agent's only connection to the outside. The allow-list lives in two places that must agree: the control plane and the Hermes profile (`tools.include` in `deploy/hermes/config.yaml.example`).

| Group | Tools | Notes |
| --- | --- | --- |
| Look at the PC | `endpoint_get_info`, `endpoint_system_info`, `office_inventory`, `office_activation_status`, `office_runtime_status`, `windows_activation_status` | Read-only. Results are masked before the cloud model sees them. |
| Fix with actions | `list_endpoint_actions`, `run_endpoint_action`, `list_environment_targets` | The catalogue of small, typed actions. See [Endpoint actions and tiers](/docs/actions/). |
| Fix with packages | `find_repair_packages`, `run_repair_package`, `list_repair_attempts` | Administrator-signed BAT or EXE packages. |
| Dedicated repairs | `run_windows_activation_repair`, `run_office_activation_repair` | Pinned repairs for activation problems. |
| Propose a change | `propose_endpoint_action` | Creates a card the user must approve in the Tray. |
| Know the situation | `check_known_incidents`, `recent_reports_from_others`, `probe_from_server`, `check_user_account` | Outages, other reports, a server-side check, read-only domain account state. |
| Finish | `mark_problem_resolved`, `escalate_to_helpdesk`, `record_feature_request`, `note_skill_used` | Closing, handing over, saving requests and statistics. |

Hermes' own built-in toolsets (terminal, files, browser, web, memory, code execution and others) are switched off in the profile.

## 3. Packages: reviewed files that do a repair

A **package** is a folder with a BAT or EXE, its fixed answers and arguments (the *playbook*), its SHA-256 hashes, the problem it solves and the symptoms that match it. An administrator reviews it and signs it. The agent never writes or edits one.

How the agent uses them:

1. `find_repair_packages` returns only packages that are compatible with this PC (Windows or Office version). The agent compares the user's words with each package's `symptoms`.
2. `run_repair_package` pins the file, the playbook and the supporting files by hash and sends one command to this user's PC. No approval card is shown, because a person already reviewed the package.
3. Packages that share a `problem` are alternatives, tried in `order`. After each one the agent asks whether the problem is solved. On "no" it runs the next one it has not tried yet; when none is left it escalates to the helpdesk.
4. A "Problem guides" entry in the `repair-packages` skill overrides the package order, so you steer fallbacks by editing the skill.

To add a package, see [Adding and signing packages](/docs/packages/).

## What the agent never does

- It never gets a device ID, a path, a URL, a command line or a script to run. Parameters are choices from lists, IDs the server resolves, names the PC itself reported, bounded integers and short search text.
- It never edits skills or memory during a user conversation. Skill writes are staged for an administrator and cross-user memory is disabled.
- It never repeats the same repair twice in one conversation.
- It never tells the user tool names, IDs, paths or JSON. Replies are simple Persian, at most three short sentences, and the conversation is closed only when the user says the problem is solved.
- It does not repair a PC when a known outage matches; it tells the user the outage is known.

## Organisation knowledge

Every site is different. An `ORGANIZATION.md` file on the control-plane server can tell the agent about this site (layout, services, support rules, quirks), and the agent reads it at the start of a conversation. It is background only: it never adds a permission, internal names are masked before the cloud model sees them, and it must not contain passwords. Live facts such as servers, printers and outages belong on the **Environment** page, not in the file. The template and review rules are in `docs/organization-knowledge.md`.

> This feature lives on the `claude/org-knowledge` branch; the paragraph applies once that branch is merged.

## After you change a skill or SOUL

Hermes only sees what is copied into its profile. After changing `deploy/hermes/SOUL.md` or a reviewed skill, run `scripts\sync-hermes-profile.ps1` (add `-Check` to see whether the profile is current) and restart the gateway. Skills published from the dashboard are written to the folder in `HelpdeskSkills:PublishPath`, or downloaded and copied by hand when that is empty.
