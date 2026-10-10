---
name: repair-packages
description: Fix a user's PC problem with an administrator-approved repair package. Use for any Windows, application, printer, network, or device problem that is not Windows or Office activation and not IBSng, when a catalog package lists a matching symptom.
---

# Repair Packages

Administrators publish reviewed repair packages. Each one lists the symptoms it fixes and
belongs to a `problem`; packages with the same `problem` are alternatives, listed in the
order to try them. The package's file, arguments, and every answer to its screens are
fixed by the administrator; you only decide which problem matches the user's request.

1. If the problem is unclear, ask one short question about the symptom first.
2. Call `mcp_controlplane_find_repair_packages` once with
   `{ "runCapability": "<the supplied run capability>" }`.
3. Compare the user's description with each package's `symptoms`, `displayName`, and
   `description`. Choose a `problem` only when its packages' symptoms clearly describe
   the same problem. Never guess and never pick the "closest" package for an unrelated
   problem. If none match, or two different problems match equally, go to step 7.
4. If "Problem guides" below has an entry for the chosen problem, follow its list: take
   the first listed package that `find_repair_packages` returned and whose `alreadyTried`
   is false. Otherwise take the first package of that problem in the listed order whose
   `alreadyTried` is false. If every package for the problem was already tried, go to
   step 7.
5. Tell the user in one short sentence what you are about to fix, then call
   `mcp_controlplane_run_repair_package` once with exactly
   `{ "runCapability": "<the supplied run capability>", "packageId": "<chosen packageId>" }`.
   The package runs without an approval prompt because the administrator already
   approved it. Run at most one package per reply.
6. Report the result plainly and end with one short question asking whether the problem
   is solved, for example «آیا مشکل شما حل شد؟»:
   - `ok: true` with `restartRequired: true`: the fix finished and the PC needs a
     restart; ask the user to restart and then say whether it is solved. Never restart
     it yourself.
   - `ok: true`: the fix finished.
   - `ok: false`: the automatic fix did not finish.
   When the user answers yes, call `mcp_controlplane_mark_problem_resolved` once with
   exactly `{ "runCapability": "<the supplied run capability>" }` and confirm in one short
   sentence. When the user answers no, or the run failed, start again at step 2 for the
   same problem: the next untried package is the next fallback.
7. When no package fits, or every package for the problem has been tried and the user
   still has the problem, call `mcp_controlplane_escalate_to_helpdesk` once with exactly
   `{ "runCapability": "<the supplied run capability>", "reason": "<one English line>", "summary": "<short English summary>" }`.
   The summary names the problem, the packages tried and their results, and what the
   user said. Then tell the user that the helpdesk team will follow up. Also escalate
   when the user asks for a person.

## Problem guides

Administrators keep this section. Each entry names a problem and the packages to try for
it, first to last; when one does not solve the problem, the next is tried. Add, remove,
or reorder entries here; no code change is needed. A listed package that is not
published or not returned by `find_repair_packages` is skipped. Without an entry, the
package's own `problem` and `order` decide.

- `printing-stuck` (printing is stuck, jobs stay in the queue):
  1. `no-input-repair-sample` (simulation for testing; replace with real packages)

Use only the ControlPlane tools above. Never use terminal, filesystem, browser, web,
code execution, memory, cron, delegation, learning, or skill-management tools. Never
request passwords, product keys, tokens, endpoint identifiers, paths, URLs, commands,
scripts, or secrets, and never describe a package's arguments or menu answers.
ControlPlane binds the endpoint and user; do not attempt to select either. Follow the
profile `SOUL.md` for response language and presentation.
