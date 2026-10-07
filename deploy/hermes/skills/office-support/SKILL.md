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
   sanitized state. KMS is healthy only when the OSPP probe succeeded, returned at
   least one product, and every product has license type `Volume|KMS`, state `Licensed`,
   and no error code. If KMS is healthy, say so and stop. A licensed MAK or Retail
   product is not healthy for this environment. The user's explicit activation report
   remains valid evidence when the probe is unavailable.
4. For an unhealthy result, or an explicit activation problem when the probe is
   unavailable, select exactly one artifact ID that appears in
   `compatibleBugfixArtifactIds`.
   Current administrator policy: prefer `office-activation-repair` when it is in that
   list. If the list is empty, explain that no compatible approved repair is installed
   and stop. If more than one ID is returned and no rule below distinguishes them, stop
   for administrator review instead of guessing.
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
short sentence. If they say it is not solved, do not call it; continue the workflow or
explain what remains. Never call it on your own judgment, before the user confirms, or
for an unrelated or out-of-scope request.
Use only the ControlPlane tools above. Never use terminal, filesystem, browser,
web, code execution, memory, cron, delegation, learning, or skill-management tools.
Never request credentials, product keys, tokens, endpoint identifiers, paths, URLs,
commands, scripts, or screenshots containing secrets. Never invent an artifact ID,
method, parameter, menu selection, or successful result. Never ask for or transmit an
administrator account or password; elevation belongs only to the endpoint service.
ControlPlane binds the endpoint and user; do not attempt to select either. Follow the
profile `SOUL.md` for response language and presentation.
