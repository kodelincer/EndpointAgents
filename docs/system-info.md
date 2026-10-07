# System information (`system.get_info` / `endpoint_system_info`)

A read-only overview of the user's PC so the support agent can reason about it before
diagnosing. It runs automatically (no Tray approval) because it never changes anything.

| Section | What it reads |
| --- | --- |
| `network` | Connected adapters with an IP, plus unplugged physical Ethernet/Wi-Fi: IPv4 address, subnet mask, prefix, network, default gateway (and whether it is in the subnet), DNS servers, DNS suffix, DHCP on/off, DHCP server and lease times, WINS, MTU, speed, VPN/virtual hints; primary DNS suffix, suffix search list, firewall profiles; a summary (APIPA, gateway, DNS present). |
| `windows` | Product, edition, display version, build and UBR, install date, last boot and uptime, display language, time zone, pending restart (servicing, Windows Update, file rename). |
| `domain` | Domain / workgroup / unjoined (NetGetJoinInformation), joined name, primary DNS suffix, Entra ID join, and for domain PCs a bounded (8 s) domain-controller lookup (DsGetDcName). |
| `hardware` | Manufacturer, model, BIOS version and date, processor, logical processors, total/available memory and load. No serial numbers. |
| `storage` | Each fixed drive: format, total, free, free %, `LowSpace` (< 10 % or < 5 GB). |

The MCP tool takes an optional `sections` list; omitted means all. Unknown section names
are rejected by both the control plane and the endpoint. Each section (and each value
inside it) is collected independently: a failure becomes an entry in `Warnings` and the
rest of the report still returns.

Privacy: the tool result keeps IP addresses (that is its purpose) but still masks machine,
user and domain-account names, SIDs, MAC addresses and credential-looking text through
`ToolResultSanitizer`.

## Path

Hermes `endpoint_system_info` -> `ControlPlane.Api/Tools/EndpointSupportTools.cs` (validates
sections, resolves the run capability to one device) -> `AgentHub` -> Service ->
`SystemInfoCommandHandler` -> each `ISystemInfoSection` in
`src/EndpointAgents.Platform.Windows/SystemInfo`.

## Adding a new read-only fact

1. Add the section name to `SystemInfoContracts` (`src/EndpointAgents.Shared/Support`).
2. Add a class implementing `ISystemInfoSection` in `Platform.Windows/SystemInfo` and
   register it in `src/EndpointAgents.Service/Program.cs`.
3. Mention the section in `deploy/hermes/skills/system-info/SKILL.md` and the MCP tool
   description, add tests, then run `scripts\sync-hermes-profile.ps1`.

A brand-new endpoint capability (something that is not a section of this report) is its
own `IEndpointCommandHandler`, a `CommandCatalog` entry, an MCP tool in
`EndpointSupportTools`, the Hermes `tools.include` entry and a skill, the same way.
Anything that changes the PC must go through `propose_endpoint_action` and Tray consent.
