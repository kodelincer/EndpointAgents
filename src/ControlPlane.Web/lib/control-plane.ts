import type { Activity, Agent, ApiSystemStatus, CommandDefinition, FileInventory,
  MaintenanceModeResult, PackageInventory, RpcRequest, RpcResponse, SupportRequest } from "./types";
import type { FeatureRequest, HelpdeskConversationDetail, HelpdeskConversationSummary } from "./types";

/** The API runs on port 5052 of whatever host served the dashboard. Keeping the host name
 * identical makes the dashboard and API the same site, so the HttpOnly sign-in cookie
 * (SameSite=Strict) travels with every call. */
export const CONTROL_PLANE_URL = process.env.NEXT_PUBLIC_CONTROL_PLANE_URL
  ?? (typeof window === "undefined" ? "http://localhost:5052"
    : `${window.location.protocol}//${window.location.hostname}:5052`);

/** Raised when the API says the operator is no longer signed in. */
export const SIGNED_OUT_EVENT = "operator-signed-out";

/** Every API call sends the sign-in cookie and reports an expired session once. */
export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const response = await fetch(`${CONTROL_PLANE_URL}${path}`,
    { cache: "no-store", ...init, credentials: "include" });
  if (response.status === 401 && typeof window !== "undefined" && !path.startsWith("/api/auth/"))
    window.dispatchEvent(new Event(SIGNED_OUT_EVENT));
  return response;
}

async function read<T>(path: string): Promise<T> {
  const response = await apiFetch(path);
  if (!response.ok) throw new Error(response.status === 403
    ? "Your role does not allow this." : `Control plane returned ${response.status}.`);
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
  const response = await apiFetch(`/api/agents/${encodeURIComponent(agentId)}/commands`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
  });
  if (response.status === 401 || response.status === 403)
    throw new Error(response.status === 403 ? "Only admins can run endpoint commands." : "Sign in again.");
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
  const response = await apiFetch(`/api/system/maintenance`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled })
  });
  if (!response.ok) throw new Error(response.status === 403
    ? "Only admins can pause or resume commands." : `Maintenance update failed with status ${response.status}.`);
  return response.json() as Promise<MaintenanceModeResult>;
}

async function write<T>(path: string, method: "POST" | "PATCH", body: unknown): Promise<T> {
  const response = await apiFetch(path, {
    method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { message?: string };
    throw new Error(error.message ?? (response.status === 403
      ? "Your role does not allow this." : `Control plane returned ${response.status}.`));
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

/** Requests for abilities the support agent does not have yet, newest first. */
export function getFeatureRequests(): Promise<FeatureRequest[]> {
  return read<FeatureRequest[]>("/api/feature-requests?limit=500");
}

export function setFeatureRequestStatus(requestId: string, status: FeatureRequest["status"]): Promise<void> {
  return write(`/api/feature-requests/${encodeURIComponent(requestId)}`, "PATCH", { status });
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
