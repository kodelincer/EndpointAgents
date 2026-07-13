import type { Activity, Agent, CommandDefinition, ManagedServiceStatus, RpcRequest, RpcResponse,
  ServiceAction, ServiceActionResult } from "./types";

export const CONTROL_PLANE_URL = process.env.NEXT_PUBLIC_CONTROL_PLANE_URL ?? "http://localhost:5052";
export const MANAGEMENT_URL = process.env.NEXT_PUBLIC_MANAGEMENT_URL ??
  (process.env.NODE_ENV === "development" ? "http://127.0.0.1:5053" : "");

async function read<T>(path: string): Promise<T> {
  const response = await fetch(`${CONTROL_PLANE_URL}${path}`, { cache: "no-store" });
  if (!response.ok) throw new Error(`Control plane returned ${response.status}.`);
  return response.json() as Promise<T>;
}

/** Loads the REST snapshot used before and after SignalR connection changes. */
export async function getSnapshot() {
  const [agents, activity, commands] = await Promise.all([
    read<Agent[]>("/api/agents"), read<Activity[]>("/api/activity?limit=500"), read<CommandDefinition[]>("/api/commands")
  ]);
  return { agents, activity, commands };
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

/** Reads Windows service and API health from the independent local supervisor. */
export async function getManagedServiceStatus(): Promise<ManagedServiceStatus> {
  const response = await fetch(`${MANAGEMENT_URL}/api/management/control-plane`, { cache: "no-store" });
  if (!response.ok) throw new Error(`Management service returned ${response.status}.`);
  return response.json() as Promise<ManagedServiceStatus>;
}

/** Requests one serialized lifecycle action from the local supervisor. */
export async function runServiceAction(action: ServiceAction): Promise<ServiceActionResult> {
  const route: Record<ServiceAction, string> = {
    start: "start", stop: "stop", restart: "restart",
    enterMaintenance: "enter-maintenance", exitMaintenance: "exit-maintenance"
  };
  const response = await fetch(`${MANAGEMENT_URL}/api/management/control-plane/${route[action]}`, {
    method: "POST", headers: { "Content-Type": "application/json" }
  });
  const result = await response.json() as ServiceActionResult;
  if (!response.ok && !result.message) throw new Error(`Service action failed with ${response.status}.`);
  return result;
}
