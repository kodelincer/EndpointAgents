# Clock and date correction

A wrong clock or date breaks HTTPS, sign-in, Office and updates (typical after a dead clock
battery or a long power-off). It also happens to PCs in a domain. Any PC, in a domain or not,
can be corrected by the agent with one catalogued action; the source of time depends on the PC:

- **In a domain:** Active Directory. The PC syncs with its domain controller (Windows Time type
  `NT5DS`). If Group Policy sets the time source itself, that policy is left alone and the PC is only resynced.
- **Outside a domain:** the organisation NTP servers set in the dashboard (**Settings** -> Time servers):
  one or more host names or IPv4 addresses, comma-separated, up to 8, in the order they are tried.
  They can mix an internal server and internet names such as `time.windows.com`, `time.nist.gov`
  or `pool.ntp.org` (buttons add them). **Test from server** sends each one a time request (UDP 123) from
  the control-plane server and shows its answer and clock difference. The value is the
  environment setting `time.ntpServer` (`GET/PUT /api/settings/ntp`, `POST /api/settings/ntp/test`,
  admin only for changes). The time zone standard (`time.standardZone`) stays on the Environment page.

## Catalogued actions (area `domain`)

| Action | Tier | What it does |
| --- | --- | --- |
| `time.status` | Look | Local time, time zone, Windows Time service state, `ClockSource` (`domain`, `ntp` or `none`), clock offset against that source, whether NTP servers are set and the PC already uses them, `ClockOk` (under 2 minutes) |
| `time.resync` | SafeFix | Starts Windows Time if needed and runs `w32tm /resync /force` against the PC's current source |
| `time.correct` | SafeFix | Corrects the clock and date from the right source (above), even years off |
| `timezone.set_standard` | SafeFix | Sets the standard time zone; undoable (`timezone.restore`) |

`time.correct` answers `NotNeeded` when the PC already follows its source and is in sync, and
`Blocked` when no domain controller is found, none of the NTP servers answers from that PC, or a PC outside
a domain has no NTP server set. It is checked with `time.status` before and after and runs without a Tray card.

How it works, with no shell and no script:

1. One SNTP request from the PC measures the offset to the domain controller, or to the first
   NTP server that answers (read only).
2. Registry values under `HKLM\SYSTEM\CurrentControlSet\Services\W32Time`: a domain PC gets
   `Parameters\Type = NT5DS`; a PC outside a domain gets `Type = NTP`, `NtpServer = <host>,0x9 <host2>,0x9 ...`
   and `TimeProviders\NtpClient\Enabled = 1`.
3. The Windows Time service is restarted, then fixed `w32tm /config /update` and
   `w32tm /resync /rediscover` run.
4. When the clock is still off by 2 minutes or more (Windows refuses very large jumps by default),
   `MaxPosPhaseCorrection` and `MaxNegPhaseCorrection` are lifted for one more resync and put
   back to the Windows default (54000) afterwards, even if that resync fails.
5. The offset is measured again: `Done` only if it is under 2 minutes.

The server names come only from the control-plane setting, are validated on the server and again on
the endpoint (`NtpSettings.Parse`: plain host names or IPv4 addresses, so no port, option or command can
ride along), and never from the agent. They are written to the registry, never to a command line. This works
the same on Windows 10 and 11.

## Agent flow

The `signin-domain` skill runs `time.status` first. For a small drift it runs `time.resync`; when the
clock or date is still wrong, or the user asks to fix the time or date, it runs `time.correct` once and checks
`time.status` again. If the domain controller or the time servers do not answer, or none is set, it escalates
to the helpdesk with the facts.
