import type { ApiSystemStatus } from "./types";

export type LifecycleStage = { label: string; value: string; state: "success" | "warning" | "error" };

/** Converts unified API state into the operator-visible runtime stages. */
export function buildLifecycle(status: ApiSystemStatus | undefined, agentCount: number): LifecycleStage[] {
  return [
    { label: "API service", value: status ? "Running" : "Checking",
      state: status ? "success" : "warning" },
    { label: "Command flow", value: status?.maintenanceMode ? "Paused" : status ? "Accepting" : "Checking",
      state: status?.maintenanceMode ? "warning" : status ? "success" : "warning" },
    { label: "Agent fabric", value: `${agentCount} connected`,
      state: status ? "success" : "warning" }
  ];
}
