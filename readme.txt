
Shared = contracts used by both endpoint and server
Core = endpoint business rules and abstractions
Platform.Windows = real Windows implementation
Service = background host that wires everything together
Tray = user interface near Windows clock
ControlPlane.Api = server-side REST and SignalR backend running as a Windows service
ControlPlane.Web = independent Next.js dashboard running on loopback port 3000


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
	Endpoint information and internet checks
	IBSng diagnostics, artifact verification, repair, and launch commands
	Interactive Windows user-session discovery
	Printer diagnostics
	Windows services
	Registry checks
	Installed software checks
	VPN/dialer checks

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
		status-aware tray icons for ready, paused, stopped, warning, and error states
		right-click status and log menu
		double-click Helpdesk, Status & Controls, and Logs form
		Persian/RTL support request editing and submission
		administrator-approved start, stop, pause-functions, and resume controls
		Windows Event Log viewing and export
		secured named-pipe command listener
		launch the approved IBSng dialer in the interactive user session

	Rule:
		Tray app executes only narrow user-session actions requested by the service.
		The background service owns the control-plane connection and runtime state.
		It never accepts credentials from the control-plane command channel.

- Helpdesk requests:
	Tray -> EndpointAgents.Service local IPC -> AgentHub -> ControlPlane.Web /support
	Requests are live and in-memory on the control plane; Windows endpoint logs remain in Event Log.





HubConnection
= client-side SignalR connection

Hub
= server-side receiver for client calls and connection events

IHubContext
= server-side sender usable outside the Hub

Connection registry
= your own storage for agent identity and online state

Endpoint agent calls server
    → AgentHub

Control plane sends command to endpoint
    → IHubContext<AgentHub>

Control plane finds which agent is connected
    → AgentConnectionRegistry

