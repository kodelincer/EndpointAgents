import { describe, expect, it } from "vitest";
import { age, factName, factRows, hubUrlFor, installCommand, minutesLeft } from "./inventory";

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

  it("builds the install line for a new PC", () => {
    expect(hubUrlFor("127.0.0.1")).toBe("https://SERVER-NAME:5052/agent-hub");
    expect(hubUrlFor("localhost")).toBe("https://SERVER-NAME:5052/agent-hub");
    expect(hubUrlFor("helpdesk.shirazmetro.local")).toBe("https://helpdesk.shirazmetro.local:5052/agent-hub");
    expect(installCommand("abc123", "https://hd:5052/agent-hub"))
      .toBe(".\\scripts\\install-endpoint.ps1 -ControlPlaneHubUrl 'https://hd:5052/agent-hub' -EnrollmentCode 'abc123'");
  });

  it("counts the minutes a code has left", () => {
    const now = new Date("2026-11-16T10:00:00Z");
    expect(minutesLeft("2026-11-16T10:30:00Z", now)).toBe(30);
    expect(minutesLeft("2026-11-16T10:00:20Z", now)).toBe(1);
    expect(minutesLeft("2026-11-16T09:00:00Z", now)).toBe(0);
  });
});
