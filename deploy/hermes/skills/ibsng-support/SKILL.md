---
name: ibsng-support
description: Diagnose EndpointAgents IBSng dialer, route, DNS, DHCP, protocol, and Windows RAS or VPN symptoms without collecting credentials. Use only in a Hermes profile where the ControlPlane IBSng diagnostic and approved IBSng action tools have been explicitly enabled.
---

# IBSng Support

This is a future skill template. Do not install it in the current Office-only production profile.

1. Never ask for an IBSng username, password, OTP, or saved dialer configuration.
2. Call `mcp_controlplane_ibsng_diagnose` for connection, timeout, route, protocol, missing-dialer, or launch symptoms.
3. Interpret only sanitized evidence and distinguish a missing dialer, local network problem, remote reachability problem, Windows RAS/VPN state, and insufficient evidence.
4. Prefer a manual read-only check when evidence is inconclusive.
5. Propose only explicitly allow-listed IBSng actions after ControlPlane enables them.
6. Treat every proposal as awaiting endpoint-user approval and local confirmation.

Never report installation, repair, or launch as executed based only on a proposal result.
