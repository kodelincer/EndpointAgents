import { describe, expect, it } from "vitest";
import { buildLifecycle, serviceActionNeedsConfirmation } from "./operations";
import type { ManagedServiceStatus } from "./types";

function status(overrides: Partial<ManagedServiceStatus> = {}): ManagedServiceStatus {
  return {
    serviceName: "EndpointAgents.ControlPlane.Api", serviceState: "running", apiHealth: "healthy",
    api: { version: "1.0", machineName: "test", startedAtUtc: new Date().toISOString(), uptimeSeconds: 10,
      maintenanceMode: false, connectedAgents: 2, retainedActivity: 4 },
    availableActions: ["enterMaintenance", "stop", "restart"], checkedAtUtc: new Date().toISOString(),
    ...overrides
  };
}

describe("operations lifecycle", () => {
  it("shows a healthy command path", () => {
    const stages = buildLifecycle(status(), 2);
    expect(stages.map(stage => stage.value)).toEqual(["Running", "Healthy", "Accepting", "2 connected"]);
    expect(stages.every(stage => stage.state === "success")).toBe(true);
  });

  it("distinguishes maintenance from an outage", () => {
    const stages = buildLifecycle(status({ apiHealth: "maintenance",
      api: { ...status().api!, maintenanceMode: true } }), 2);
    expect(stages[1]).toMatchObject({ value: "Maintenance", state: "warning" });
    expect(stages[2]).toMatchObject({ value: "Paused", state: "warning" });
  });

  it("shows stopped and unreachable states as failures", () => {
    const stages = buildLifecycle(status({ serviceState: "stopped", apiHealth: "unreachable", api: null }), 0);
    expect(stages[0].state).toBe("error");
    expect(stages[1].value).toBe("Unreachable");
    expect(stages[2].value).toBe("Unavailable");
  });

  it("requires confirmation only for destructive lifecycle actions", () => {
    expect(serviceActionNeedsConfirmation("stop")).toBe(true);
    expect(serviceActionNeedsConfirmation("restart")).toBe(true);
    expect(serviceActionNeedsConfirmation("start")).toBe(false);
    expect(serviceActionNeedsConfirmation("enterMaintenance")).toBe(false);
  });
});
