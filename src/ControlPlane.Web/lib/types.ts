export type Agent = { agentId: string; hostname: string; machineName: string; userName: string; version: string; connectedAtUtc: string };
export type CommandDefinition = { method: string; name: string; description: string; defaultParams: unknown };
export type RpcRequest = { jsonRpc: "2.0"; id: string; method: string; params: unknown };
export type RpcResponse = { jsonRpc: string; id: string; result: unknown; error?: { code: number; message: string; data?: unknown } | null };
export type Activity = {
  eventId: string; type: "agent.connected" | "agent.disconnected" | "command.started" | "command.completed";
  timestampUtc: string; agentId: string; status: "online" | "offline" | "pending" | "success" | "error";
  requestId?: string; request?: RpcRequest; response?: RpcResponse; durationMs?: number; agent?: Agent;
};
export type ConnectionState = "connecting" | "live" | "reconnecting" | "offline";

export type ApiSystemStatus = {
  version: string; machineName: string; startedAtUtc: string; uptimeSeconds: number;
  maintenanceMode: boolean; connectedAgents: number; retainedActivity: number;
};
export type ServiceAction = "start" | "stop" | "restart" | "enterMaintenance" | "exitMaintenance";
export type ManagedServiceState = "unknown" | "notInstalled" | "stopped" | "startPending" |
  "stopPending" | "running" | "continuePending" | "pausePending" | "paused";
export type ApiHealthState = "unknown" | "healthy" | "maintenance" | "degraded" | "unreachable";
export type ManagedServiceStatus = {
  serviceName: string; serviceState: ManagedServiceState; apiHealth: ApiHealthState;
  api?: ApiSystemStatus | null; pendingAction?: ServiceAction | null;
  availableActions: ServiceAction[]; checkedAtUtc: string; detail?: string | null;
};
export type ServiceActionResult = { accepted: boolean; message: string; status: ManagedServiceStatus };
export type ServiceActionState = { action: ServiceAction; state: "running" | "success" | "error"; message?: string };
