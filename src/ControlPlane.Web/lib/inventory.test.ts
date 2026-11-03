import { describe, expect, it } from "vitest";
import { age, factName, factRows } from "./inventory";

describe("devices page", () => {
  it("names facts and their age", () => {
    expect(factName("action:software.inventory")).toBe("Installed programs");
    const now = new Date("2026-11-03T12:00:00Z");
    expect(age("2026-11-03T11:30:00Z", now)).toBe("30 min ago");
    expect(age("2026-11-01T12:00:00Z", now)).toBe("48 hours ago".replace("48 hours", "2 days"));
  });

  it("flattens a stored answer to rows", () => {
    expect(factRows({ manufacturer: "HP", memoryGb: 16, programs: [{ name: "7-Zip" }, { name: "Chrome" }] })).toEqual([
      { label: "Manufacturer", value: "HP" }, { label: "Memory Gb", value: "16" },
      { label: "Programs", value: "2: 7-Zip, Chrome" }
    ]);
  });
});
