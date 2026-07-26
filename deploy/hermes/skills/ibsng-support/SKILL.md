---
name: ibsng-support
description: Diagnose EndpointAgents IBSng dialer, route, DNS, DHCP, protocol, and Windows RAS/VPN symptoms without collecting credentials. Use for IBSng login, connection, dialer installation, launch, timeout, DNS, gateway, or VPN prompts.
---

# IBSng Support

1. Never ask for an IBSng username, password, OTP, or saved dialer configuration.
2. Call `mcp_controlplane_ibsng_diagnose` for connection, timeout, route, protocol, missing-dialer, or launch symptoms.
3. Interpret only returned sanitized evidence. Distinguish:
   - missing or unverified approved dialer;
   - local gateway, DHCP, DNS, or clock issue;
   - IBSng server route or reachability issue;
   - Windows RAS/VPN state;
   - insufficient evidence.
4. Prefer a manual read-only next check when diagnosis is inconclusive.
5. Propose `network.ibsng_ensure_dialer` only when evidence indicates the approved dialer is missing or invalid.
6. Propose `network.ibsng_launch` only when the approved dialer is present and launching it is the appropriate next step.

Both actions require the endpoint user's action-card approval. Installation or repair also requires endpoint-local confirmation. Never report either as executed from the proposal result.
