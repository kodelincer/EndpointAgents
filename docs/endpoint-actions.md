# General endpoint actions

The support agent diagnoses and fixes everyday problems (network, internal web apps,
printers, shared drives, sign-in, slow PCs, desktop and apps) by combining small,
catalogued actions. It never runs commands it writes itself: every action is fixed code in
`Platform.Windows`, and the agent only picks an action id and listed values.

## Design rules

1. **One catalogue.** `src/EndpointAgents.Shared/Actions/EndpointActionCatalog.cs` defines
   every action: id, area, tier, where it runs, parameters, limit, verification and undo.
   The agent's tools, the dashboard's Capabilities (agent actions) page, the kill switches and the
   endpoint's own validation all read it.
2. **Tiers decide the rules.**

   | Tier | Meaning | Rules |
   | --- | --- | --- |
   | Look | Reads facts only | Runs freely; 30 per hour per PC by default |
   | SafeFix | Small, reversible fix | Runs without asking (no Tray card); default 3 per hour; checked before and after |
   | Disruptive | Interrupts the user (restart desktop or PC, close an app) | Needs `userConfirmed=true`, which the agent sets only after the user said yes in chat (offer now or later); default 2 per hour |
   | ItApproval | Needs a helpdesk member's yes (removing software) | The agent's request waits on the Approvals page; a member approves (it runs at once) or declines; the user sees the outcome in the chat; default 3 per hour |
   | OperatorOnly | Undo steps | Never offered to the agent; the helpdesk runs them from Recent runs |

3. **Typed parameters only.** Choice (fixed list), Target (an environment-map id the
   server resolves to a real address), LocalItem (a name the PC itself reported, such as a
   printer), Integer (with a range) and SearchText (letters, digits, spaces). Nothing is
   ever a path, URL, command or address chosen by the agent.
4. **No shell.** Actions use Windows APIs, WMI, the service controller, event logs and a
   fixed allow-list of System32 tools (`ipconfig`, `w32tm`, `gpupdate`, `klist`, `netsh`,
   `tzutil`, `shutdown`, `powercfg`, `msiexec` for removing listed software) with fixed arguments.
5. **Right identity.** System actions run in the LocalSystem service. User actions (proxy,
   printers, mapped drives, browser caches, keyboard layouts, saved passwords) run in the
   signed-in user's Tray through the one service pipe (`endpoint.user_action`).
6. **Proof.** A fix with `Verify` runs its Look action before and after; the result shows
   both. The agent then asks the user whether the problem is solved.
7. **Undo.** Fixes that change a user setting (proxy, default printer, time zone) store the
   previous values; the helpdesk can undo them from Capabilities → Recent runs.
8. **Privacy.** Results are masked before they reach the cloud model: environment-map
   addresses become their labels, names under the internal DNS suffixes become
   `[internal-host]`, UNC paths `[internal-share]`, IP addresses are removed.
   `endpoint_system_info` also masks IP addresses unless
   `Privacy:ShareIpAddressesWithAgent` is `true` in appsettings.
9. **Kill switch and limits.** Every action can be switched off or given another hourly
   limit on the Capabilities (agent actions) page, without a release.
10. **Audit.** Every run is stored (`action_runs`), written to the conversation's helpdesk
    timeline and to the tool audit; fixes are also recorded as repair attempts so the
    agent never repeats one in a conversation.

## Pieces

| Where | What |
| --- | --- |
| `Shared/Actions` | Contracts, catalogue, validator (used by both sides) |
| `Platform.Windows/Actions` | The actions (`SystemSide`, `UserSide`), infrastructure helpers, the `endpoint.action` RPC handler |
| `Tray.Runtime/TrayTaskDispatcher.cs` | Runs user-side actions in the user's session |
| `ControlPlane.Api/Actions` | `ActionRunner` (all checks), `ActionStore` (SQLite), `TargetMasker`, `ServerProbe`, `ActiveDirectoryAccountChecker`, `SkillService`, REST routes |
| `ControlPlane.Api/Tools/EndpointActionTools.cs` | The agent's MCP tools |
| `deploy/hermes/skills` | `endpoint-actions` plus symptom skills: `network-support`, `printing-support`, `shared-drives`, `signin-domain`, `slow-pc-health`, `desktop-apps` |
| Dashboard | `/capabilities`, `/skills`, `/environment` |

## Agent tools

| Tool | Purpose |
| --- | --- |
| `list_endpoint_actions` | The catalogue for an area (switched-off actions hidden) |
| `list_environment_targets` | Environment-map ids and labels (no addresses) |
| `run_endpoint_action` | Run one action through every check above (ItApproval returns `AwaitingApproval`) |
| `check_known_incidents` | Outages the helpdesk declared |
| `recent_reports_from_others` | What other users reported in the last hours |
| `probe_from_server` | Check a target from the server, to tell a PC fault from an outage |
| `check_user_account` | Read-only AD state of the signed-in user (locked, disabled, expired); never unlocks |
| `note_skill_used` | Record which skill guided a conversation (skill statistics) |
| `request_helpdesk_action` | Ask a person for work the PC must never do itself: `unlock-account`, `password-reset`, `local-admin`, `bitlocker-key`, `site-access`, `trust-repair`. Nothing runs on the PC; the request waits on Approvals |

## Environment map

Dashboard → Environment. Add the internal web apps, shared folders (with their standard
drive letter), office printers and key servers, by id and label. Optional ports and expected
IP addresses let the agent test the right port and spot wrong DNS answers. Standards: the
internal DNS suffixes (masked for the model), the proxy standard, the standard time zone and
the organisation's root CA thumbprints, and whether DHCP hands out the right DNS servers
(`dns.reset_to_dhcp` runs only when it does), whether every PC should get its IPv4 address
from DHCP (`ip.reset_to_dhcp` runs only when it is true) and the standard power plan
(`balanced`, `high` or `saver`, for `power.set_plan_standard`). Incidents: declare an outage so the agent tells
users it is known instead of repairing PCs.

These lists drive the helpdesk requests:

| Kind | Address | Details | Used by |
| --- | --- | --- | --- |
| `app` (Removable software) | The Apps & features name, `*` as wildcard, e.g. `Foxit PDF Reader*` | `publisher` (optional pattern), `packageId` (an uninstall package, for apps not installed by Windows Installer) | `software.inventory` marks matching apps `removable`; `software.uninstall` or the uninstall package removes them after approval |
| `device` (Driver catalog) | Hardware IDs separated by `;`, e.g. `USB\VID_04F9&PID_0042` | `packageId` (the signed driver package) | `drivers.inventory` marks matching devices with `catalogMatch`; the agent runs the driver package |
| `vpn` (Company VPN) | The VPN server's host name | `type` (`builtin` or `client`), `connection` (the Windows VPN connection name), `service` (the client's Windows service) | `vpn.status`, `vpn.client_restart` |
| `wifi` (Company Wi-Fi) | The SSID (1-32 characters) | `profile` (the Windows profile name your policy deploys, when it differs) | `wifi.status` marks company networks (`companyProfiles`); `wifi.connect_company` connects to one |
| `site` (Allowed work site) | A host name, or `*.domain` for all its sites | `category` (a short word such as banking) | `site.check_access` |

`zones.status` reads the PC's intranet zone list, but the control plane replaces it with one
line per web app (`sites`: its zone and whether policy sets it) before the model sees it.

Software the users may install is the set of install packages (`problem` starting with
`install-`), not a list here; see `docs/software-install.md`.

## Approvals

Dashboard → Approvals (every operator role). The agent's ItApproval requests, the actions
an admin switched to "Needs IT approval" on the Capabilities (agent actions) page, and packages whose
`problem` starts with `uninstall-` or whose manifest has `requiresApproval: true` wait here,
with the user, the PC, the item and the agent's reason. **Approve and run** runs it at once
through the same checks (switches, limits, before/after); **Decline** takes an optional note
the user sees. Either way the user gets a short helpdesk message in the Tray chat. Requests
are kept with who decided and the result.

Requests from `request_helpdesk_action` show as **Helpdesk task**. The member does the work
outside the system (Active Directory, the network filter, reading a BitLocker key to the
user by phone) and presses **Mark done**; the optional note goes to the user's chat. A note
that looks like a password or a 48-digit recovery key is refused, since the model reads
the chat too.

## Who can do what

Every page needs a signed-in operator. Members read the Capabilities (agent actions) page, approve or
decline requests, draft and submit skills and declare incidents. Reviewers also publish, send back, retire and roll
back skills, and undo fixes from Recent runs. Admins also change the environment map,
the standards and the action switches and limits.

## Configuration (appsettings.json)

```json
"ActiveDirectory": { "Enabled": false, "Server": "corp.example", "BaseDn": "DC=corp,DC=example" },
"HelpdeskSkills": { "PublishPath": "" },
"Privacy": { "ShareIpAddressesWithAgent": false }
```

- `ActiveDirectory`: read-only account checks. With no `BindUser`, the control-plane
  service's own identity is used (on a domain-joined server, LocalSystem reads AD as the
  computer account). If you add a read-only account, keep its password out of git.
- `HelpdeskSkills:PublishPath`: the Hermes profile's `skills` folder, for example
  `C:\Users\<admin>\AppData\Local\hermes\profiles\endpointagents\skills`. When empty,
  publishing stores the skill and the reviewer downloads `SKILL.md` and copies it by hand.
  Published skills are written as `hd-<name>\SKILL.md`; `scripts\sync-hermes-profile.ps1`
  never touches `hd-*` folders.

## Helpdesk skills

Helpdesk members write skills on the Agent Skills page: a name, a one-line description (Hermes
picks skills by it, so include the phrases users write) and the steps. **Check** lints the
draft: unknown tools or action ids, helpdesk-only actions, command-like text, wording that
reads like a permission, missing "ask the user" or escalation steps, IP addresses or UNC
paths. Drafts are submitted for review; a reviewer publishes, sends back, retires or rolls
back to an older version. "From conversation" starts a draft from a solved or escalated
chat. Usage statistics come from `note_skill_used`.

A skill only orders existing actions. It cannot add a tool, an action or a permission; the
SOUL rules, tiers, limits and kill switches still apply.

## Event log tools

`events.summary` (by area, now also `drivers`), `events.restart_reasons` and `events.app_errors` read
only a fixed slice of one log (fixed event ids, a time limit, at most 20 or 50 events), run only when the
agent asks in a chat, and return short grouped summaries. `EventText.Scrub` removes user folders,
account names, e-mail addresses, SIDs and share paths from every event text before it leaves the PC.
The program name for `events.app_errors` is matched in code, never put into the event query.

## Health rules

Scheduled health rules (dashboard **Automatic checks**, `ControlPlane.Api/Health`) run Look actions
through `ActionRunner` with source `HealthRule`, judge the answer with fixed code in
`HealthCheckCatalog`, and after the report-only week may run one of that check's SafeFix
actions or `notice.show`. `notice.show` is OperatorOnly (never offered to the agent); only health
rules may call it, and it shows one of the fixed Persian texts in `UserNoticeSink` on the PC.
No LLM call is made. See `docs/guide/en/health.md`.

## Adding a new action

1. Add the definition to `EndpointActionCatalog` (pick the lowest tier that fits; add
   `Verify` for fixes and `UndoActionId` when it changes a user setting).
2. Implement `IEndpointAction` in `Platform.Windows/Actions/SystemSide` or `UserSide` and add
   it to `EndpointActionSet`.
3. Mention it in the matching symptom skill, run the tests (catalogue consistency, skill
   lint over the repo skills, Hermes allow-list), then `scripts\sync-hermes-profile.ps1`.

No new MCP tool, Hermes allow-list entry or hash-pinned script is needed for a catalogue
action: `run_endpoint_action` is the one tool, and the action is compiled code, not a file.
