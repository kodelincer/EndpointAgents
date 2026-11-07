---
name: drivers-devices
description: A printer, scanner, webcam, card reader, signature pad, barcode reader, Bluetooth or other device does not work, shows a yellow mark, is "not recognised", or needs a driver installed. درایور نصب نیست، دستگاه را نمی‌شناسد، اسکنر یا دوربین کار نمی‌کند، درایور پرینتر.
---

# Drivers and Devices

Use with the endpoint-actions skill. Call `note_skill_used` with `drivers-devices`.
A printer that is installed but does not print belongs to printing-support.

The helpdesk keeps a driver catalog: the device models the organisation supports, each
with its hardware IDs and the signed driver package that installs it. You never choose a
driver, a file or a download; you only match the user's device to the catalog.

## Look

1. `drivers.inventory` with `{ "scope": "problems" }`. If the device the user means is not
   listed, run it again with `{ "scope": "all" }`.
2. Find the device the user means by its `name` and `class`. If it is unclear which one,
   ask one short question naming the two or three likely devices.

## Decide

| Fact | Next step |
| --- | --- |
| `problem` says the device is turned off (code 22) | `device.enable` with its `instanceId`. |
| The device has a problem and `catalogMatch.packageId` is set | Install its driver: `run_repair_package` with that `packageId` and a short `reason`. |
| No driver, or the wrong driver, and `catalogMatch` is null | The model is not in the catalog: say so, then follow the feature-requests skill so the helpdesk can add it. Escalate instead if the user cannot work without it. |
| The device has a driver but stopped answering (webcam, card reader, Bluetooth, dock) | Offer `device.restart` (Disruptive: ask now or later). |
| The device is not listed at all | Ask the user to unplug it, plug it into another port and wait a few seconds, then `devices.rescan` and `drivers.inventory` again. Still missing: escalate (cable or hardware). |
| `device.enable` or `device.restart` returns Blocked | Offer `reboot.schedule` (Disruptive) or escalate. |
| Disks, display adapters, keyboards, mice, USB controllers | Never change them; escalate with the facts. |

A driver package runs once per conversation. If the package result is `ok: false`, or the
device still has a problem after it, escalate with the device name, model label and
problem.

## Close

After a fix, ask whether the device works now. If not and nothing untried fits, escalate
with the device name, its `problem` and what was tried. Never offer to download a driver
from the internet or from the manufacturer's site.
