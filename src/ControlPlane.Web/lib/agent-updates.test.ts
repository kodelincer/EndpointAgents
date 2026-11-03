import { describe, expect, it } from "vitest";
import { compareVersions, deviceState, summarize, targetFor, type AgentUpdateDevice, type AgentUpdateSettings } from "./agent-updates";

const settings: AgentUpdateSettings = {
  targetVersion: "0.2.0", pilotVersion: "0.3.0", pilotAgentIds: ["PILOT"], paused: false,
  windowStartHour: null, windowEndHour: null
};
const device = (agentId: string, currentVersion: string | null, extra: Partial<AgentUpdateDevice> = {}): AgentUpdateDevice =>
  ({ agentId, machineName: agentId, currentVersion, online: true, ...extra });

describe("agent updates", () => {
  it("compares versions by number, not text", () => {
    expect(compareVersions("0.10.0", "0.9.0")).toBeGreaterThan(0);
    expect(compareVersions("1.0", "1.0.0")).toBe(0);
    expect(compareVersions("0.2.0+abc", "0.3.0")).toBeLessThan(0);
  });

  it("gives pilot PCs the pilot version", () => {
    expect(targetFor(device("pilot", "0.1.0"), settings)).toBe("0.3.0");
    expect(targetFor(device("other", "0.1.0"), settings)).toBe("0.2.0");
  });

  it("names each PC's state from its last report", () => {
    expect(deviceState(device("other", "0.2.0"), settings)).toBe("up-to-date");
    expect(deviceState(device("other", "0.1.0"), settings)).toBe("waiting");
    expect(deviceState(device("other", "0.1.0", { lastStatus: "Installing", lastToVersion: "0.2.0" }), settings)).toBe("updating");
    expect(deviceState(device("PILOT", "0.2.0", { lastStatus: "RolledBack", lastToVersion: "0.3.0" }), settings)).toBe("rolled-back");
    expect(deviceState(device("x", null), settings)).toBe("unknown");
    expect(summarize([device("a", "0.2.0"), device("b", "0.1.0")], settings))
      .toMatchObject({ "up-to-date": 1, waiting: 1 });
  });
});
