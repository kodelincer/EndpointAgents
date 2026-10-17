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
- Disk full: `endpoint_system_info` with `["storage"]`.
- Updates: `windows_update.status`. Security worry: `security.status`.
- A device not detected: `devices.problems`.
- A program missing: `apps.find` with a short `name`.

## Fix

| Fact | Action |
| --- | --- |
| Uptime of many days or a pending restart | Offer `reboot.schedule` (Disruptive: ask now or later). |
| Low free space on the system drive | `disk.cleanup_safe` (temp files and the update cache only). |
| One Office app hangs | Offer `app.close` for it (Disruptive: ask first; unsaved work may be lost). |
| Search or indexing heavy | `service.restart` with `WSearch`. |
| Update service stopped or stuck | `service.restart` with `wuauserv`, then `windows_update.status`. |
| Device has a problem code | `devices.rescan`; if still failing, escalate (driver). |
| Defender off or out of date, or virus worry | `defender.quick_scan` (starts in the background); escalate if `security.status` shows a threat. |
| Blue screens or disk errors | Escalate with the summary; do not try repairs. |

## Close

Tell the user what was done and ask whether the PC works better. Escalate with the facts
if not.
