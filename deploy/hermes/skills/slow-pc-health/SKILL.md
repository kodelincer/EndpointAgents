---
name: slow-pc-health
description: Slow or frozen PC, programs crash or hang, disk full, blue screens, unexpected restarts, Windows Update stuck, antivirus or virus worries, a device (USB, Bluetooth, webcam) not detected, "it worked yesterday". سیستم کند است، هنگ می‌کند، برنامه بسته می‌شود، فضای دیسک پر است.
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

## Close

Tell the user what was done and ask whether the PC works better. Escalate with the facts
if not.
