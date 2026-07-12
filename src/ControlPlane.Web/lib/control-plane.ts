import type { Activity, Agent, CommandDefinition, RpcRequest, RpcResponse } from "./types";

export const CONTROL_PLANE_URL = process.env.NEXT_PUBLIC_CONTROL_PLANE_URL ?? "http://localhost:5052";

async function read<T>(path: string): Promise<T> {
  const response = await fetch(`${CONTROL_PLANE_URL}${path}`, { cache: "no-store" });
  if (!response.ok) throw new Error(`Control plane returned ${response.status}.`);
  return response.json() as Promise<T>;
}

export async function getSnapshot() {
  const [agents, activity, commands] = await Promise.all([
    read<Agent[]>("/api/agents"), read<Activity[]>("/api/activity?limit=500"), read<CommandDefinition[]>("/api/commands")
  ]);
  return { agents, activity, commands };
}

export async function runCommand(agentId: string, request: RpcRequest): Promise<RpcResponse> {
  const response = await fetch(`${CONTROL_PLANE_URL}/api/agents/${encodeURIComponent(agentId)}/commands`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
  });
  const body = await response.json() as RpcResponse;
  if (!response.ok && !body.error) throw new Error(`Command failed with status ${response.status}.`);
  return body;
}
