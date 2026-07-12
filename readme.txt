
Shared = contracts used by both endpoint and server
Core = endpoint business rules and abstractions
Platform.Windows = real Windows implementation
Service = background host that wires everything together
Tray = user interface near Windows clock
ControlPlane.Api = server side


Notes:

- Shared: EndpointAgents.Shared contains contracts that are used by both sides:
		RpcRequest
		RpcResponse
		RpcError
		AgentRegistration
		Command names
		DTOs
	Rule:
		Put something in Shared only when both the endpoint and server must underst and it.
		Do not put Windows code in Shared.

- Core: EndpointAgents.Core contains the main endpoint agent rules:

		IEndpointCommandHandler
		CommandDispatcher
		Command execution context
		Command result pattern
		Validation abstractions
		Authorization abstractions later

	Rule:
		Put something in Core when it is part of the endpoint agent engine and should be reused by all features.

- Platform.Windows: EndpointAgents.Platform.Windows contains real Windows-specific features:
  Teammates usually add their functionality here.
	Network diagnostics
	Printer diagnostics
	Windows services
	Registry checks
	Installed software checks
	VPN/dialer checks
	Office checks

- Service: EndpointAgents.Service is the Windows background service.
	It handles:
		start with Windows
		connect to control plane
		reconnect
		receive RPC command
		call CommandDispatcher
		return result
	Rule:
		Teammates should almost never edit this project.

- Tray: EndpointAgents.Tray is only the visible tray icon app.
	It handles:
		tray icon
		right-click menu
		double-click support form
		show status
		send user problem text

	Rule:
		Tray app should not execute powerful admin commands directly.
		Later, tray talks to the background service by Named Pipe or localhost IPC.