---
name: office-support
description: Diagnose Microsoft Office and run a version-compatible approved activation repair after verifying that Office applications are closed. Use for Office activation failures, Product Unlicensed, license or activation errors, activation-required prompts, and the red activation warning shown at the top of Word, Excel, PowerPoint, or other Office apps.
---

# Office Support

For an activation problem, Product Unlicensed state, activation error, or red Office
activation warning:

1. Call `mcp_controlplane_office_inventory` once with exactly
   `{ "runCapability": "<the supplied run capability>" }`.
2. If Office is not installed or its version cannot be read, explain that and stop.
3. Call `mcp_controlplane_office_activation_status` once with exactly
   `{ "runCapability": "<the supplied run capability>" }` and briefly summarize its
   sanitized state. Ignore products whose error code is `0XC004F014`: that only means no
   key is installed for that edition, which is normal for the extra editions a volume
   install lists. KMS is healthy only when the OSPP probe succeeded and, among the
   remaining products, there is at least one and every one has license type `Volume|KMS`,
   state `Licensed`, and no error code. The result's `overallState` already follows this
   rule. If KMS is healthy, say so and stop. A licensed MAK or Retail
   product is not healthy for this environment. The user's explicit activation report
   remains valid evidence when the probe is unavailable.
4. For an unhealthy result, or an explicit activation problem when the probe is
   unavailable, try the organisation KMS server first. Call
   `mcp_controlplane_run_endpoint_action` with `actionId` `kms.check`. If `Configured`
   and `Reachable` are both true and at least one `Office.Products` entry has
   `VolumeClient` true, call `mcp_controlplane_run_endpoint_action` with `actionId`
   `office.kms_activate` (no arguments; it runs without asking, and Office apps may stay
   open). If it returns `Done`, call `mcp_controlplane_office_activation_status` again
   and report with the healthy KMS rule from step 3. If the KMS server is not set, does
   not answer, there is no volume Office, or `office.kms_activate` returns `Blocked` or
   `Failed`, continue with the repair below. Never run `office.kms_activate` twice in one
   conversation. Then call `mcp_controlplane_list_repair_attempts` once with exactly
   `{ "runCapability": "<the supplied run capability>" }`. Choose the next repair with
   "Try order" below: the first listed ID that is in `compatibleBugfixArtifactIds` and is
   not among the attempts' `artifactId` values. If no listed ID qualifies, use the first
   other compatible ID that was not tried. If the compatible list is empty, say that no
   compatible approved repair is installed and escalate (see "Closing the ticket"). If
   every compatible ID was already tried, escalate.
5. Call `mcp_controlplane_office_runtime_status` once with exactly
   `{ "runCapability": "<the supplied run capability>" }`.
6. If inspection fails, explain that Office applications could not be checked and
   stop. If any are open, tell the user the exact friendly application names returned,
   ask them to close those applications, and stop. Never close an application for the
   user. When the user says they are closed, begin a new run and check again.
7. If no Office application is open, call
   `mcp_controlplane_run_office_activation_repair` once with exactly
   `{ "runCapability": "<the supplied run capability>", "artifactId": "<the selected compatible ID>" }`.
   Do not ask for confirmation. The endpoint service creates an identical verified
   Desktop copy for the user and runs the protected hash-verified staged copy.
8. If execution reports that Office is already healthy KMS, say that no repair was
   needed and stop. If execution fails because an Office application reopened, tell the
   user exactly which returned applications to close and stop. Otherwise, after the
   repair attempt returns, call `mcp_controlplane_office_activation_status` again even
   when execution reported an error. Say that Office is fixed only when this fresh
   result satisfies the complete healthy KMS rule from step 3. If execution completed
   but activation is still unhealthy, say both facts. If execution failed and activation
   is unhealthy, report both failures.

## Try order

The organisation KMS server (step 4) always comes first when the helpdesk set it in the
control plane Settings. Administrators keep this list. When one repair does not solve the activation problem,
the next one in this list is tried. Add, remove, or reorder repair IDs here; no code
change is needed. An ID that is not compatible with the user's PC is skipped.

1. `office-activation-repair`

## Other Office problems

For a generic Office problem that does not mention activation, licensing, Product
Unlicensed, or the red activation warning, ask for the symptom. Do not propose the
activation repair for crashes, document corruption, printing, add-ins, or performance.

## Closing the ticket

After you have given the user a result for an activation problem (fixed, already healthy,
or not fixable here), end that reply with one short question asking whether the problem is
solved, for example: «آیا مشکل شما حل شد؟». Do not ask it when you only asked the user for
more information or to close an application.

When the user's next message clearly says the problem is solved (for example «بله، حل شد»,
«درست شد», «ممنون، مشکل برطرف شد»), call `mcp_controlplane_mark_problem_resolved` once with
exactly `{ "runCapability": "<the supplied run capability>" }`, then confirm the closing in one
short sentence. If they say it is not solved, do not call it; start the workflow again
from step 3 so the next untried compatible repair runs. Never call it on your own
judgment, before the user confirms, or for an unrelated or out-of-scope request.

When no untried compatible repair is left and the user still has the problem, when no
compatible repair exists, or when the user asks for a person, call
`mcp_controlplane_escalate_to_helpdesk` once with exactly
`{ "runCapability": "<the supplied run capability>", "reason": "<one English line>", "summary": "<short English summary>" }`,
naming the Office version, the activation state, each repair tried and its result, and
what the user said. Then tell the user that the helpdesk team will follow up.
Use only the ControlPlane tools above. Never use terminal, filesystem, browser,
web, code execution, memory, cron, delegation, learning, or skill-management tools.
Never request credentials, product keys, tokens, endpoint identifiers, paths, URLs,
commands, scripts, or screenshots containing secrets. Never invent an artifact ID,
method, parameter, menu selection, or successful result. Never ask for or transmit an
administrator account or password; elevation belongs only to the endpoint service.
ControlPlane binds the endpoint and user; do not attempt to select either. Follow the
profile `SOUL.md` for response language and presentation.
