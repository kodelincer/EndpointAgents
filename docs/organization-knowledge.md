# Organization knowledge

Every site that installs EndpointAgents is different. The organization knowledge file tells the
helpdesk agent about *this* site (network layout, services, support rules, quirks) so its first
answer already fits. It is background only: it never gives the agent a new permission.

## What goes where

| Information | Where it lives |
| --- | --- |
| Story and rules of the site (this file) | `ORGANIZATION.md`, edited by an administrator |
| Servers, web apps, printers, shares, DNS suffixes, proxy and time standards, outages | Dashboard **Environment** page (live data) |
| How to fix a problem step by step | Hermes skills (`deploy/hermes/skills`) and the Capabilities page |

Do not copy Environment page data into the file; mention it in words ("the HR portal") and the agent
looks the id up itself.

## Fill it in (administrator, no development needed)

1. Copy `deploy\control-plane\organization\ORGANIZATION.template.md` to
   `C:\ProgramData\EndpointAgents\ControlPlane\Organization\ORGANIZATION.md`
   (create the folder if needed; the path is `Organization:KnowledgeFile` in the API settings).
2. Open it in Notepad or VS Code and fill each section in plain sentences. Skip what does not apply.
3. Replace `YYYY-MM-DD` after `Last reviewed:` with today's date.
4. Delete the instruction comment and the `TEMPLATE-NOT-FILLED` line. While that line is present
   the agent ignores the file.
5. Nothing to restart: the file is read on each conversation.

Do not write passwords, keys or personal data of users. The agent's tool masks internal host names,
shares, DNS-suffix names and IP addresses before the cloud model sees them, but secrets should never be in
the file at all.

## Keeping it correct

- Review it at least every 90 days and update `Last reviewed:`. After `Organization:StaleAfterDays`
  the agent is told the file may be outdated and trusts the PC's facts over it.
- Put changing facts (a new server, an outage) on the Environment page, not here.
- Name an owning team in `Owner:` so someone is responsible.

## How it reaches the agent

`get_site_knowledge` (MCP tool) returns the file with internal names masked. The `site-knowledge`
Hermes skill makes the agent call it at the start of each conversation. After pulling this change run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\sync-hermes-profile.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\sync-hermes-profile.ps1 -Check
```

then restart the profile gateway. Real file content is never stored in the repository or the Hermes
profile; it stays on the control-plane server.

## Example (fictional)

```
Organization: Acme Trading, a distribution company with a head office and two branches.
Owner: IT operations
Last reviewed: 2026-10-14

## 3. Core services
- Printing: one print server; every floor has one shared printer. Users must never install drivers.

## 4. Support rules
- Never: restart a PC on the accounting floor before 17:00.
- Hand to a person when: a user asks for new software or a locked account.

## 6. Known quirks
- Branch B loses the VPN around 13:00 daily; wait five minutes before anything else.
```
