---
name: signin-domain
description: Sign-in and domain problems: password expired or account locked, "trust relationship failed", clock or time is wrong, group policy not applied, keeps asking for a password, Outlook or shares ask for credentials. رمز عبور، حساب قفل شده، ساعت اشتباه است، مدام رمز می‌خواهد.
---

# Sign-in, Domain and Time

Use with the endpoint-actions skill. Call `note_skill_used` with `signin-domain`.
Never ask for or accept a password or a recovery key. People-only work (unlock, password
reset, administrator rights, domain rejoin) goes through `request_helpdesk_action`.

## Look

1. `check_user_account`: disabled, locked out, password expired or must change. If it is
   not set up, continue with the PC checks.
2. `domain.health`: domain membership, domain controller found, secure channel.
3. `time.status`: clock offset from the domain controller and time zone.

## Decide

| Fact | Next step |
| --- | --- |
| Account locked | `request_helpdesk_action` with kind `unlock-account`. Tell the user the helpdesk was asked and the answer will appear here. |
| Account disabled | Escalate; only the helpdesk decides whether to turn it on. |
| Password expired or must change | Tell the user to press Ctrl+Alt+Del and choose "Change a password". If that fails or they forgot it, `request_helpdesk_action` with kind `password-reset`. |
| An approved install needs administrator rights | `request_helpdesk_action` with kind `local-admin` and the program's name. |
| Clock off by more than 2 minutes | `time.resync` (SafeFix). |
| Wrong time zone | `timezone.set_standard` (SafeFix, undoable by the helpdesk). |
| No domain controller found | Follow network-support (`network.status`); do not touch the domain settings. |
| Secure channel broken ("trust relationship") | `request_helpdesk_action` with kind `trust-repair`; a domain administrator rejoins the PC. |
| No access after being added to a group | `whoami.groups`; if the group is missing, ask the user to sign out and back in. |
| Policy missing (drives, printers, settings) | `gpo.refresh_computer`, then `gpo.refresh_user`. |
| Keeps asking for a password | `credman.list`; remove stale entries for internal servers with `credman.remove`, then `kerberos.purge`. |
| Several users cannot sign in | `recent_reports_from_others`, then escalate once. |

## Close

Ask whether sign-in works now. Escalate with the facts if not.
