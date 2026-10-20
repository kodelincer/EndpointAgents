---
name: slow-pc-health
description: Slow or frozen PC, programs crash or hang, disk full, blue screens, unexpected restarts, Windows Update stuck, antivirus or virus worries, a device (USB, Bluetooth, webcam) not detected, "it worked yesterday", Windows files damaged, BitLocker recovery screen, laptop battery drains fast, too many programs at startup. سیستم کند است، هنگ می‌کند، برنامه بسته می‌شود، فضای دیسک پر است.
---

# Slow PC and Health

Use with the endpoint-actions skill. Call `note_skill_used` with `slow-pc-health`.

## Look (choose by symptom)

- Slow or frozen: `perf.snapshot` (CPU, memory, disk use, top processes, uptime).
- "It worked yesterday": `system.recent_changes` with `days` 3 (updates, installs, drivers).
- Crashes or blue screens: `events.summary` with `area` `crashes` or `bluescreen`.
- Disk full: `disk.usage` (free space and what fills it).
- "Restart needed" messages or many days without a restart: `restart.pending`.
- Updates: `windows_update.status`. Security worry: `security.status`.
- A device not detected: follow drivers-devices.
- Slow start after sign-in: `startup.list`.
- Antivirus definitions or found threats: `defender.threats`.
- Disk noises, errors or a warning about the disk: `disk.health`.
- Windows features or built-in apps broken, odd errors after a crash: `system.repair_status`
  (shows whether a file check already ran and its result).
- Encryption questions: `bitlocker.status` (never a key).
- Laptop battery drains fast or dies early: `battery.report`, and `power.status` for the plan.
- A program missing: `apps.find` with a short `name`; to install it, follow software-install.

## Fix

| Fact | Action |
| --- | --- |
| `disk.usage` shows the Downloads folder or recycle bin is large | Tell the user which one; only they decide what to delete. |
| Uptime of many days or a pending restart | Offer `reboot.schedule` (Disruptive: ask now or later). |
| Low free space on the system drive | `disk.cleanup_safe` (temp files and the update cache only). |
| One Office app hangs | Offer `app.close` for it (Disruptive: ask first; unsaved work may be lost). |
| Search or indexing heavy | `service.restart` with `WSearch`. |
| Update service stopped or stuck | `service.restart` with `wuauserv`, then `windows_update.status`. |
| Device has a problem code | Follow drivers-devices. |
| Defender off or out of date, or virus worry | `defender.quick_scan` (starts in the background); escalate if `security.status` shows a threat. |
| Blue screens or disk errors | Escalate with the summary; do not try repairs. |
| Updates fail again and again after the service restart | `windows_update.reset_components`, then `windows_update.scan_now`. |
| Updates simply not checked for days | `windows_update.scan_now`. |
| Definitions out of date | `defender.update_signatures`. |
| `defender.threats` shows a threat not resolved | Escalate at once with the threat name; do not let the user keep working on it. |
| `disk.health` AnyWarning | Tell the user to save their work, then escalate (the disk may fail). |
| Windows files damaged or `system.repair_status` says CouldNotRepair | `system.image_repair` first; when its result is ready, `system.file_check`. Both run in the background; tell the user it takes about half an hour and they can keep working. |
| Many startup apps the user does not need | Ask which ones, then `startup.disable` with each Name (SafeFix, undoable). Apps that start for every user are not changed. |
| Recycle bin is large and the user agrees nothing in it is needed | Offer `recyclebin.empty` (Disruptive: it cannot be undone, ask first). |
| Battery WearPercent above 40 | Tell the user the battery is worn; escalate for a replacement. |
| Power plan differs from the standard | `power.set_plan_standard` (SafeFix, undoable by the helpdesk). |
| BitLocker recovery screen at start | `request_helpdesk_action` with kind `bitlocker-key`; tell the user a helpdesk member will give the key in person or by phone. Never ask them to read or type a key in the chat. |

## Close

Tell the user what was done and ask whether the PC works better. Escalate with the facts
if not.
