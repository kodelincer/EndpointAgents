---
name: shared-drives
description: Shared folder or network drive problems: a drive letter is missing or has a red X, "network path not found", access denied on a share, files on the server cannot be opened, user profile or desktop looks empty. درایو شبکه نیست، پوشه اشتراکی باز نمی‌شود، دسترسی ندارم.
---

# Shared Drives and Folders

Use with the endpoint-actions skill. Call `note_skill_used` with `shared-drives`.

## Look

1. `drives.list`: mapped drives, whether connected, and `missingStandardDrives` (drives the
   organisation expects that are absent).
2. `share.test` with the share's target id: name lookup, port 445, and whether the user
   can list it (access denied is reported separately).
3. If the share fails, `probe_from_server` with the same id; `check_known_incidents`.
4. Empty desktop or documents, or "temporary profile": `profile.status`.

## Fix

| Fact | Action |
| --- | --- |
| A standard drive is missing or disconnected, share reachable | `drive.remap` with the share's target id. |
| Share not reachable from the PC, reachable from the server | Follow network-support (`network.triage` with the share id). |
| Share down from the server too | Escalate once. |
| Access denied | `credman.list`; if an old saved password for that server exists (`matchesTarget`), `credman.remove` with its `ref`, then `kerberos.purge`, then `share.test` again. If still denied, escalate: permissions are set by the helpdesk. |
| Temporary profile | Escalate immediately; do not change anything. |

## Close

Ask whether the drive works now. If not, escalate with the share label and the facts.
