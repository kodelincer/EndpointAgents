import { describe, expect, it } from "vitest";
import {
  emptyForm, emptyStep, formFromDraft, formProblems, idFromFileName, stepFromSuggestion, toRequest,
  type BuilderDraft, type ScreenSuggestion
} from "./package-builder";

const menu: ScreenSuggestion = {
  line: 12, kind: "menu", section: "main", expect: "Select an option [1-6]:", submit: "line",
  options: [{ value: "1", label: "Office repair", goesTo: "office" }, { value: "6", label: "Exit" }],
  shown: ["[1] Office repair", "[6] Exit"], notes: []
};

function filled() {
  const form = emptyForm("office");
  return {
    ...form, id: "office-reseller-fix", displayName: "Office activation", description: "Activates Office through the reseller BAT.",
    steps: [stepFromSuggestion(menu, "1")], successMarker: "Product activation successful", failureMarkers: "FAILED\nError"
  };
}

describe("package builder form", () => {
  it("turns a picked menu answer into a step without choosing one by itself", () => {
    const step = stepFromSuggestion(menu, "1");
    expect(step).toMatchObject({ expect: "Select an option [1-6]:", send: "1", submit: "line", timeoutSeconds: "60", note: "Office repair" });
  });

  it("builds a version 2 Office playbook with the finish rules", () => {
    const request = toRequest(filled(), { fileName: "fix.bat", fileBase64: "QA==" }, false);
    expect(request.kind).toBe("office");
    expect(request.playbook).toEqual({
      version: 2,
      steps: [{ expect: "Select an option [1-6]:", send: "1", submit: "line", timeoutSeconds: 60, optional: false, maxRepeats: 1 }],
      successExitCodes: [0], successMarker: "Product activation successful", failureMarkers: ["FAILED", "Error"]
    });
    expect(request.office).toEqual({ minOfficeVersion: "16.0", maxOfficeVersionExclusive: "17.0", officeProductReleaseIds: [] });
    expect(request.windows).toBeNull();
    expect(request.symptoms).toEqual([]);
  });

  it("uses version 3 with arguments and a run timeout for general packages", () => {
    const form = { ...filled(), kind: "general" as const, symptoms: "printer is stuck\nچاپگر کار نمی‌کند", arguments: "/quiet", restartExitCodes: "3010" };
    const request = toRequest(form, null, true);
    expect(request.playbook.version).toBe(3);
    expect(request.playbook.arguments).toEqual(["/quiet"]);
    expect(request.playbook.restartExitCodes).toEqual([3010]);
    expect(request.symptoms).toEqual(["printer is stuck", "چاپگر کار نمی‌کند"]);
    expect(request.compatibility).toEqual({ minWindowsVersion: "10.0.19041".replace("19041", "17763"), maxWindowsVersionExclusive: null });
    expect(request.replace).toBe(true);
  });

  it("lists what to fix before saving", () => {
    expect(formProblems(filled(), true)).toEqual([]);
    const broken = { ...filled(), id: "Bad Id", steps: [{ ...emptyStep(), submit: "key" as const, send: "12" }] };
    const problems = formProblems(broken, false);
    expect(problems.some(problem => problem.startsWith("Id:"))).toBe(true);
    expect(problems).toContain("Upload the BAT file.");
    expect(problems).toContain("Step 1: fill in the screen text to wait for.");
    expect(problems).toContain("Step 1: a key press is exactly one character.");
    expect(formProblems({ ...filled(), description: "TODO" }, true)).toContain("Replace every TODO.");
  });

  it("suggests an id from the file name", () => {
    expect(idFromFileName("Office Fix v2.BAT")).toBe("office-fix-v2");
    expect(idFromFileName("__KMS!!.bat")).toBe("kms");
  });

  it("loads an existing package back into the form", () => {
    const draft: BuilderDraft = {
      kind: "windows", signed: true,
      manifest: {
        id: "win-fix", displayName: "Windows fix", description: "Fixes it.",
        playbook: { version: 2, steps: [{ expect: "Continue? [Y/N]", send: "Y", submit: "key", timeoutSeconds: 30, optional: true, maxRepeats: 2 }], successExitCodes: [0, 1], failureMarkers: ["FAILED"] },
        bat: { fileName: "fix.bat" },
        windows: { minWindowsVersion: "10.0.19041", maxWindowsVersionExclusive: "10.0.30000", editionIds: ["Professional"] },
        review: { reviewedBy: "Reza", source: "vendor" }
      }
    };
    const form = formFromDraft(draft);
    expect(form.kind).toBe("windows");
    expect(form.steps[0]).toMatchObject({ expect: "Continue? [Y/N]", send: "Y", submit: "key", timeoutSeconds: "30", optional: true, maxRepeats: "2" });
    expect(form.successExitCodes).toBe("0, 1");
    expect(form.editionIds).toBe("Professional");
    expect(toRequest(form, null, true).windows?.editionIds).toEqual(["Professional"]);
  });
});
