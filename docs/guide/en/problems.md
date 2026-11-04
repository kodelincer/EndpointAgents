---
title: Problems and log server
summary: How you learn what went wrong on the PCs without waiting for a phone call, how to run the pilot, how to get the logs of one PC, and how to send everything to Splunk or another log server.
order: 10
---

# Problems and log server

When the agent is on many PCs, most problems never reach you as a call: a user just stops using the Tray, or a fix fails quietly. The **Problems** page shows them by itself. Nothing here uses the AI, so it costs no tokens.

## What gets reported

| Kind | What it means | Who notices |
| --- | --- | --- |
| Crash | The service, the Tray or the updater stopped on an error in our code, including an instant crash that Windows recorded in its log. These can arrive a few minutes late, or after the next restart | The PC |
| Logged error | Our code wrote an error to its log | The PC |
| Failed operation | An operation failed or timed out on a PC, or a repair finished but its own check says the problem is still there. A user saying no to a repair does not count | The control plane |
| Failed package | A repair package could not run or failed | The control plane |
| Health check did not run | A health rule could not check a PC | The control plane |
| Update rolled back | A new agent version failed and the PC went back | The control plane |
| Install failed | The installer stopped at a step | The installer |
| User: did not fix it | A user pressed **مشکلم حل نشد** under an answer in the chat. The chat also goes to the Helpdesk tab | The user |

Every report says which agent version, PC, Windows version and build, and PC model it came from.

**What is never sent:** chat text, passwords, file contents, the user's documents. User names, user folders, e-mail addresses, share paths and passwords are cut from the text on the PC, before it is sent, and cut again on the control plane.

## Reading the page

Open **Problems** under Monitor (reviewers and admins). There is one row per kind of problem, however many times it happened. Each row shows how many times, on how many PCs, which versions, and first and last seen.

- **Filters:** status, kind, version, Windows, model and PC name.
- **Group by:** kind, version, Windows or model. Use it to see whether a kind of PC has more problems than others.
- **"only ..." labels:** a row says *only Windows 10 22H2* or *only Dell OptiPlex 7080* when every PC with the problem shares that and other PCs in the fleet do not. That is usually the fastest clue to a hardware or Windows bug. The label appears only when the page has seen PCs that differ.
- **Details:** the list of PCs, and where in our code it broke.
- **Copy for developer:** everything a developer needs, as text.
- **Fixed in version:** type the version that has the fix, such as 0.4.0. The row leaves the Open list. If the problem comes back on that version or a later one, the row opens again by itself and says *Came back*. On an older version it stays closed, because that PC only has not updated yet.

Lower on the page:

- **PCs that went quiet:** no contact for more than a day. A crashed or removed agent cannot report, so this is how you notice it. A PC that is simply off for the weekend also appears here.
- **PCs on an old version:** behind the version set on **Agent updates**. With no version set, behind the newest version seen on any PC.

## How to run the pilot

Do not install on all 100 PCs at once. A bad version should hurt 10 PCs, not 100.

1. **First group, 5 to 10 PCs.** Choose a mix: Windows 10 and Windows 11, and different models, including the oldest ones you have. Choose people who will tell you when something is odd.
2. **Wait a few days.** Check the Problems page every day. Fix what appears, and mark it fixed in the new version.
3. **Second group, about 30 PCs.** Wait a few days again.
4. **Then the rest.**

**Do the same for every agent upgrade.** On **Agent updates**, set the new version as the pilot version on those first PCs, watch the Problems page for a few days, and only then set it for everyone. See [Agent updates](/docs/updates/).

**Every day during the pilot:**

- Are there new rows on Problems, or rows that say *Came back*?
- Do any rows have an *only ...* label?
- Did any PC go quiet that should be on?
- Did an install fail? Those rows show the step it stopped at.

## Get logs from one PC

When a report is not enough, an admin can get the logs of one online PC: on the Problems page, under **Get logs from one PC**, choose the PC and press **Get logs**.

The PC collects a fixed list: our own logs (service, Tray, updater, install) and our own entries from the Windows event log for the last 7 days. Every line is cleaned on the PC. The zip is at most 10 MB, newest lines kept. It is kept on the control plane for 30 days, then deleted. Nobody can choose other files or run anything with this button.

## When an install fails

`scripts\install-endpoint.ps1` writes `C:\ProgramData\EndpointAgents\Reports\install-failure.log` on the PC and sends one small report to the control plane with the step and the error. If the PC cannot reach the control plane, only the log file exists, so the PC is simply missing from the dashboard. Compare your count of PCs with the Devices page.

Any installer can reach this one route without signing in, because no agent exists yet. It accepts one small report, it is limited per address and in total, and it can only add a row. A row of this kind is not proof that the PC is really what it says.

## Send everything to a log server

The Problems page keeps the short grouped view for 90 days. A log server is for keeping reports longer and searching them. It is optional and off by default.

Open **Settings** under Administration (admins only).

| Type | Works with | What to enter |
| --- | --- | --- |
| Splunk | Splunk HTTP Event Collector | Address, port (usually 8088), HEC token, optional index |
| Graylog | Graylog GELF HTTP input | Address, port (usually 12201) |
| Syslog | ELK, Wazuh, rsyslog and others | Address, port, UDP, TCP or TLS |

**For Splunk:** in Splunk, open Settings > Data inputs > HTTP Event Collector, create a new token and turn on HTTP Event Collector in its Global Settings. Then enter the address, port and token here and press **Send test**. In Splunk, search for `sourcetype="endpointagents:problem"`.

**For Graylog:** in Graylog, open System > Inputs and start a GELF HTTP input.

Good to know:

- **Only the control plane sends**, so only this server needs a firewall rule. The PCs do not know the log server exists.
- **The token is stored protected** and never shown again. Leave the box empty to keep it.
- **Encryption:** Splunk needs HTTPS, so the token never crosses the network in clear text. Graylog over HTTP and Syslog over UDP or TCP are not encrypted, and the page says so.
- **A server with its own certificate:** the certificate has to be trusted on this server, or you can enter its SHA-256 fingerprint.
- **If the log server is down:** copies wait in a small queue (2,000) and the oldest are dropped. The PCs and the dashboard are never held up, and a red warning shows on Problems and Settings. Copies that wait are lost if the control plane restarts; the reports themselves stay on the Problems page.

## The server cannot fill up

- Reports live in their own file (`problems.db`, next to the main database), so a flood cannot slow down chats and actions.
- The same problem is counted, not stored again, so the file grows with the number of different problems, not with how often they happen.
- A PC sends at most 50 reports an hour, and the control plane checks that too. A PC waiting to send keeps at most 2 MB, oldest dropped first.
- Reports are deleted after 90 days. Past 1 GB, the oldest go first.
- When the disk has less than 1 GB free, new reports are not saved and the page says so.

These limits are in `appsettings.json`, section `Problems`.
