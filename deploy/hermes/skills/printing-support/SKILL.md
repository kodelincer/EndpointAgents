---
name: printing-support
description: Printer problems: cannot print, jobs stuck in the queue, printer offline or missing, wrong default printer, add a network printer, scanner not found. پرینتر چاپ نمی‌کند، چاپ در صف مانده، پرینتر را پیدا نمی‌کند، اسکنر کار نمی‌کند.
---

# Printing

Use with the endpoint-actions skill. Call `note_skill_used` with `printing-support`.

## Look

1. `printer.list`: installed printers, default, status, queued jobs. `matchesTarget`
   links a printer to the environment map.
2. If several users may be affected (an office printer), `check_known_incidents`.
3. For a printer with a problem, `printer.status` with its name: paper jam, out of paper,
   toner, door open or offline, as the printer reports it.
4. For a mapped printer that is offline, `probe_from_server` with its target id.

## Fix (one per reply, in this order as the facts fit)

| Fact | Action |
| --- | --- |
| `printer.status` shows paper, toner, jam or door | Tell the user what the printer reports; nothing to repair on the PC. |
| Jobs stuck or the spooler is stopped | `spooler.restart_and_clear` (SafeFix; clears every stuck job on this PC). |
| Wrong default printer | `printer.set_default` with the exact `printer` name from `printer.list`. |
| The office printer is missing | `printer.add` with its target id from `list_environment_targets` (kind `printer`). |
| A printer shows an error and was added before | `printer.remove` with its name, then `printer.add` with its target. |
| Printer up from the server but jobs still fail | `printer.test_page`, then escalate with the result. |
| Printer down from the server too | Escalate once; do not change the PC. |
| Scanner not found | `services.status` with `stisvc`; if stopped, `service.restart` `stisvc`. |

A missing or broken driver, or a scanner that is not recognised, belongs to the
drivers-devices skill (signed driver packages from the helpdesk catalog only).

## Close

Ask whether printing works now. If not and nothing untried fits, escalate with the
printer label, its status and what was tried.
