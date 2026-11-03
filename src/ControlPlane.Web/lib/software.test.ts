import { describe, expect, it } from "vitest";
import { approvalDetails, cannotSkipApproval, installLines, requirementLines } from "./software";

describe("software install packages", () => {
  it("describes requirements in plain words", () => {
    expect(requirementLines({ requirements: { minMemoryMb: 8192, minFreeDiskMb: 4096, domainJoined: true, runtimes: ["webview2"] } }))
      .toEqual(["At least 8 GB of memory", "At least 4 GB free on drive C:",
        "Needs webview2 (installed first when the catalog has it)", "PC must be in the domain"]);
    expect(requirementLines({})).toEqual(["No system requirements."]);
  });

  it("describes the install and the detection rule", () => {
    const lines = installLines({ install: { kind: "msi", msiProperties: { ALLUSERS: "1" } },
      detection: [{ kind: "uninstall-entry", displayName: "7-Zip" }] });
    expect(lines[0]).toContain("msiexec");
    expect(lines).toContain("MSI properties: ALLUSERS=1");
    expect(lines.at(-1)).toBe('Installed when: "7-Zip" in Apps & features');
  });

  it("never lets licensed or non-install packages skip approval", () => {
    expect(cannotSkipApproval({ problem: "install-app", software: { licensed: true } })).toMatch(/Licensed/);
    expect(cannotSkipApproval({ problem: "office-repair" })).toMatch(/Only install/);
    expect(cannotSkipApproval({ problem: "install-chrome" })).toBeNull();
  });

  it("reads approval details only when they are install details", () => {
    expect(approvalDetails('{"software":"7-Zip","requirementsMet":true,"requirements":[]}')?.software).toBe("7-Zip");
    expect(approvalDetails("{}")).toBeNull();
    expect(approvalDetails("not json")).toBeNull();
  });
});
