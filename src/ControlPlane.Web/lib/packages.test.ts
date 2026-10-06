import { describe, expect, it } from "vitest";
import { describeStep, finishRules, isPublished, packageCompatibility, packageStatus } from "./packages";
import type { BatPackage } from "./types";

const ready: BatPackage = {
  id: "menu-repair", folder: "C:\\FileRepository\\packages\\menu-repair", status: "Ready",
  reasons: [], warnings: [], kind: "office", supportFiles: [],
  office: { minOfficeVersion: "16.0", maxOfficeVersionExclusive: "17.0",
    officeProductReleaseIds: ["ProPlus2021Volume"] }
};

describe("package presentation", () => {
  it("labels statuses with a tone", () => {
    expect(packageStatus("Ready")).toEqual({ label: "Ready", tone: "ready" });
    expect(packageStatus("ReadyWithWarnings").tone).toBe("warning");
    expect(packageStatus("HashMismatch")).toEqual({ label: "Hash mismatch", tone: "blocked" });
  });

  it("publishes only ready packages", () => {
    expect(isPublished(ready)).toBe(true);
    expect(isPublished({ ...ready, status: "ReadyWithWarnings" })).toBe(true);
    expect(isPublished({ ...ready, status: "InvalidPlaybook" })).toBe(false);
  });

  it("describes steps in plain language", () => {
    expect(describeStep({ expect: "Select an option [1-6]:", send: "1", submit: "line",
      timeoutSeconds: 60 }, 0)).toBe(
      '1. when the screen shows "Select an option [1-6]:", type "1" and press Enter (waits up to 60 s).');
    expect(describeStep({ expect: "Press any key", send: "x", submit: "key", timeoutSeconds: 30,
      optional: true, maxRepeats: 3,
      alternatives: [{ expect: "Continue? [Y/N]", send: "Y", submit: "key" }] }, 3)).toBe(
      '4. when the screen shows "Press any key", press the key "x"; or when the screen shows '
      + '"Continue? [Y/N]", press the key "Y" (skipped if this screen does not appear, '
      + "repeated up to 3 times, waits up to 30 s).");
  });

  it("lists the finish rules with defaults", () => {
    expect(finishRules({ version: 1, steps: [] }, 15)).toEqual([
      "Exit code must be 0.", "A window left open for 15 s stops the run."]);
    expect(finishRules({ version: 2, steps: [], successExitCodes: [0, 3010],
      successMarker: "Done", failureMarkers: ["FAILED"], guiDialogGraceSeconds: 60 }, 15)).toEqual([
      "Exit code must be 0 or 3010.", 'Output must contain "Done".',
      'Stops as failed if the output shows "FAILED".', "A window left open for 60 s stops the run."]);
  });

  it("summarizes compatibility", () => {
    expect(packageCompatibility(ready)).toBe("Office 16.0 to < 17.0 · products ProPlus2021Volume");
    expect(packageCompatibility({ ...ready, office: null, windows: {
      minWindowsVersion: "10.0.19041", maxWindowsVersionExclusive: "10.0.30000" } }))
      .toBe("Windows 10.0.19041 to < 10.0.30000 · all editions");
    expect(packageCompatibility({ ...ready, office: null })).toBe("No compatibility declared");
  });
});
