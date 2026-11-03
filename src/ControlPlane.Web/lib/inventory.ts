/** The Devices page: facts kept from earlier reads ("keep, don't schedule"). */
import { apiFetch } from "./control-plane";

export type StoredDevice = { agentId: string; machineName: string; online: boolean; version?: string | null; lastReadUtc: string; facts: number };
export type StoredFact = { key: string; readAtUtc: string; fresh: boolean; keptForHours: number; value: unknown };

const factNames: Record<string, string> = {
  "system:windows": "Windows", "system:domain": "Domain", "system:hardware": "Hardware", "system:storage": "Disks",
  "action:software.inventory": "Installed programs", "action:drivers.inventory": "Drivers",
  "action:disk.health": "Disk health", "action:bitlocker.status": "BitLocker"
};

export const factName = (key: string) => factNames[key] ?? key;

/** "3 hours ago", "2 days ago". */
export function age(iso: string, now = new Date()) {
  const minutes = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 60000));
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  return hours < 48 ? `${hours} hours ago` : `${Math.round(hours / 24)} days ago`;
}

function label(key: string) {
  return key.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, first => first.toUpperCase());
}

/** Flattens a stored answer to label/value rows; lists show how many and the first few names. */
export function factRows(value: unknown, prefix = "", depth = 0): { label: string; value: string }[] {
  if (value === null || value === undefined) return [];
  if (typeof value !== "object") return [{ label: prefix || "Value", value: String(value) }];
  if (Array.isArray(value)) {
    const names = value.slice(0, 5).map(item => typeof item === "object" && item
      ? String((item as Record<string, unknown>).name ?? (item as Record<string, unknown>).displayName
        ?? (item as Record<string, unknown>).drive ?? (item as Record<string, unknown>).letter ?? Object.values(item)[0] ?? "")
      : String(item)).filter(Boolean);
    return [{ label: prefix || "Items", value: `${value.length}${names.length ? `: ${names.join(", ")}${value.length > 5 ? ", …" : ""}` : ""}` }];
  }
  return Object.entries(value as Record<string, unknown>).flatMap(([key, item]) => {
    const name = prefix ? `${prefix} · ${label(key)}` : label(key);
    return depth >= 1 && item && typeof item === "object" && !Array.isArray(item)
      ? [{ label: name, value: JSON.stringify(item).slice(0, 120) }]
      : factRows(item, name, depth + 1);
  });
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await apiFetch(path, init);
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { message?: string };
    throw new Error(error.message ?? `Control plane returned ${response.status}.`);
  }
  return response.json() as Promise<T>;
}

export const getDevices = () => call<StoredDevice[]>("/api/inventory");
export const getDeviceFacts = (agentId: string) => call<StoredFact[]>(`/api/inventory/${encodeURIComponent(agentId)}`);
export const refreshDevice = (agentId: string) =>
  call<{ refreshed: boolean; problems: string[] }>(`/api/inventory/${encodeURIComponent(agentId)}/refresh`, { method: "POST" });
