import type { ManagedServiceStatus, ServiceAction } from "./types";

export type LifecycleStage = { label: string; value: string; state: "success" | "warning" | "error" };

function readable(value?: string) {
  if (!value) return "Unknown";
  return value.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, character => character.toUpperCase());
}

/** Converts service/API contracts into the four operator-visible lifecycle stages. */
export function buildLifecycle(status: ManagedServiceStatus | undefined, agentCount: number): LifecycleStage[] {
  return [
    { label: "Windows service", value: readable(status?.serviceState),
      state: status?.serviceState === "running" ? "success" : status?.serviceState === "stopped" ? "error" : "warning" },
    { label: "API health", value: readable(status?.apiHealth),
      state: status?.apiHealth === "healthy" ? "success" : status?.apiHealth === "maintenance" ? "warning" : "error" },
    { label: "Command flow",
      value: status?.api?.maintenanceMode ? "Paused" : status?.apiHealth === "healthy" ? "Accepting" : "Unavailable",
      state: status?.api?.maintenanceMode ? "warning" : status?.apiHealth === "healthy" ? "success" : "error" },
    { label: "Agent fabric", value: `${agentCount} connected`,
      state: status?.apiHealth === "healthy" || status?.apiHealth === "maintenance" ? "success" : "error" }
  ];
}

/** Identifies destructive actions that require an explicit second click. */
export function serviceActionNeedsConfirmation(action: ServiceAction) {
  return action === "stop" || action === "restart";
}
