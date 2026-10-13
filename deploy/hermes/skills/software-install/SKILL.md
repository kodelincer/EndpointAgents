---
name: software-install
description: Install an approved application or font pack on the user's PC when they ask for it or say it is missing, such as Google Chrome, Adobe Connect, the Windows Photos app, Persian (Farsi) fonts, Foxit PDF Reader, or Foxit PDF Editor. Uses administrator-approved install packages only.
---

# Software Install

Administrators publish one reviewed install package per application. Each package first
checks whether the application is already on the PC and installs it only when it is
missing, silently and for all users, from an installer kept on the control plane. You only
decide which application the user wants; the installer, its options, and the checks are
fixed by the administrator.

## Catalog

| The user wants | problem | Package, in try order |
| --- | --- | --- |
| Google Chrome | `install-chrome` | `chrome-install` |
| Adobe Connect (online classes and meetings) | `install-adobe-connect` | `adobe-connect-install` |
| The Windows Photos app | `install-photos` | `photos-install` |
| Persian (Farsi) fonts | `install-persian-fonts` | `persian-fonts-install` |
| Foxit PDF Reader, or just "a PDF reader" | `install-foxit-reader` | `foxit-reader-install` |
| Foxit PDF Editor, or editing PDF files | `install-foxit-editor` | `foxit-editor-install` |

Administrators keep this table. A new application needs a package whose `problem` starts
with `install-`, plus a row here; no code change is needed.

## Workflow

1. Work out which application from the table the user means. If it is unclear, ask one
   short question. If the application is not in the table, it is not supported yet:
   follow the feature-requests skill.
2. Call `mcp_controlplane_find_repair_packages` once with
   `{ "runCapability": "<the supplied run capability>" }`.
3. Take the first package in the row's try order that the result contains with
   `alreadyTried` false. If the row's packages are missing from the result, the installer
   is not ready yet: go to step 6. If all of them were already tried, go to step 6.
4. Tell the user in one short sentence that you are installing the application, then call
   `mcp_controlplane_run_repair_package` once with exactly
   `{ "runCapability": "<the supplied run capability>", "packageId": "<chosen packageId>" }`.
   It runs without an approval prompt because the administrator approved it, and it can
   take a few minutes. Run at most one package per reply.
5. Report the result plainly and end with one short question:
   - `ok: true` and `exitCode` 10: it was already installed and nothing changed. Say so
     and ask whether they can find and open it. Fonts: ask whether Persian text shows
     correctly. Photos: ask them to sign out and back in, then check.
   - `ok: true` with `restartRequired: true`: it was installed and Windows needs the user
     to sign out and back in (fonts, Photos) or restart. Ask them to do that and then say
     whether it works. Never restart or sign out yourself.
   - `ok: true`: it was installed. Ask whether it opens and works.
   - `ok: false`: the installation did not finish. Go to step 6, or to step 3 when the row
     lists another untried package.
   When the user answers yes, call `mcp_controlplane_mark_problem_resolved` once with
   exactly `{ "runCapability": "<the supplied run capability>" }` and confirm in one short
   sentence. When the user answers no, start again at step 2: the next untried package in
   the row is the fallback. Never run a package that was already tried.
6. When no package is ready, every package for the application was tried, or the user
   still has the problem, call `mcp_controlplane_escalate_to_helpdesk` once with exactly
   `{ "runCapability": "<the supplied run capability>", "reason": "<one English line>", "summary": "<short English summary>" }`.
   The summary names the application, the packages tried and their results (including
   "installer not ready" or "already installed"), and what the user said. Then tell the
   user that the helpdesk team will follow up. Also escalate when the user asks for a
   person.

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
