import { describe, expect, it } from "vitest";
import { describeRule, parseMachines, reportOnlyDaysLeft, type HealthCheck } from "./health";

const check: HealthCheck = {
  id: "disk-space", name: "Low disk space on C:", nameFa: "", description: "", optionLabel: "Fail when free space is below",
  defaultOption: "10", options: [{ value: "10", label: "10 GB" }], fixes: [{ id: "disk.cleanup_safe", name: "Free disk space safely" }],
  canTellUser: true, notice: "Drive C: is almost full"
};

describe("health rules", () => {
  it("reads PC names from a typed list", () => {
    expect(parseMachines("pc-01, PC-02;pc-01\nhq-3")).toEqual(["PC-01", "PC-02", "HQ-3"]);
    expect(parseMachines("  ")).toEqual([]);
  });

  it("counts the days left in the report-only week", () => {
    const now = new Date("2026-11-03T00:00:00Z");
    expect(reportOnlyDaysLeft({ reportOnlyUntilUtc: "2026-11-10T00:00:00Z" }, now)).toBe(7);
    expect(reportOnlyDaysLeft({ reportOnlyUntilUtc: "2026-11-01T00:00:00Z" }, now)).toBe(0);
  });

  it("says what a rule does in one line", () => {
    expect(describeRule({ name: "d", checkId: "disk-space", option: "10", fixActionId: "disk.cleanup_safe",
      frequency: "daily", machines: [], onFailure: "fix" }, check))
      .toBe('Low disk space on C: (fail when free space is below 10 GB), daily on every PC; on failure it runs "Free disk space safely".');
  });
});
