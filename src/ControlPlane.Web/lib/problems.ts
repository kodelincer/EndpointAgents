import { apiFetch } from "./control-plane";

export interface CountedValue { label: string; pcs: number; }

export interface ProblemRow {
  fingerprint: string; kind: string; source: string; title: string; exceptionType?: string | null;
  status: "Open" | "Fixed"; fixedInVersion?: string | null; fixedBy?: string | null; fixedAtUtc?: string | null;
  reopenedAtUtc?: string | null; conversationId?: string | null; firstSeenUtc: string; lastSeenUtc: string;
  count: number; pcs: number; versions: string[]; windows: CountedValue[]; models: CountedValue[]; onlyOn: string[];
}

export interface LogServerStatus {
  enabled: boolean; down: boolean; lastSuccessUtc?: string | null; lastErrorUtc?: string | null;
  lastError?: string | null; waiting: number; dropped: number; sent: number;
}

export interface ProblemPage {
  problems: ProblemRow[];
  filters: { kinds: string[]; versions: string[]; windows: string[]; models: string[] };
  storage: { databaseBytes: number; diskLow: boolean; droppedByLimit: number; droppedDiskFull: number };
  logServer: LogServerStatus;
}

export interface ProblemOccurrence {
  agentId: string; machineName: string; agentVersion?: string | null; osName?: string | null; osEdition?: string | null;
  osVersion?: string | null; osBuild?: string | null; architecture?: string | null; manufacturer?: string | null;
  model?: string | null; count: number; firstSeenUtc: string; lastSeenUtc: string;
}

export interface ProblemDetail {
  problem: { stack?: string | null };
  occurrences: ProblemOccurrence[];
  developerText: string;
}

export interface DeviceFacts {
  osName?: string | null; osVersion?: string | null; manufacturer?: string | null; model?: string | null;
}

export interface FleetPc {
  agentId: string; machineName: string; online: boolean; version?: string | null;
  lastContactUtc?: string | null; facts?: DeviceFacts | null;
}

export interface LogBundle {
  bundleId: string; agentId: string; machineName: string; requestedBy: string; requestedAtUtc: string;
  status: "Requested" | "Ready" | "Failed"; sizeBytes: number; completedAtUtc?: string | null; message?: string | null;
}

export interface FleetPage { quiet: FleetPc[]; oldVersion: FleetPc[]; all: FleetPc[]; bundles: LogBundle[]; }

export interface ProblemFilter {
  status?: string; kind?: string; version?: string; pc?: string; os?: string; model?: string;
}

export type LogServerType = "Splunk" | "Graylog" | "Syslog";

export interface LogServerSettings {
  enabled: boolean; type: LogServerType; host: string; port: number; protocol: string;
  index?: string | null; certificateThumbprint?: string | null; updatedBy?: string | null; updatedAtUtc?: string | null;
}

export interface LogServerState { settings: LogServerSettings; hasToken: boolean; status: LogServerStatus; }

async function request<T>(path: string, method = "GET", body?: unknown): Promise<T> {
  const response = await apiFetch(path, {
    method, headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { message?: string };
    throw new Error(error.message ?? (response.status === 403
      ? "Your role cannot do this." : `Control plane returned ${response.status}.`));
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

export function problemQuery(filter: ProblemFilter): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(filter)) if (value) query.set(key, value);
  const text = query.toString();
  return text ? `?${text}` : "";
}

export const getProblems = (filter: ProblemFilter) => request<ProblemPage>(`/api/problems${problemQuery(filter)}`);
export const getProblem = (fingerprint: string) => request<ProblemDetail>(`/api/problems/${encodeURIComponent(fingerprint)}`);
export const markFixed = (fingerprint: string, version: string) =>
  request<void>(`/api/problems/${encodeURIComponent(fingerprint)}/fixed`, "POST", { version });
export const reopenProblem = (fingerprint: string) =>
  request<void>(`/api/problems/${encodeURIComponent(fingerprint)}/reopen`, "POST");
export const getFleet = () => request<FleetPage>("/api/problems/pcs");
export const requestLogs = (agentId: string) =>
  request<LogBundle>(`/api/problems/pcs/${encodeURIComponent(agentId)}/logs`, "POST");

export async function downloadLogs(bundle: LogBundle): Promise<void> {
  const response = await apiFetch(`/api/problems/logs/${bundle.bundleId}/download`);
  if (!response.ok) throw new Error("Those logs are no longer kept.");
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement("a");
  link.href = url;
  link.download = `logs-${bundle.machineName}.zip`;
  link.click();
  URL.revokeObjectURL(url);
}

export const getLogServer = () => request<LogServerState>("/api/settings/log-server");
export const saveLogServer = (settings: LogServerSettings, token: string) =>
  request<LogServerState>("/api/settings/log-server", "PUT", { ...settings, token: token || null });
export const testLogServer = (settings: LogServerSettings, token: string) =>
  request<{ ok: boolean; message: string }>("/api/settings/log-server/test", "POST", { ...settings, token: token || null });

export const kindLabels: Record<string, string> = {
  Crash: "Crash", LoggedError: "Logged error", OperationFailed: "Failed operation", PackageFailed: "Failed package",
  HealthCheckFailed: "Health check did not run", UpdateRolledBack: "Update rolled back",
  InstallFailed: "Install failed", UserReported: "User: did not fix it"
};

export const kindLabel = (kind: string) => kindLabels[kind] ?? kind;

/** Crashes and failed installs stand out most; a user's word is a question to look at. */
export const kindClass = (kind: string) =>
  kind === "Crash" || kind === "InstallFailed" || kind === "UpdateRolledBack" ? "escalated"
    : kind === "UserReported" ? "new" : "open";

export type GroupBy = "none" | "kind" | "version" | "windows" | "model";

/** Sections for the list. A problem on two versions or models shows under each of them. */
export function groupProblems(rows: ProblemRow[], by: GroupBy): { title: string; rows: ProblemRow[] }[] {
  if (by === "none") return rows.length ? [{ title: "", rows }] : [];
  const keys = (row: ProblemRow): string[] => {
    const values = by === "kind" ? [kindLabel(row.kind)]
      : by === "version" ? row.versions
        : (by === "windows" ? row.windows : row.models).map(item => item.label);
    return values.length ? values : ["Not known"];
  };
  const groups = new Map<string, ProblemRow[]>();
  for (const row of rows) for (const key of keys(row)) groups.set(key, [...(groups.get(key) ?? []), row]);
  return [...groups.entries()]
    .map(([title, grouped]) => ({ title, rows: grouped }))
    .sort((a, b) => b.rows.length - a.rows.length || a.title.localeCompare(b.title));
}

export const protocolsFor = (type: LogServerType): string[] =>
  type === "Syslog" ? ["udp", "tcp", "tls"] : ["https", "http"];

/** The usual port and protocol when the admin switches type. */
export const defaultsFor = (type: LogServerType): Pick<LogServerSettings, "port" | "protocol"> =>
  type === "Splunk" ? { port: 8088, protocol: "https" }
    : type === "Graylog" ? { port: 12201, protocol: "http" }
      : { port: 6514, protocol: "tls" };

export const isUnencrypted = (settings: Pick<LogServerSettings, "protocol">) =>
  settings.protocol === "http" || settings.protocol === "udp" || settings.protocol === "tcp";

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

export const pcFacts = (pc: FleetPc) => [
  [pc.facts?.osName, pc.facts?.osVersion].filter(Boolean).join(" "),
  [pc.facts?.manufacturer, pc.facts?.model].filter(Boolean).join(" ")
].filter(Boolean).join(" · ");
