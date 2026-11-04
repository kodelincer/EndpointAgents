---
title: Dashboard pages and troubleshooting
summary: What each page is for, who can change what, and what to check when something does not work.
order: 11
---

# Dashboard pages and troubleshooting

## What each page is for

| Page | What you do there | Who |
| --- | --- | --- |
| **Overview** | A dashboard for the last 7, 14, 30 or 90 days: key-number tiles (PCs online, chats, share solved by the agent, chats waiting for the helpdesk, fixes run, approvals waiting, failing health checks, new feature requests), charts of chats and fixes per day, PCs by connection, Windows 10/11 and agent version, the most used operations and repair packages with their success rate, health rules, agent update results, and short lists of waiting approvals, chats sent to the helpdesk, feature requests and lessons. Every tile and card links to its page. Below it: the control plane service state and recent activity. Admins can switch maintenance mode on or off. Counting only, no AI. | Everyone reads, admins change |
| **Endpoints** | The PCs that are connected. Admins can send a catalogued command; the user still sees a consent card for commands that change the PC. | Everyone reads, admins send |
| **Devices** | What is known about each PC from earlier checks (Windows, hardware, disks, programs, drivers) and how old it is. **Read now** asks one PC again. PCs are never asked on a timer. | Everyone reads |
| **Health rules** | Daily or weekly checks that fix common problems before users call. See [Health rules](/docs/health/). | Everyone reads, admins change |
| **Problems** | What went wrong in our own agent on the PCs, which PCs went quiet or stayed on an old version, and Get logs for one PC. See [Problems and log server](/docs/problems/). | Reviewers and admins read, admins get logs |
| **Settings** | The outside log server (Splunk, Graylog or Syslog). See [Problems and log server](/docs/problems/). | Admins only |
| **Packages** | Every repair and install package with its status, symptoms, requirements, hashes, signer and exactly what to fix when it is not Ready. Admins switch an install package to **No approval needed**. | Everyone reads, admins switch |
| **Helpdesk** | Live conversations, escalated cases, take over, read-only diagnostics. See [Daily helpdesk work](/docs/helpdesk/). | Members and above |
| **Approvals** | Requests that wait for a helpdesk member: removing software, uninstall packages, and fixes an admin marked "Needs IT approval". **Approve and run** runs it at once; **Decline** takes a note the user sees. | Members and above |
| **Lessons** | Every solved chat leaves a short lesson (what the user said, what was tried, what fixed it), written without AI. A reviewer starts a skill draft from it, keeps it for analysis, or drops it; only a skill a reviewer publishes reaches the agent. The top shows which fixes solved the most chats. | Everyone reads, reviewers decide |
| **Feature requests** | Things users asked for that the system cannot do yet: New, Planned, Done, Declined. | Reviewers and above |
| **Capabilities** | Four tabs: **Actions** (switches and limits, admins change them), **Agent tools**, **Writing a skill**, **Recent runs** (reviewers can undo a fix). | Everyone reads |
| **Skills** | Draft, check, submit, publish, retire and roll back skills. | Members draft, reviewers publish |
| **Environment** | Three tabs: **Targets** (servers, web apps, printers, shares, plus **Removable software**, the **Driver catalog** of device models, and **Company VPNs**), **Standards** (DNS suffixes, proxy, time zone, root CAs) and **Incidents**. | Everyone declares incidents, admins edit the rest |
| **Settings** | The organisation **KMS server** (host name or IPv4, optional port, 1688 by default) used to activate volume Windows and Office. **Test from server** checks the server answers before you save. | Admins |
| **Operators** | Create local accounts, add domain users, change roles, disable accounts, reset passwords. | Admins |
| **User manual** | This guide. | Everyone |

## Finding your way

All pages are in the sidebar on the left, grouped as **Monitor**, **Support**, **Agent setup**, **Administration** (admins only) and **Help**. The current page is highlighted, and red numbers show items waiting (for example open helpdesk cases).

- The button at the top of the sidebar shrinks it to icons only; hover an icon to see its name. The dashboard remembers your choice on this computer.
- On a narrow window the sidebar is hidden. Open it with the menu button in the top bar, and close it with **Esc**, the close button or a click outside.
- The top bar shows the page name, whether live updates are connected, how many endpoints are online, and a refresh button.

The account button at the bottom of the sidebar shows who you are and your role, and lets local accounts change their password or sign out. Domain accounts change their password in Active Directory.

## First start

The first time the API starts it creates an `admin` account and writes its one-time password to `initial-admin-password.txt` next to the database (`.local\` when developing, `C:\ProgramData\EndpointAgents\ControlPlane\` in production). Sign in, choose a new password, then delete the file. Create personal accounts on the Operators page; each one-time password is shown once.

## Daily checks

1. **Helpdesk:** escalated cases first.
2. **Overview:** is the connection state Live, and are the expected agents online?
3. **Packages:** anything not Ready, Not signed or Hash mismatch?
4. **Feature requests:** new ones to triage.
5. **Capabilities, Recent runs:** fixes that failed or were undone.

## Troubleshooting

| Symptom | Likely cause and what to do |
| --- | --- |
| The top bar says Unavailable or Reconnecting | The live connection to the API is down. Check that the API is running and that `http://localhost:5052/health/live` answers 200. |
| Port 5052 shows JSON, not a page | Expected. The dashboard is on port 3000. |
| HTTP 403 from another PC | Expected. The dashboard and operator APIs are only available on the server itself. |
| The dashboard does not load after a reboot | The dashboard Node process is started by hand (`npm.cmd run start` in `src\ControlPlane.Web`). It does not restart by itself. |
| Sign-in says the domain server is unavailable | Use a local account, or try again later. Keep one local admin for this reason. |
| Too many wrong passwords | The name is locked for 15 minutes. |
| `/health/ready` returns 503 or commands fail with 503 | Maintenance mode is on. An admin can turn it off on Overview. |
| An agent never connects | The endpoint must use the server's real host name, not `localhost`. Check enrollment, and that the device was not revoked. |
| A package is missing or Invalid | Open it on Packages and read the line that starts with the field to fix. After copying a changed package folder to the server, refresh the page. |
| The agent ignores a new skill or SOUL change | Hermes only sees what is in its profile. Run `scripts\sync-hermes-profile.ps1 -Check`, then without `-Check`, and restart the gateway. |
| An action is missing from the agent's list | It was switched off, or reached its hourly limit. See Capabilities. |

For installation, ports and routes see `docs/control-plane-operations.md`, and for enrollment and Hermes setup see `docs/hermes-support-agent.md`.
