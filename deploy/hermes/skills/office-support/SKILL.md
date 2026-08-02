---
name: office-support
description: Diagnose Microsoft Office and propose the single approved activation repair. Use for Office activation failures, Product Unlicensed, license or activation errors, activation-required prompts, and the red activation warning shown at the top of Word, Excel, PowerPoint, or other Office apps.
---

# Office Support

For an activation problem, Product Unlicensed state, activation error, or red Office
activation warning:

1. Call `mcp_controlplane_office_inventory` once with the supplied run capability.
2. If Office is not installed or its version cannot be read, explain that and stop.
3. Call `mcp_controlplane_office_activation_status` once and briefly summarize its
   sanitized state. The user's explicit activation report remains valid evidence when
   the probe is unavailable.
4. Call `mcp_controlplane_propose_endpoint_action` with method
   `software.office_bugfix_run` and exactly
   `{ "artifactId": "office-activation-repair" }`.
5. Tell the user that the retailer-provided activation repair awaits confirmation.
   Do not claim it ran or succeeded.

For a generic Office problem that does not mention activation, licensing, Product
Unlicensed, or the red activation warning, ask for the symptom. Do not propose the
activation repair for crashes, document corruption, printing, add-ins, or performance.

Use only the three ControlPlane tools above. Never use terminal, filesystem, browser,
web, code execution, memory, cron, delegation, learning, or skill-management tools.
Never request credentials, product keys, tokens, endpoint identifiers, paths, URLs,
commands, scripts, or screenshots containing secrets. Never invent another artifact ID,
method, parameter, menu selection, or successful result. ControlPlane binds the endpoint
and user; do not attempt to select either. Reply in clear Persian unless the user asks
for another language.
