import type { Activity, Agent, ApiSystemStatus, CommandDefinition, FileInventory,
  MaintenanceModeResult, PackageInventory, RpcRequest, RpcResponse, SupportRequest } from "./types";
import type { HelpdeskConversationDetail, HelpdeskConversationSummary } from "./types";

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

/** Reads every BAT package folder with its validation status. */
export function getPackageInventory(): Promise<PackageInventory> {
  return read<PackageInventory>("/api/files/packages");
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

async function write<T>(path: string, method: "POST" | "PATCH", body: unknown): Promise<T> {
  const response = await fetch(`${CONTROL_PLANE_URL}${path}`, {
    method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { message?: string };
    throw new Error(error.message ?? `Control plane returned ${response.status}.`);
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

export function getHelpdeskConversations(): Promise<HelpdeskConversationSummary[]> {
  return read<HelpdeskConversationSummary[]>("/api/helpdesk/conversations?limit=200");
}

export function getHelpdeskConversation(conversationId: string): Promise<HelpdeskConversationDetail> {
  return read<HelpdeskConversationDetail>(`/api/helpdesk/conversations/${encodeURIComponent(conversationId)}`);
}

export function patchHelpdeskConversation(conversationId: string,
  patch: { status?: string; operatorTakeover?: boolean; readThroughSequence?: number }): Promise<void> {
  return write(`/api/helpdesk/conversations/${encodeURIComponent(conversationId)}`, "PATCH", patch);
}

export function sendHelpdeskReply(conversationId: string, messageText: string,
  textDirection: "ltr" | "rtl"): Promise<unknown> {
  return write(`/api/helpdesk/conversations/${encodeURIComponent(conversationId)}/messages`, "POST", {
    clientMessageId: crypto.randomUUID().replaceAll("-", ""), messageText, textDirection
  });
}

export function runHelpdeskDiagnostic(conversationId: string, method: string): Promise<RpcResponse> {
  const parameters = method === "network.ibsng_diagnose"
    ? { serverHost: "", protocol: "Auto", includeDialerLog: true } : null;
  return write(`/api/helpdesk/conversations/${encodeURIComponent(conversationId)}/diagnostics`,
    "POST", { method, parameters });
}
