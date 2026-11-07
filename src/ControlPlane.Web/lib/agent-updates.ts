import { apiFetch } from "./control-plane";

export interface AgentUpdateSettings {
  targetVersion: string | null; pilotVersion: string | null; pilotAgentIds: string[];
  paused: boolean; windowStartHour: number | null; windowEndHour: number | null;
  updatedBy?: string | null; updatedAtUtc?: string | null;
}

export interface AgentUpdatePackage {
  version: string; status: "Ready" | "Not signed" | "Hash mismatch" | "Invalid"; problem?: string | null;
  addedAtUtc?: string | null; releaseNotes?: string | null; minWindowsBuild?: number | null;
  sizeBytes?: number | null; signedBy?: string | null;
}

export interface AgentUpdateDevice {
  agentId: string; machineName: string; currentVersion?: string | null; online: boolean;
  lastCheckUtc?: string | null; lastStatus?: string | null; lastFromVersion?: string | null;
  lastToVersion?: string | null; lastMessage?: string | null; lastReportUtc?: string | null;
}

export interface AgentUpdateOverview {
  settings: AgentUpdateSettings; packages: AgentUpdatePackage[]; devices: AgentUpdateDevice[];
}

async function request<T>(path: string, method = "GET", body?: unknown): Promise<T> {
  const response = await apiFetch(path, {
    method, headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { message?: string };
    throw new Error(error.message ?? (response.status === 403
      ? "Only admins can manage agent updates." : `Control plane returned ${response.status}.`));
  }
  return response.json() as Promise<T>;
}

export const getAgentUpdates = () => request<AgentUpdateOverview>("/api/agent-updates");

export const saveAgentUpdateSettings = (settings: AgentUpdateSettings) =>
  request<AgentUpdateSettings>("/api/agent-updates/settings", "PUT", {
    targetVersion: settings.targetVersion || null, pilotVersion: settings.pilotVersion || null,
    pilotAgentIds: settings.pilotAgentIds, paused: settings.paused,
    windowStartHour: settings.windowStartHour, windowEndHour: settings.windowEndHour
  });

/** Numeric comparison of "1.2.3" versions; missing parts count as 0. */
export function compareVersions(a: string, b: string): number {
  const parts = (value: string) => value.split(/[-+]/)[0].split(".").map(part => Number.parseInt(part, 10) || 0);
  const left = parts(a), right = parts(b);
  for (let index = 0; index < Math.max(left.length, right.length); index++) {
    const difference = (left[index] ?? 0) - (right[index] ?? 0);
    if (difference !== 0) return difference;
  }
  return 0;
}

/** The version a PC should end up on with these settings (pilot PCs get the pilot version). */
export function targetFor(device: AgentUpdateDevice, settings: AgentUpdateSettings): string | null {
  const pilot = settings.pilotAgentIds.some(id => id.toLowerCase() === device.agentId.toLowerCase());
  return (pilot && settings.pilotVersion) ? settings.pilotVersion : settings.targetVersion;
}

export type DeviceUpdateState = "up-to-date" | "waiting" | "updating" | "rolled-back" | "failed" | "unknown";

/** One word for the device table, from what the PC last reported. */
export function deviceState(device: AgentUpdateDevice, settings: AgentUpdateSettings): DeviceUpdateState {
  const target = targetFor(device, settings);
  if (device.lastStatus === "RolledBack" && device.lastToVersion && device.lastToVersion === target) return "rolled-back";
  if (device.lastStatus === "Failed" && device.lastToVersion && device.lastToVersion === target) return "failed";
  if (!device.currentVersion) return "unknown";
  if (!target || compareVersions(device.currentVersion, target) >= 0) return "up-to-date";
  if (device.lastStatus === "Downloading" || device.lastStatus === "Installing") return "updating";
  return "waiting";
}

/** How many PCs are in each state, for the summary line. */
export function summarize(devices: AgentUpdateDevice[], settings: AgentUpdateSettings) {
  const counts: Record<DeviceUpdateState, number> =
    { "up-to-date": 0, waiting: 0, updating: 0, "rolled-back": 0, failed: 0, unknown: 0 };
  for (const device of devices) counts[deviceState(device, settings)]++;
  return counts;
}
