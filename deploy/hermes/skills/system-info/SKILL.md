---
name: system-info
description: Read the user's PC configuration before diagnosing. Use for network, Ethernet, Wi-Fi, IP address, DNS, DHCP, gateway, "no internet", "can't reach the server or shared folder", domain or workgroup membership, domain login problems, Windows version, uptime or pending restart, slow PC, memory, and low disk space questions.
---

# System Information

`mcp_controlplane_endpoint_system_info` returns read-only facts about the PC bound to
this run. It never changes anything, so it may run automatically.

Call it once with `{ "runCapability": "<the supplied run capability>" }` and an optional
`sections` list. Ask only for what the problem needs:

| Problem | sections |
| --- | --- |
| Network, internet, Ethernet, Wi-Fi, IP, DNS, DHCP, gateway, shared folder | `["network"]` (add `"domain"` for domain resources) |
| Domain login, "trust relationship", group policy, domain or workgroup question | `["domain", "network"]` |
| Windows version, restart, update | `["windows"]` |
| Slow PC, memory | `["hardware", "storage", "windows"]` |
| Disk full | `["storage"]` |
| General "what is this PC" question | omit `sections` |

Read the result like this:

- `network.Summary` first. `HasApipaAddress` true (169.254.x.x) means DHCP did not
  answer. `HasDefaultGateway` false means the PC cannot leave its local network.
  `HasDnsServers` false means names will not resolve.
- Per adapter: `Connected` false on a physical Ethernet adapter usually means an
  unplugged or faulty cable or switch port. `DhcpEnabled` false means a static
  (manual) address. `GatewayInSubnet` false means the gateway does not match the IP and
  subnet mask, so the static settings are wrong. Compare `DnsServers` across adapters:
  a VPN or dialer adapter can override the office DNS.
- `domain.DomainJoined` with `DomainController.Found` false means the PC cannot reach a
  domain controller right now (network or DNS problem, not a password problem).
  `JoinType` `Workgroup` means the PC is not in the domain at all.
- `windows.PendingRestart.Required` true: suggest restarting before deeper repairs.
- `storage[].LowSpace` true: the drive is nearly full. `hardware.Memory.LoadPercent`
  above 90 explains slowness.
- `Warnings` lists facts that could not be read; never guess them.

For internet reachability or proxy checks, also call `mcp_controlplane_endpoint_get_info`.

This tool only reads. To act on what it shows (renew the address, clear the DNS cache,
sync the clock, refresh policy), follow the network-support or signin-domain skill and
the endpoint-actions skill. Static IP, adapter and domain-membership settings are never
changed: explain the likely cause in one sentence and tell the user what to check (for
example the cable) or that IT support must change the setting.

IP addresses are usually masked in the result (the flags above still tell you APIPA,
gateway and DNS state). The user may hear their own IP address, gateway or DNS server
values only when they ask for them and the result shows them. Never reveal computer names, domain controller names, adapter IDs, or the
raw result. Use only the ControlPlane tools above, never request credentials, and follow
the profile `SOUL.md` for language and length.
