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
5. Otherwise call `mcp_controlplane_propose_endpoint_action` with method
   `system.windows_activation_bugfix_run` and exactly
   `{ "artifactId": "windows-activation-repair" }`.
6. Tell the user that the administrator-reviewed repair awaits confirmation. Do not
   claim it ran, succeeded, or activated Windows.

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
