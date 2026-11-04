---
title: Ideas and future features
summary: Reza's notebook of features the system should get one day, so no idea is forgotten.
order: 12
---

# Ideas and future features

This page is the place to keep ideas for the system, big or small, so none of them get lost. Ideas are not built unless their status says so. When an idea is picked for a batch, it moves to [How the system was born and where it is going](/docs/roadmap/), and when it is built it appears in the rest of this manual.

Every idea must still keep the promises of the system: only catalogued, fixed operations and signed packages (no free scripts), no Microsoft 365, Intune or Entra, nothing that needs GPO or SCCM, and it must work on Windows 10 and 11.

## How to add an idea

1. Open `docs/guide/en/ideas.md` and `docs/guide/fa/ideas.md` in the repository (both files must exist; the text can be in one language first).
2. Add a `###` heading with a short name under the right group, then a few lines: what it is, why it helps, and where the idea came from.
3. Add a line to the summary table with its status: **Idea**, **Picked** (chosen for a batch) or **Dropped** (with the reason).
4. Commit and push. The Documentation page shows it after the dashboard is rebuilt or restarted.

## Summary

| # | Idea | Group | Priority | Status | Since |
| --- | --- | --- | --- | --- | --- |
| A | Agent self-update and daily self-check | Before a wide rollout | 1 | Self-update built 2026-11-03; self-check is an idea | 2026-11-02 |
| B | Stored inventory and a Devices page | Before a wide rollout | 1 | Idea | 2026-11-02 |
| C | Offline queue, both ways | Before a wide rollout | 1 | Idea | 2026-11-02 |
| D | Health rules: fix problems before users call | Proactive care | 2 | Idea | 2026-11-02 |
| E | Device groups and pilot rings | Proactive care | 2 | Idea | 2026-11-02 |
| F | Required deployments of catalog software | Software | 3 | Idea | 2026-11-02 |
| G | Collect diagnostics | Helpdesk tools | 3 | Idea | 2026-11-02 |
| H | Fleet questions | Helpdesk tools | 3 | Idea | 2026-11-02 |
| I | Endpoint analytics | Later | 4 | Idea | 2026-11-02 |
| J | Peer sharing of large packages | Later | 4 | Idea | 2026-11-02 |
| K | Software metering | Later | 4 | Idea | 2026-11-02 |
| L | Remote help with the user's consent | Later | 4 | Idea | 2026-11-02 |
| M | Safe software install requests: approval, system requirements, one generic installer | Software | 2 | Idea | 2026-11-03 |
| N | Supervised learning from solved problems | AI agent | 2 | Idea | 2026-11-03 |
| O | Light, read-only event log checks | Helpdesk tools | 2 | Idea | 2026-11-03 |
| P | Network OS installation for new PCs | Later | 4 | Idea | 2026-11-03 |
| Q | Agent problem reports from pilot PCs | Before a wide rollout | 1 | Idea | 2026-11-04 |

Ideas A to L come from studying what the SCCM client and the Intune agent do (from Microsoft's public documentation only, never from their code), and what our system is still missing.

## Before a wide rollout

### A. Agent self-update and daily self-check

- **What:** the agent reports its version; the control plane keeps a target version per ring. An agent update is a signed package of its own kind; a small updater swaps the files and rolls back if the new version does not connect. A daily self-check makes sure the service and Tray run, the device credential is valid (re-enroll if not), the clock is right, the disk is not full and the control plane answers. It fixes what it can and reports the rest.
- **Why:** there is no GPO or SCCM to push new versions to hundreds of PCs, and today nobody knows when the agent on a PC is broken until the user calls.
- **Idea from:** SCCM automatic client upgrade and its daily client health check.
- **Built (2026-11-03):** the self-update, with pilot PCs, update hours, no update during a chat or a repair, and going back to the previous version by itself when the new one does not connect or keeps crashing. See [Updating the endpoint agent](/docs/updates/). The daily self-check is still an idea, and it must use fixed checks only, no AI.

### B. Stored inventory and a Devices page

- **What (recommended design, 2026-11-03): "keep, don't schedule".** The PC does not collect a full inventory on a timer. Whenever a tool reads facts from a PC (during a chat, or when an IT member opens the PC), the control plane stores the answer in its database with the time it was read. The next time the same fact is needed and the stored copy is fresh enough (for example 7 days for hardware, 1 day for installed software), the control plane gives the stored copy and does not ask the PC at all. The only regular message is the small one the agent already sends when it connects (version, Windows build, last boot, free disk). A **Devices** page shows the stored facts with their age, and an IT member can press **Refresh** for one PC.
- **Why:** today inventory is read only during a chat and then thrown away. This way the AI gets short, ready answers (fewer tokens), the PCs do no extra work, and IT still has a Devices page. Nothing here uses AI.
- **Built (2026-11-03):** kept facts for Windows, domain, hardware, disks, installed programs, drivers, disk health and BitLocker (network facts are never kept), the Devices page with **Read now**, and the agent gets a fresh stored answer instead of asking the PC.
- **Idea from:** SCCM hardware and software inventory.

### C. Offline queue, both ways

- **What:** work for a PC that is off waits on the server, with an expiry time, and runs when the PC connects. Results the PC could not send are kept on the PC and sent after it reconnects.
- **Why:** today commands reach only PCs that are online at that moment, and a result can be lost if the network drops during a fix.
- **Idea from:** SCCM's way of keeping messages on the PC until the server is reachable.

### Q. Agent problem reports from pilot PCs

- **What:** every PC sends the control plane a short report when something goes wrong in *our own* agent, and a **Problems** page on the dashboard shows them grouped, so the team knows what to fix in the next version. A report is sent for: a crash of the service, the Tray or the updater (with where in our code it broke); an error our code logs; an operation or package that failed or timed out (operation name, package name and version, return code, how long it ran); a health rule check that could not run; and an update that was rolled back. Each report carries the agent version, the PC name, Windows 10 or 11 and its build, and the time.
- **How it is kept small:** the same problem is sent once with a counter ("this happened 37 times since 09:10"), not 37 times. The PC keeps unsent reports in a small file (at most 2 MB, the oldest are dropped first) and sends them when it connects again, so nothing is lost when the network or the control plane is down. At most 50 reports per PC per hour. The control plane keeps reports for 90 days.
- **Private data:** every message is cleaned on the PC before it is sent (user names, folders, e-mail addresses and share paths are cut, with the same cleaner the event log tools use). Never sent: chat text, passwords, file contents, the user's documents.
- **The Problems page:** one row per kind of problem (same error at the same place in the code), with how many PCs and which versions have it, first and last seen. Filters by version, PC and kind. A row can be marked **Fixed in version X**; if it comes back on that version or later, it opens again by itself. A row can be copied as text for a developer.
- **No AI:** collecting, grouping and showing reports uses no AI at all, so it costs no tokens.
- **PCs that cannot report:** a crashed or removed agent sends nothing, so the page also lists PCs that have gone quiet for more than a day and PCs stuck on an old version.
- **Failed installs:** before the agent is running there is nobody to send a report, so the installer itself sends one short "install failed at step X" message (or leaves a log file that can be collected).
- **The control plane cannot fill up:** besides the 90 days, the reports have a hard size limit (for example 500 MB); when it is reached the oldest reports are deleted first, so logs can never fill the disk or stop the server. Reports live in their own database file, not in the main one, so a flood of reports cannot slow down chats and actions, and the PC limit is checked on the server too. For 100 PCs with the counters and the hourly cap this is expected to stay at a few tens of MB.
- **Optional log server (Reza, 2026-11-04):** in **Settings** an admin can add an outside log server. The control plane (not each PC) then also sends a copy of every report there, in a standard format (Syslog, or JSON over HTTP such as Graylog GELF or Seq), so any common log server works and only the server needs a firewall rule. Reports are already cleaned on the PC before this. If the log server is down, the control plane keeps a small, limited queue and drops the oldest copies; it never waits for it. The Problems page stays on the control plane for the short grouped view; the log server is for long keeping and searching. Not needed for the first pilot.
- **Related ideas:** the full cleaned logs of one PC on request is idea G (Collect diagnostics); installing on a few PCs first is idea E and the pilot PCs of the self-update. A user saying "this did not fix it" is already recorded as an unsolved chat for the Helpdesk tab.
- **Builds on what exists:** the PC already writes its own events to a local journal (`service-events.jsonl`, `tray-events.jsonl`) and the Windows event log, and the self-update already counts crashes in its first 24 hours and reports rollbacks to the **Agent updates** page. This idea adds the sending, the counting and the page; it does not replace them.
- **Why:** for a pilot on about 100 PCs, nobody should have to wait for a user to call to learn that a version is broken, and developers need the exact place and version of an error to fix it.
- **Idea from:** Reza, 2026-11-04, before the pilot.

## Proactive care

### D. Health rules: fix problems before users call

- **What:** an admin makes a rule on the dashboard: a check (one Look operation), an optional fix (one Fix operation), a schedule and a device group. Disruptive fixes become a notice in the Tray; IT-tier fixes become an approval request. A **Compliance** page shows how many PCs pass, were fixed or still fail. The AI is not involved, so this is fully predictable.
- **Why:** many tickets can be stopped before they happen.
- **First rules to consider:** KMS activation close to expiry, old Defender signatures, almost full disk, a restart pending for many days, clock out of sync, time zone or proxy different from the Environment map, Tray not running.
- **Idea from:** Intune Remediations and SCCM configuration baselines.
- **Keeping the choices small (Reza, 2026-11-03):** IT members decide *what* is checked, never write code. A rule has only five answers to pick: which check (from a fixed list), which fix (from the fixed list for that check, or none), how often (daily or weekly, at a random minute so PCs do not run at once), which PCs (all, pilot or a group), and what to do when it fails (only report, fix by itself, or tell the user in the Tray). Every new rule starts in **only report** mode for a week, so IT sees how many PCs would be touched before any fix runs. Start with 5 rules; the list grows only when a new fixed check is built.
- **Built (2026-11-03):** seven fixed checks, the five answers, a random minute per PC, the report-only first week, Tray messages and the pass/fixed/fail counts. See [Health rules](/docs/health/). Proxy, KMS expiry and "Tray not running" checks are still to build. Device groups (idea E) will replace typing PC names.

### E. Device groups and pilot rings

- **What:** groups by rule (AD OU, site or IP range from the Environment map, Windows 10 or 11, model) or by hand, and rings: Pilot first, then Everyone. Anything sent to many PCs needs a second person to approve it (the author cannot approve their own change).
- **Why:** health rules, deployments and agent updates need targets, and a mistake should reach five PCs, not all of them.
- **Idea from:** SCCM collections and Intune groups.

## Software

### F. Required deployments of catalog software

- **What:** choose a package from the Software catalog, a group, a deadline and a maintenance window. Each install package gets a detection rule from a fixed list (MSI product code, file version, uninstall entry), so the PC knows when the software is already there. The Tray warns before a restart.
- **Why:** today software is installed only when a user asks for it in chat.
- **Idea from:** SCCM and Intune application deployments with detection methods.

### M. Safe software install requests

- **What:** when a user asks for software, the PC first checks the software's system requirements (Windows build and edition, x64, memory, free disk, CPU, screen, required runtimes, conflicting apps). If they are not met, the user is told why and no approval is asked. If they are met, the request goes to the **Approvals** page by default, unless the admin put that software on a **No approval needed** list. Right before installing, the PC checks again. Instead of a BAT per app, each package describes its install in typed, signed fields (installer kind, silent switches, detection rule, return codes) and the endpoint runs it with compiled code. An installer that cannot run silently becomes a Helpdesk task for a person.
- **Why:** vendors ship installers in many ways, and nobody should approve or install something the PC cannot run.
- **Idea from:** Reza (2026-11-03), and Intune's Win32 apps (requirement rules, detection rules, return codes).
- **Design:** `safe-software-install-design.md` in the project's design folder.

## Helpdesk tools

### G. Collect diagnostics

- **What:** one catalogued action that zips a fixed list of our own logs and filtered Windows event logs, masks them and uploads them to the case.
- **Why:** the helpdesk often needs logs and should not have to ask the user to find them.
- **Idea from:** the Intune "Collect diagnostics" remote action.

### O. Light, read-only event log checks

- **What:** a few fixed, read-only tools that read the Windows event log for the agent: "errors of the last 24 hours for this app", "why did the PC restart", "last blue screen", "failed Windows updates", "disk and driver warnings". Each one reads only a small, fixed slice (one log, a few event IDs, a time limit, at most 50 events), runs only when the agent asks during a chat, and sends back a short summary instead of the raw log. Personal data (user names in messages, paths) is cut before it leaves the PC.
- **Why:** many causes are written in the event log; today the agent cannot see it. Small, fixed queries keep the load on the PC and on the AI tiny.
- **Idea from:** Reza, 2026-11-03.
- **Built (2026-11-03):** "why did the PC restart", "crashes of one program" and a "driver warnings" area, next to the existing error summary. Every event text is cleaned of user names, folders, e-mail addresses and share paths on the PC.

### H. Fleet questions

- **What:** saved, read-only questions over the stored inventory, such as "which PCs have Office not activated?" or "who has less than 5 GB free?".
- **Why:** answers in seconds instead of checking PCs one by one. Needs idea B.
- **Idea from:** SCCM CMPivot.

## AI agent

### N. Supervised learning from solved problems

- **What:** after each solved chat, the control plane writes a short "lesson" (symptom, what was tried, what fixed it), without AI where possible. Lessons wait in a **Lessons** queue on the dashboard. An IT member reads them and chooses: turn it into a skill change, edit it, or drop it. Only approved lessons ever reach Hermes, as a reviewed skill update with a version, and every change can be undone. All lessons are kept for later analysis (which problems come back, which fixes work best).
- **Why:** the agent gets better from real tickets, but people stay in control, and nothing changes the agent by itself.
- **Idea from:** Reza, 2026-11-03.
- **Built (2026-11-03):** the **Lessons** page. A lesson is written a few minutes after a chat is marked solved, from the chat's first message and the steps that ran (no AI). Reviewers choose **Start skill draft** (opens the Skills page with that chat; the draft is checked, published and can be rolled back there), **Keep** or **Drop**. All lessons stay, and the top of the page counts which fixes solved the most chats.

## Later

### I. Endpoint analytics

Boot time, application crashes and blue screens from the event log, with trends per model and per site, so slow or failing PCs are found early. Idea from Intune Endpoint analytics.

### J. Peer sharing of large packages

For sites on slow links: resume large downloads (BITS) first, then let PCs on the same site share a package they already have. Idea from SCCM peer cache and BranchCache.

### K. Software metering

Count how often licensed applications are really used, to free licences nobody uses. Idea from SCCM software metering.

### L. Remote help with the user's consent

Start the company's remote-support tool from the chat, only after the user says yes. The operation catalog already lists "remote-support launch" for batch 3.

### P. Network OS installation for new PCs

A new or empty PC starts from the network (PXE) into a small Windows PE, and installs Windows from an approved image. IT members write the steps as skills and fixed tools: how to partition the disk, which drivers to add for each PC model, and which software the PC and its users need. The AI only picks among those approved steps; it never makes up disk or install commands. Idea from Reza, 2026-11-03 (earlier listed under "not to copy"; now an idea of its own because new PCs need it).

**Design (2026-11-03):** fixed, signed steps only (identify, partition with a named layout, apply a hash-pinned image, add the model's drivers, fill a fixed answer file, install the agent, restart), an IT member approves each new PC by serial number before anything is erased, and the domain join uses an offline join blob so no password reaches the PC. Start with a USB stick, then PXE. Not built yet: it needs a PXE server and a spare PC or VM that can be wiped. Full design in `docs/network-os-install.md`.

## Things we decided not to copy

- **Free scripts** (SCCM Run Scripts, Intune platform scripts): they break the rule that only catalogued operations and signed packages run.
- A **Windows Update server** replacement and the **MDM channel**: each is a large product of its own. (Operating system installation is now idea P.)
- **Code from Microsoft's agents**: their licence forbids reverse engineering, so we learn only from public documentation and build our own code.
