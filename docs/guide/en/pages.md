---
title: Dashboard pages and troubleshooting
summary: What each page is for, who can change what, and what to check when something does not work.
order: 7
---

# Dashboard pages and troubleshooting

## What each page is for

| Page | What you do there | Who |
| --- | --- | --- |
| **Overview** | See the state of the control plane, agents online and recent activity. Admins can switch maintenance mode on or off. | Everyone reads, admins change |
| **Agents** | The PCs that are connected. Admins can send a catalogued command; the user still sees a consent card for commands that change the PC. | Everyone reads, admins send |
| **Packages** | Every repair package with its status, symptoms, hashes, signer and exactly what to fix when it is not Ready. | Everyone reads |
| **Helpdesk** | Live conversations, escalated cases, take over, read-only diagnostics. See [Daily helpdesk work](/docs/helpdesk/). | Members and above |
| **Approvals** | Requests that wait for a helpdesk member: removing software, uninstall packages, and fixes an admin marked "Needs IT approval". **Approve and run** runs it at once; **Decline** takes a note the user sees. | Members and above |
| **Feature requests** | Things users asked for that the system cannot do yet: New, Planned, Done, Declined. | Reviewers and above |
| **Capabilities** | Four tabs: **Actions** (switches and limits, admins change them), **Agent tools**, **Writing a skill**, **Recent runs** (reviewers can undo a fix). | Everyone reads |
| **Skills** | Draft, check, submit, publish, retire and roll back skills. | Members draft, reviewers publish |
| **Environment** | Three tabs: **Targets** (servers, web apps, printers, shares, plus **Removable software**, the **Driver catalog** of device models, and **Company VPNs**), **Standards** (DNS suffixes, proxy, time zone, root CAs) and **Incidents**. | Everyone declares incidents, admins edit the rest |
| **Operators** | Create local accounts, add domain users, change roles, disable accounts, reset passwords. | Admins |
| **Documentation** | This guide. | Everyone |

The account menu in the top right shows who you are and your role, and lets local accounts change their password. Domain accounts change their password in Active Directory.

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
