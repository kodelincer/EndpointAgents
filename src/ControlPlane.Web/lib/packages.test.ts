import { describe, expect, it } from "vitest";
import { acceptedRiskLines, describeRun, describeStep, finishRules, isPublished, packageCompatibility, packageStatus } from "./packages";
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
    expect(packageCompatibility({ ...ready, office: null, kind: "general",
      compatibility: { minWindowsVersion: "10.0.17763" } }))
      .toBe("Windows 10.0.17763 to < latest");
    expect(packageCompatibility({ ...ready, office: null, kind: "general" }))
      .toBe("Any Windows version");
  });

  it("describes how a general package runs", () => {
    const noInput: BatPackage = { ...ready, office: null, kind: "general", fileType: "exe",
      exeSubsystem: "GUI", playbook: { version: 3, steps: [], arguments: ["/quiet", "/norestart"],
        timeoutSeconds: 600, restartExitCodes: [3010] } };
    expect(describeRun(noInput)).toEqual([
      'Runs the EXE directly (windowed program) with the fixed arguments "/quiet" "/norestart", hidden, as the endpoint service.',
      "No input: nothing is typed, input is closed at start, and the run waits for the file to finish."]);
    expect(finishRules(noInput.playbook!, 15)).toEqual([
      "Exit code must be 0.", "Exit code 3010 means it worked and the PC needs a restart.",
      "Stops as failed after 600 s in total.", "A window left open for 15 s stops the run."]);
    expect(describeRun({ ...noInput, fileType: "bat", exeSubsystem: null, playbook: {
      version: 2, steps: [{ expect: "Menu", send: "1", submit: "line", timeoutSeconds: 30 }] } }))
      .toEqual(["Runs the BAT through cmd.exe without arguments, hidden, as the endpoint service.",
        "Answers 1 screen as listed below."]);
  });

  it("lists the risks a reviewer accepted", () => {
    expect(acceptedRiskLines(ready)).toEqual([]);
    expect(acceptedRiskLines({ ...ready, review: { acceptedRisks: ["self-elevation", "embedded-payload"] } }))
      .toEqual([
        "Accepted by the reviewer: relaunches itself with administrator rights (the service is already admin).",
        "Accepted by the reviewer: runs code carried inside the file (covered by its hash and your signature)."]);
  });
});
