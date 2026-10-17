---
name: windows-support
description: Diagnose managed Windows operating-system problems. Windows activation is the first automated repair; use for activation errors, unlicensed Windows, activation warnings, and Windows license problems.
---

# Windows Support

For a Windows activation, license, unlicensed, or activation-warning problem:

1. Call `mcp_controlplane_windows_activation_status` once with the supplied run capability.
2. Briefly summarize only the sanitized activation state. Never expose raw diagnostic data.
3. If the result is `Licensed`, explain that Windows reports as activated and stop.
4. If the probe failed, explain that activation could not be verified and stop.
5. Call `mcp_controlplane_list_repair_attempts` once with exactly
   `{ "runCapability": "<the supplied run capability>" }`. Choose the next repair with
   "Try order" below: the first listed ID that is in `compatibleBugfixArtifactIds` and is
   not among the attempts' `artifactId` values. If no listed ID qualifies, use the first
   other compatible ID that was not tried. If the compatible list is empty, or every
   compatible ID was already tried, escalate (see "Closing the ticket").
6. Call `mcp_controlplane_run_windows_activation_repair` once with exactly
   `{ "runCapability": "<the supplied run capability>", "artifactId": "<selected compatible ID>" }`.
   ControlPlane pins the BAT and playbook revisions and binds the action to the endpoint
   and user for this explicit activation request.
7. After the repair attempt returns, call `mcp_controlplane_windows_activation_status`
   again even when execution reported an error. Say that Windows is fixed only when this
   fresh result is `Licensed`. If execution completed but Windows remains unlicensed,
   report both facts. If execution failed and Windows remains unlicensed, report both
   failures.

## Try order

Administrators keep this list. When one repair does not solve the activation problem,
the next one in this list is tried. Add, remove, or reorder repair IDs here; no code
change is needed. An ID that is not compatible with the user's PC is skipped.

1. `windows-activation-retry`
2. `windows-activation-repair` (legacy fixed file; unavailable once a packaged repair exists)

## Other Windows problems

For another Windows problem, ask for the specific symptom when it is unclear. Call
`mcp_controlplane_endpoint_get_info` only when machine, version, network, proxy, or
time information is relevant. Do not propose the activation repair for updates,
drivers, crashes, networking, performance, files, printing, or application problems.
For those, follow the matching symptom skill (network-support, printing-support,
shared-drives, signin-domain, slow-pc-health, desktop-apps) and the endpoint-actions skill.

When the user explicitly asks to test the Windows support action flow or run the
harmless Hello terminal demo, propose `system.windows_hello_sample_run` with exactly
`{ "artifactId": "windows-hello-sample" }`. Explain that approval will copy the
reviewed sample BAT to their desktop and open a visible terminal. Never propose this
demo as a fix for an unrelated Windows problem.

## Closing the ticket

After you have given the user a result for an activation problem (fixed, already healthy,
or not fixable here), end that reply with one short question asking whether the problem is
solved, for example: «آیا مشکل شما حل شد؟». Do not ask it when you only asked the user for
more information or to close an application.

When the user's next message clearly says the problem is solved (for example «بله، حل شد»,
«درست شد», «ممنون، مشکل برطرف شد»), call `mcp_controlplane_mark_problem_resolved` once with
exactly `{ "runCapability": "<the supplied run capability>" }`, then confirm the closing in one
short sentence. If they say it is not solved, do not call it; start the activation
workflow again from step 1 so the next untried compatible repair runs. Never call it on
your own judgment, before the user confirms, or for an unrelated or out-of-scope request.

When no untried compatible repair is left and the user still has the problem, when no
compatible repair exists, or when the user asks for a person, call
`mcp_controlplane_escalate_to_helpdesk` once with exactly
`{ "runCapability": "<the supplied run capability>", "reason": "<one English line>", "summary": "<short English summary>" }`,
naming the Windows edition, the activation state, each repair tried and its result, and
what the user said. Then tell the user that the helpdesk team will follow up.

For a Windows problem other than activation, use the repair-packages skill when an
approved package lists a matching symptom.
Use only the ControlPlane tools above. Never use terminal, filesystem, browser, web,
code execution, memory, cron, delegation, learning, or skill-management tools. Never
request passwords, product keys, tokens, endpoint identifiers, paths, URLs, commands,
scripts, or secrets. Never help bypass licensing, install unauthorized keys, configure
unapproved KMS services, or circumvent activation. ControlPlane binds the endpoint and
user; do not attempt to select either. Follow the profile `SOUL.md` for response language
and presentation.
