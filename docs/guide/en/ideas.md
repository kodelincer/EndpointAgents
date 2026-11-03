---
title: Ideas and future features
summary: Reza's notebook of features the system should get one day, so no idea is forgotten. Nothing on this page is built yet.
order: 12
---

# Ideas and future features

This page is the place to keep ideas for the system, big or small, so none of them get lost. **Nothing here is built yet.** When an idea is picked for a batch, it moves to [How the system was born and where it is going](/docs/roadmap/), and when it is built it appears in the rest of this manual.

Every idea must still keep the promises of the system: only catalogued, fixed operations and signed packages (no free scripts), no Microsoft 365, Intune or Entra, nothing that needs GPO or SCCM, and it must work on Windows 10 and 11.

## How to add an idea

1. Open `docs/guide/en/ideas.md` and `docs/guide/fa/ideas.md` in the repository (both files must exist; the text can be in one language first).
2. Add a `###` heading with a short name under the right group, then a few lines: what it is, why it helps, and where the idea came from.
3. Add a line to the summary table with its status: **Idea**, **Picked** (chosen for a batch) or **Dropped** (with the reason).
4. Commit and push. The Documentation page shows it after the dashboard is rebuilt or restarted.

## Summary

| # | Idea | Group | Priority | Status | Since |
| --- | --- | --- | --- | --- | --- |
| A | Agent self-update and daily self-check | Before a wide rollout | 1 | Idea | 2026-11-02 |
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

Ideas A to L come from studying what the SCCM client and the Intune agent do (from Microsoft's public documentation only, never from their code), and what our system is still missing.

## Before a wide rollout

### A. Agent self-update and daily self-check

- **What:** the agent reports its version; the control plane keeps a target version per ring. An agent update is a signed package of its own kind; a small updater swaps the files and rolls back if the new version does not connect. A daily self-check makes sure the service and Tray run, the device credential is valid (re-enroll if not), the clock is right, the disk is not full and the control plane answers. It fixes what it can and reports the rest.
- **Why:** there is no GPO or SCCM to push new versions to hundreds of PCs, and today nobody knows when the agent on a PC is broken until the user calls.
- **Idea from:** SCCM automatic client upgrade and its daily client health check.

### B. Stored inventory and a Devices page

- **What:** a light heartbeat every 15 minutes and a full inventory once a day: model, serial, CPU, memory, disk, Windows version, last boot, installed software, drivers, Windows and Office activation, Defender and BitLocker state (never keys). Stored with history and shown on a new **Devices** page. The agent can read the stored facts instead of asking the user.
- **Why:** today inventory is read only during a chat and then thrown away.
- **Idea from:** SCCM hardware and software inventory.

### C. Offline queue, both ways

- **What:** work for a PC that is off waits on the server, with an expiry time, and runs when the PC connects. Results the PC could not send are kept on the PC and sent after it reconnects.
- **Why:** today commands reach only PCs that are online at that moment, and a result can be lost if the network drops during a fix.
- **Idea from:** SCCM's way of keeping messages on the PC until the server is reachable.

## Proactive care

### D. Health rules: fix problems before users call

- **What:** an admin makes a rule on the dashboard: a check (one Look operation), an optional fix (one Fix operation), a schedule and a device group. Disruptive fixes become a notice in the Tray; IT-tier fixes become an approval request. A **Compliance** page shows how many PCs pass, were fixed or still fail. The AI is not involved, so this is fully predictable.
- **Why:** many tickets can be stopped before they happen.
- **First rules to consider:** KMS activation close to expiry, old Defender signatures, almost full disk, a restart pending for many days, clock out of sync, time zone or proxy different from the Environment map, Tray not running.
- **Idea from:** Intune Remediations and SCCM configuration baselines.

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

### H. Fleet questions

- **What:** saved, read-only questions over the stored inventory, such as "which PCs have Office not activated?" or "who has less than 5 GB free?".
- **Why:** answers in seconds instead of checking PCs one by one. Needs idea B.
- **Idea from:** SCCM CMPivot.

## Later

### I. Endpoint analytics

Boot time, application crashes and blue screens from the event log, with trends per model and per site, so slow or failing PCs are found early. Idea from Intune Endpoint analytics.

### J. Peer sharing of large packages

For sites on slow links: resume large downloads (BITS) first, then let PCs on the same site share a package they already have. Idea from SCCM peer cache and BranchCache.

### K. Software metering

Count how often licensed applications are really used, to free licences nobody uses. Idea from SCCM software metering.

### L. Remote help with the user's consent

Start the company's remote-support tool from the chat, only after the user says yes. The operation catalog already lists "remote-support launch" for batch 3.

## Things we decided not to copy

- **Free scripts** (SCCM Run Scripts, Intune platform scripts): they break the rule that only catalogued operations and signed packages run.
- **Operating system deployment**, a **Windows Update server** replacement and the **MDM channel**: each is a large product of its own.
- **Code from Microsoft's agents**: their licence forbids reverse engineering, so we learn only from public documentation and build our own code.
