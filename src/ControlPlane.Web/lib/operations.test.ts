import { describe, expect, it } from "vitest";
import { buildLifecycle } from "./operations";
import type { ApiSystemStatus } from "./types";

function status(overrides: Partial<ApiSystemStatus> = {}): ApiSystemStatus {
  return {
    version: "1.0", machineName: "test", startedAtUtc: new Date().toISOString(), uptimeSeconds: 10,
    maintenanceMode: false, connectedAgents: 2, retainedActivity: 4,
    ...overrides
  };
}

describe("operations lifecycle", () => {
  it("shows a healthy command path", () => {
    const stages = buildLifecycle(status(), 2);
    expect(stages.map(stage => stage.value)).toEqual(["Running", "Accepting", "2 connected"]);
    expect(stages.every(stage => stage.state === "success")).toBe(true);
  });

  it("shows maintenance as a paused command flow while the service remains running", () => {
    const stages = buildLifecycle(status({ maintenanceMode: true }), 2);
    expect(stages[0]).toMatchObject({ value: "Running", state: "success" });
    expect(stages[1]).toMatchObject({ value: "Paused", state: "warning" });
  });

  it("shows a checking state before the first API status response", () => {
    const stages = buildLifecycle(undefined, 0);
    expect(stages.map(stage => stage.value)).toEqual(["Checking", "Checking", "0 connected"]);
    expect(stages.every(stage => stage.state === "warning")).toBe(true);
  });
});
