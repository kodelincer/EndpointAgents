---
name: desktop-apps
description: Desktop and everyday app problems: taskbar or Start menu frozen, Persian keyboard missing, wrong date or calendar format, second monitor, no sound, camera or microphone not working in meetings, Outlook not connecting, Teams not loading, OneDrive not syncing, laptop battery. کیبورد فارسی ندارم، صدا ندارم، دوربین کار نمی‌کند، تسک‌بار گیر کرده، اوت‌لوک وصل نمی‌شود.
---

# Desktop and Everyday Apps

Use with the endpoint-actions skill. Call `note_skill_used` with `desktop-apps`.
Office activation belongs to office-support.

| Symptom | Look | Fix |
| --- | --- | --- |
| Taskbar, Start menu or desktop frozen | `explorer.status` | Offer `explorer.restart` (Disruptive: ask now or later). |
| Persian keyboard missing | `input.languages` | `input.add_layout` with `persian-standard` (or `persian` if the user asks for the old layout). |
| Date shows the wrong calendar or format | `region.status` | Explain where the user changes it; nothing to repair remotely. |
| Clock or time zone wrong | `time.status` | See signin-domain. |
| Second monitor not shown | `display.status` | Ask the user to check the cable and press Win+P; escalate if the display is not detected. |
| No sound, or the microphone is silent | `audio.status` | Muted or volume near zero: `audio.unmute`. Wrong device (headset not used): `audio.set_default` with its `id`. Otherwise `service.restart` with `Audiosrv`. |
| Camera or microphone not detected | `drivers.inventory` | Follow drivers-devices. |
| Camera or microphone blocked | `media.status` | `privacy.allow_camera_mic` with `camera` or `microphone`. |
| Outlook not connecting | `outlook.status` | If Outlook is offline, follow network-support; if a profile or OST problem, escalate. |
| Teams not loading or stuck | — | `teams.clear_cache` (closes Teams first if needed; say so). |
| OneDrive not syncing | `onedrive.status` | Escalate with the status if it shows an error. |
| Battery or power | `power.status` | Explain the result; escalate a battery health warning. |
| Browser shows old pages | `browser.inventory` | `browser.clear_cache` for that browser. |

Ask whether it works now after every fix, and escalate with the facts if not.
