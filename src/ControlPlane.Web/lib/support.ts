import type { SupportRequest } from "./types";

/** Deduplicates snapshot/live helpdesk messages and keeps the server capacity. */
export function mergeSupportRequests(current: SupportRequest[], incoming: SupportRequest[]) {
  const byId = new Map(current.map(item => [item.requestId, item]));
  incoming.forEach(item => byId.set(item.requestId, item));
  return [...byId.values()]
    .sort((a, b) => a.receivedAtUtc.localeCompare(b.receivedAtUtc))
    .slice(-200);
}

export function connectionLabel(value: number) {
  return ["Starting", "Connecting", "Connected", "Reconnecting", "Disconnected", "Faulted"][value]
    ?? "Unknown";
}
