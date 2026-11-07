# Clock and date correction with the organisation time server

A wrong clock or date breaks HTTPS, sign-in, Office and updates (typical after a dead clock
battery or a long power-off). An admin sets the organisation NTP server once in the dashboard
(**Settings** → Time server: a host name or IPv4 address, no port). **Test from server** sends
one time request (UDP 123) from the control-plane server and shows how far its clock is from the
answer. The value is the environment setting `time.ntpServer` (`GET/PUT /api/settings/ntp`,
`POST /api/settings/ntp/test`, admin only for changes). The time zone standard (`time.standardZone`)
stays on the Environment page.

## Catalogued actions (area `domain`)

| Action | Tier | What it does |
| --- | --- | --- |
| `time.status` | Look | Local time, time zone, Windows Time service state, `ClockSource` (`domain`, `ntp` or `none`), the clock offset against that source, whether an NTP server is set and whether the PC already uses it, `ClockOk` (under 2 minutes) |
| `time.resync` | SafeFix | Starts Windows Time if needed and runs `w32tm /resync /force` against the PC's current source (domain or whatever is configured) |
| `time.set_ntp` | SafeFix | For a PC **outside a domain**: points Windows Time at the organisation NTP server and corrects the clock, even a long way off |
| `timezone.set_standard` | SafeFix | Sets the standard time zone; undoable (`timezone.restore`) |

`time.set_ntp` answers `NotNeeded` on a domain PC (it keeps taking time from the domain
controller; use `time.resync`), `NotNeeded` when the PC already follows the server and is in
sync, and `Blocked` when no server is set or the server does not answer from that PC. It is
checked with `time.status` before and after, and runs without a Tray card.

How it works, with no shell and no script:

1. One SNTP request from the PC measures the offset (read only).
2. Registry values `Parameters\Type = NTP`, `Parameters\NtpServer = <host>,0x9` and
   `TimeProviders\NtpClient\Enabled = 1` under `HKLM\SYSTEM\CurrentControlSet\Services\W32Time`.
3. The Windows Time service is restarted, then fixed `w32tm /config /update` and
   `w32tm /resync /rediscover` run.
4. When the clock is still off by 2 minutes or more (Windows refuses very large jumps by default),
   `MaxPosPhaseCorrection` and `MaxNegPhaseCorrection` are lifted for one more resync and put
   back to the Windows default (54000) afterwards, even if that resync fails.
5. The offset is measured again: `Done` only if it is under 2 minutes.

The host comes only from the control-plane setting, is validated on the server and again on
the endpoint (`NtpSettings.TryRead`: one host name or IPv4 address, so no port, option or second
server), and never from the agent. It is written to the registry, never to a command line. This
works the same on Windows 10 and 11.

## Agent flow

The `signin-domain` skill runs `time.status` first. When the clock is far off on a PC outside a
domain (or `time.resync` did not fix it) it runs `time.set_ntp` once and checks `time.status`
again. If the server is not set or does not answer, it escalates to the helpdesk with the facts.
