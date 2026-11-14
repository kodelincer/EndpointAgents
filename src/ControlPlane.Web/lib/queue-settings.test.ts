import { describe, expect, it } from "vitest";
import { queueSettingsErrors, waitedFor } from "./queue-settings";

describe("queue settings", () => {
  it("accepts whole numbers inside each range", () => {
    expect(queueSettingsErrors({ maxParallelRuns: "4", maxQueueLength: "50", maxWaitMinutes: "10" })).toEqual({});
  });

  it("flags empty, fractional and out-of-range values", () => {
    const errors = queueSettingsErrors({ maxParallelRuns: "0", maxQueueLength: "2.5", maxWaitMinutes: "" });
    expect(Object.keys(errors).sort()).toEqual(["maxParallelRuns", "maxQueueLength", "maxWaitMinutes"]);
    expect(queueSettingsErrors({ maxParallelRuns: "33", maxQueueLength: "1000", maxWaitMinutes: "240" }))
      .toEqual({ maxParallelRuns: "Enter a whole number from 1 to 32." });
  });

  it("describes the oldest wait", () => {
    const now = Date.parse("2026-10-14T10:00:00Z");
    expect(waitedFor(null, now)).toBe("");
    expect(waitedFor("2026-10-14T09:59:30Z", now)).toBe("Oldest request has waited under a minute.");
    expect(waitedFor("2026-10-14T09:53:00Z", now)).toBe("Oldest request has waited 7 min.");
  });
});
