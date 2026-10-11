import type { FeatureRequest } from "./types";

export const featureStatuses = ["New", "Planned", "Done", "Declined"] as const;

/** How many requests are in each status. */
export function featureRequestCounts(items: FeatureRequest[]) {
  const counts: Record<FeatureRequest["status"], number> = { New: 0, Planned: 0, Done: 0, Declined: 0 };
  for (const item of items) counts[item.status] = (counts[item.status] ?? 0) + 1;
  return counts;
}
