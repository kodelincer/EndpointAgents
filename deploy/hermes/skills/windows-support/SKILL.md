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
5. Select one ID from `compatibleBugfixArtifactIds` in the status result. If the list
   is empty, explain that no compatible approved repair is available. If multiple IDs
   remain, stop for administrator selection rather than guessing.
6. Call `mcp_controlplane_run_windows_activation_repair` once with exactly
   `{ "runCapability": "<the supplied run capability>", "artifactId": "<selected compatible ID>" }`.
   ControlPlane pins the BAT and playbook revisions and binds the action to the endpoint
   and user for this explicit activation request.
7. After the repair attempt returns, call `mcp_controlplane_windows_activation_status`
   again even when execution reported an error. Say that Windows is fixed only when this
   fresh result is `Licensed`. If execution completed but Windows remains unlicensed,
   report both facts. If execution failed and Windows remains unlicensed, report both
   failures.

For another Windows problem, ask for the specific symptom when it is unclear. Call
`mcp_controlplane_endpoint_get_info` only when machine, version, network, proxy, or
time information is relevant. Do not propose the activation repair for updates,
drivers, crashes, networking, performance, files, printing, or application problems.

When the user explicitly asks to test the Windows support action flow or run the
harmless Hello terminal demo, propose `system.windows_hello_sample_run` with exactly
`{ "artifactId": "windows-hello-sample" }`. Explain that approval will copy the
reviewed sample BAT to their desktop and open a visible terminal. Never propose this
demo as a fix for an unrelated Windows problem.

Use only the ControlPlane tools above. Never use terminal, filesystem, browser, web,
code execution, memory, cron, delegation, learning, or skill-management tools. Never
request passwords, product keys, tokens, endpoint identifiers, paths, URLs, commands,
scripts, or secrets. Never help bypass licensing, install unauthorized keys, configure
unapproved KMS services, or circumvent activation. ControlPlane binds the endpoint and
user; do not attempt to select either. Follow the profile `SOUL.md` for response language
and presentation.
