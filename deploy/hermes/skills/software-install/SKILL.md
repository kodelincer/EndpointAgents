---
name: software-install
description: Install an approved application or font pack when the user asks for it or says it is missing (Google Chrome, Adobe Connect, the Windows Photos app, Persian fonts, Foxit PDF Reader or Editor), or remove an application the helpdesk lists as removable, after a helpdesk member approves. Uses administrator-approved packages only.
---

# Software Install

Administrators publish one reviewed install package per application. Each package first
checks whether the application is already on the PC and whether the PC meets the
software's system requirements, and installs it only when it is missing, silently and for
all users, from an installer kept on the control plane. Most installs wait for a helpdesk
member's approval; free software the administrators list as "No approval needed" installs
straight away. You only decide which application the user wants; the installer, its
options, the requirements, the approval rule and the checks are fixed by the administrator.

## Catalog

| The user wants | problem | Package, in try order |
| --- | --- | --- |
| Google Chrome | `install-chrome` | `chrome-install` |
| Adobe Connect (online classes and meetings) | `install-adobe-connect` | `adobe-connect-install` |
| The Windows Photos app | `install-photos` | `photos-install` |
| Persian (Farsi) fonts | `install-persian-fonts` | `persian-fonts-install` |
| Foxit PDF Reader, or just "a PDF reader" | `install-foxit-reader` | `foxit-reader-install` |
| Foxit PDF Editor, or editing PDF files | `install-foxit-editor` | `foxit-editor-install` |

Administrators keep this table; a row fixes the try order for an application. An
application without a row can still be installed: administrators publish more install
packages (any package whose `problem` starts with `install-`), and the catalog that
`find_repair_packages` returns is the full list.

## Workflow

1. Work out which application the user means. If it is unclear, ask one short question.
2. Call `mcp_controlplane_find_repair_packages` once with
   `{ "runCapability": "<the supplied run capability>" }`.
3. Choose the packages for that application:
   - If the table has a row for it, use the row's packages, in its order, that the result
     contains. If none of them is in the result, the installer is not ready yet: go to
     step 7.
   - Otherwise use the result's packages whose `problem` starts with `install-` and whose
     `displayName` or `symptoms` clearly name the same application, lowest `order` first.
     Never pick a package for a different application. If there is none, the application
     is not supported yet: follow the feature-requests skill.
   Take the first chosen package whose `alreadyTried` is false. If all of them were already
   tried, go to step 7.
4. If the chosen package's `hasRequirements` is true, you may first call
   `mcp_controlplane_check_software_requirements` once with exactly
   `{ "runCapability": "<the supplied run capability>", "packageId": "<chosen packageId>" }`
   when the user asks whether their PC can run it. `run_repair_package` always checks
   again by itself, so this step is optional.
5. Tell the user in one short sentence that you are installing the application (or that
   the request goes to the helpdesk when `requiresApproval` is true), then call
   `mcp_controlplane_run_repair_package` once with exactly
   `{ "runCapability": "<the supplied run capability>", "packageId": "<chosen packageId>", "reason": "<one English line: what the user asked for>" }`.
   It can take a few minutes. Run at most one package per reply.
6. Report the result plainly:
   - `status` `RequirementsNotMet`: the PC is not suitable. Tell the user the reason in
     simple words from `problems` (for example "your computer needs more memory for this
     program"), without numbers they would not understand, and offer the helpdesk. Do not
     run another package for the same application.
   - `status` `AwaitingApproval`: a helpdesk member was asked to approve the install. Tell
     the user in one sentence; the answer will appear in this chat. Do not run it again.
   - `status` `AwaitingHelpdesk`: this program is installed by a helpdesk member by hand.
     Tell the user in one sentence and stop.
   - `ok: true` and `exitCode` 10 (or `state` `AlreadyInstalled`): it was already installed
     and nothing changed. Say so and ask whether they can find and open it. Fonts: ask
     whether Persian text shows correctly. Photos: ask them to sign out and back in.
   - `ok: true` with `restartRequired: true`: it was installed and Windows needs the user
     to sign out and back in (fonts, Photos) or restart. Ask them to do that and then say
     whether it works. Never restart or sign out yourself.
   - `ok: true`: it was installed. Ask whether it opens and works.
   - `ok: false` with an error saying another installation is running: ask the user to try
     again in a few minutes.
   - other `ok: false`: the installation did not finish. Go to step 7, or to step 3 when
     the row lists another untried package.
   When the user answers yes, call `mcp_controlplane_mark_problem_resolved` once with
   exactly `{ "runCapability": "<the supplied run capability>" }` and confirm in one short
   sentence. When the user answers no, start again at step 2: the next untried package in
   the row is the fallback. Never run a package that was already tried.
7. When no package is ready, every package for the application was tried, or the user
   still has the problem, call `mcp_controlplane_escalate_to_helpdesk` once with exactly
   `{ "runCapability": "<the supplied run capability>", "reason": "<one English line>", "summary": "<short English summary>" }`.
   The summary names the application, the packages tried and their results (including
   "installer not ready" or "already installed"), and what the user said. Then tell the
   user that the helpdesk team will follow up. Also escalate when the user asks for a
   person.

## Removing software

Removal always waits for a helpdesk member's approval; the user cannot skip it.

1. Call `mcp_controlplane_run_endpoint_action` with `actionId` `software.inventory` and
   `{ "name": "<a short part of the application name>" }`.
2. Find the application the user means. If it is not installed, say so and stop.
3. If its `removable` is null, the helpdesk does not allow removing it: say so in one
   short sentence and offer to record the request with the feature-requests skill.
4. If `uninstallPackageId` is set, call `mcp_controlplane_run_repair_package` with that
   `packageId` and a short English `reason`. Otherwise call
   `mcp_controlplane_run_endpoint_action` with `actionId` `software.uninstall`,
   `{ "target": "<removable id>" }` and a short English `reason`.
5. The result is `AwaitingApproval`. Tell the user in one short sentence that the helpdesk
   was asked and the result will appear in this chat. Do not run it again.
6. If `software.uninstall` returns `Blocked` (it needs an uninstall package), escalate.

## Notes

- Foxit PDF Editor is licensed software. Installing it is allowed; activating or
  licensing it is not part of the package. If the user asks about activation or a license,
  escalate to the helpdesk.
- Never offer to download software from the internet, and never suggest another
  application in place of the one asked for.

Use only the ControlPlane tools above. Never use terminal, filesystem, browser, web,
code execution, memory, cron, delegation, learning, or skill-management tools. Never
request passwords, product keys, license keys, tokens, endpoint identifiers, paths, URLs,
commands, scripts, or secrets, and never describe a package's files or options.
ControlPlane binds the endpoint and user; do not attempt to select either. Follow the
profile `SOUL.md` for response language and presentation.
