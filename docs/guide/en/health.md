---
title: Health rules
summary: Checks that run by themselves on every online PC, daily or weekly, so common problems are fixed before the user calls. You pick from fixed lists; no scripts and no AI.
order: 10
---

# Health rules

A **health rule** checks one thing on the PCs on its own, every day or every week, and can fix it or tell the user. It is for problems that are easy to see early: a full disk, a PC that was not restarted for two weeks, old antivirus definitions, a wrong clock.

Nothing here uses the AI, and nobody writes code. The control plane runs the same catalogued actions the agent uses, with the same kill switches, hourly limits and before/after checks.

## The five answers

Open **Health rules** in the menu and press **New rule** (admins only).

1. **Check**: one item from the fixed list below, with its setting (for example "below 10 GB").
2. **When it fails**: *Only report*, *Fix it by itself* (only the fixes listed for that check), or *Tell the user in the Tray* (a fixed Persian message next to the clock).
3. **Fix**: which fix, when you chose to fix.
4. **How often**: daily or weekly. Each PC gets its own random minute, so they are not all checked at once.
5. **Which PCs**: leave empty for every PC, or type PC names for a pilot.

**Every new rule only reports for its first 7 days.** In that week the page shows how many PCs *would* be fixed or told, so you can see the effect before anything changes.

## The checks

| Check | Setting | Fix it can run | Message to the user |
| --- | --- | --- | --- |
| Low disk space on C: | 5, 10 or 20 GB | Free disk space safely | Drive C: is almost full |
| Not restarted for days | 3, 7 or 14 days | (none) | Please restart the PC |
| Clock out of sync | | Sync the clock | |
| Wrong time zone | | Set the standard time zone | |
| Old antivirus definitions | 3, 7 or 14 days | Update antivirus definitions | |
| Service stopped | one service from the list | Restart the service | |
| Disk may fail | | (none) | The disk may fail; call the helpdesk |

A PC that is offline is checked when it comes online. A PC where the check could not run (old agent, Tray closed for a user message, action switched off) shows as **not checked**.

## Reading the page

Each rule shows how many PCs **pass**, were **fixed**, still **fail**, or were **not checked**, and when it last ran. **Show PCs** lists each PC with what was found and what was done. **Pause** stops a rule without losing its results.

The list of checks grows only when the development team builds a new fixed check; ask for one on the Ideas page.
