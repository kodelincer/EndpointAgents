import type { Activity } from "./types";

export function mergeActivity(current: Activity[], incoming: Activity[]) {
  const byId = new Map(current.map(item => [item.eventId, item]));
  incoming.forEach(item => byId.set(item.eventId, item));
  return [...byId.values()]
    .sort((a, b) => a.timestampUtc.localeCompare(b.timestampUtc))
    .slice(-500);
}
