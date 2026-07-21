import type { Activity, Agent, ApiSystemStatus, CommandDefinition, FileInventory,
  MaintenanceModeResult, RpcRequest, RpcResponse, SupportRequest } from "./types";

export const CONTROL_PLANE_URL = process.env.NEXT_PUBLIC_CONTROL_PLANE_URL ?? "http://localhost:5052";

async function read<T>(path: string): Promise<T> {
  const response = await fetch(`${CONTROL_PLANE_URL}${path}`, { cache: "no-store" });
  if (!response.ok) throw new Error(`Control plane returned ${response.status}.`);
  return response.json() as Promise<T>;
}

/** Loads the REST snapshot used before and after SignalR connection changes. */
export async function getSnapshot() {
  const [agents, activity, commands, supportRequests] = await Promise.all([
    read<Agent[]>("/api/agents"), read<Activity[]>("/api/activity?limit=500"),
    read<CommandDefinition[]>("/api/commands"), read<SupportRequest[]>("/api/support-requests?limit=200")
  ]);
  return { agents, activity, commands, supportRequests };
}

/** Sends one reviewed JSON-RPC command to an online endpoint agent. */
export async function runCommand(agentId: string, request: RpcRequest): Promise<RpcResponse> {
  const response = await fetch(`${CONTROL_PLANE_URL}/api/agents/${encodeURIComponent(agentId)}/commands`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
  });
  const body = await response.json() as RpcResponse;
  if (!response.ok && !body.error) throw new Error(`Command failed with status ${response.status}.`);
  return body;
}

/** Reads the administrator-managed artifacts that endpoint agents may download. */
export function getFileInventory(): Promise<FileInventory> {
  return read<FileInventory>("/api/files");
}

/** Reads process health and operator-visible state directly from the unified API. */
export function getSystemStatus(): Promise<ApiSystemStatus> {
  return read<ApiSystemStatus>("/api/system/status");
}

/** Pauses or resumes new endpoint commands without stopping the API service. */
export async function setMaintenanceMode(enabled: boolean): Promise<MaintenanceModeResult> {
  const response = await fetch(`${CONTROL_PLANE_URL}/api/system/maintenance`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled })
  });
  if (!response.ok) throw new Error(`Maintenance update failed with status ${response.status}.`);
  return response.json() as Promise<MaintenanceModeResult>;
}
