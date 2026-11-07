---
name: desktop-apps
description: Desktop and everyday app problems: taskbar or Start menu frozen, Persian keyboard missing, wrong date or calendar format, second monitor, no sound, camera or microphone not working in meetings, Outlook not connecting or crashing, Word, Excel or Outlook crash at start or are very slow, laptop battery. ورد یا اکسل باز نمی‌شود، اوت‌لوک بسته می‌شود. کیبورد فارسی ندارم، صدا ندارم، دوربین کار نمی‌کند، تسک‌بار گیر کرده، اوت‌لوک وصل نمی‌شود.
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
| Outlook not connecting | `outlook.status` | If Outlook is offline, follow network-support; if its offline mailbox file is near its limit or damaged and the mail server is reachable, offer `outlook.rebuild_ost` with the file name (Disruptive: Outlook must be closed and mail downloads again). Otherwise escalate. |
| Word, Excel, PowerPoint or Outlook crash at start or are very slow | `office.addins_list` with the app | An add-in installed by the user loads at start: `office.addin_disable` with its Id (SafeFix, undoable), then ask the user to open the app again. If nothing changes, offer `office.quick_repair` (Disruptive: every Office app closes, ask first). |
| Battery or power | `power.status`, `battery.report` | Explain the result; escalate a battery health warning. |
| Browser shows old pages | `browser.inventory` | `browser.clear_cache` for that browser. |

Ask whether it works now after every fix, and escalate with the facts if not.
