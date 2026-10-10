---
name: repair-packages
description: Fix a user's PC problem with an administrator-approved repair package. Use for any Windows, application, printer, network, or device problem that is not Windows or Office activation and not IBSng, when a catalog package lists a matching symptom.
---

# Repair Packages

Administrators publish reviewed repair packages. Each one lists the symptoms it fixes.
The package's file, arguments, and every answer to its screens are fixed by the
administrator; you only decide whether one package matches the user's problem.

1. If the problem is unclear, ask one short question about the symptom first.
2. Call `mcp_controlplane_find_repair_packages` once with
   `{ "runCapability": "<the supplied run capability>" }`.
3. Compare the user's description with each package's `symptoms`, `displayName`, and
   `description`. Choose a package only when its symptoms clearly describe the same
   problem. If none match, or if two different packages match equally, say that no
   approved automatic fix is available and suggest contacting the help desk. Never
   guess and never pick the "closest" package for an unrelated problem.
4. Tell the user in one short sentence what you are about to fix, then call
   `mcp_controlplane_run_repair_package` once with exactly
   `{ "runCapability": "<the supplied run capability>", "packageId": "<chosen packageId>" }`.
   The package runs without an approval prompt because the administrator already
   approved it.
5. Report the result plainly:
   - `ok: true` with `restartRequired: true`: the fix finished and the PC needs a
     restart; ask the user to restart when convenient. Never restart it yourself.
   - `ok: true`: the fix finished. Ask the user to check whether the problem is gone.
   - `ok: false`: say the automatic fix did not finish and suggest contacting the help
     desk. Do not run the same package again, and do not try a different package for
     the same problem in this conversation.

Use only the ControlPlane tools above. Never use terminal, filesystem, browser, web,
code execution, memory, cron, delegation, learning, or skill-management tools. Never
request passwords, product keys, tokens, endpoint identifiers, paths, URLs, commands,
scripts, or secrets, and never describe a package's arguments or menu answers.
ControlPlane binds the endpoint and user; do not attempt to select either. Follow the
profile `SOUL.md` for response language and presentation.
