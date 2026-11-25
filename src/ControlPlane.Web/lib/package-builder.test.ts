import { describe, expect, it } from "vitest";
import {
  addSupportFile, approvalState, emptyForm, emptyStep, formFromDraft, formFromInstaller, formProblems, idFromFileName, stepFromSuggestion, toRequest,
  type BuilderDraft, type InstallerInfo, type ScreenSuggestion, type SupportFileInfo
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

describe("dashboard approval", () => {
  const info = { available: true, keyId: "ABCD", publicKey: "key" };
  const draft = { id: "fix", folder: "f", status: "Unsigned", reasons: ["not signed"], warnings: [], supportFiles: [],
    fileType: "bat", review: { builtBy: "member1" } } as unknown as import("./types").BatPackage;

  it("lets any admin approve an unsigned draft, including the one who saved it", () => {
    expect(approvalState(draft, info, true)).toEqual({ show: true, builtBy: "member1", blocked: null });
  });

  it("explains why the button is off", () => {
    expect(approvalState(draft, info, false).blocked).toBe("An admin approves it.");
    expect(approvalState(draft, { ...info, available: false, problem: "No key." }, true).blocked).toBe("No key.");
  });

  it("is offered for EXE and MSI drafts, not for ready packages or other file types", () => {
    expect(approvalState({ ...draft, status: "Ready" }, info, true).show).toBe(false);
    expect(approvalState({ ...draft, fileType: "exe" }, info, true).show).toBe(true);
    expect(approvalState({ ...draft, fileType: "msi" }, info, true).show).toBe(true);
    expect(approvalState({ ...draft, fileType: "zip" }, info, true).show).toBe(false);
  });
});

describe("install packages", () => {
  const info: InstallerInfo = {
    uploadId: "a".repeat(32), fileName: "npp.8.7.Installer.x64.exe", sizeBytes: 5_000_000, sha256: "AB",
    fileType: "exe", technology: "nsis", silentSwitches: ["/S"], productName: "Notepad++", signer: "CN=Notepad++", notes: []
  };

  it("fills the form from the installer as suggestions", () => {
    const form = formFromInstaller(emptyForm("install"), info, false);
    expect(form).toMatchObject({
      id: "notepad-install", appName: "Notepad++", installKind: "nsis", detectName: "Notepad++",
      displayName: "Install Notepad++"
    });
    expect(form.symptoms).toContain("Notepad++ is not installed");
    expect(formFromInstaller({ ...emptyForm("install"), id: "npp-install", detectName: "Notepad++ (64-bit" }, info, false))
      .toMatchObject({ id: "npp-install", detectName: "Notepad++ (64-bit" });
  });

  it("builds a typed install request with detection and requirements", () => {
    const form = {
      ...formFromInstaller(emptyForm("install"), info, false),
      symptoms: "نوت‌پد پلاس پلاس نصب نیست", installArguments: "/NCRC", minMemoryGB: "4", architectures: ["x64"],
      runtimes: ["vcredist-2015-x64"], detectNameAlt: "Notepad++ (64-bit x64)", licensed: true
    };
    expect(formProblems(form, true)).toEqual([]);
    const request = toRequest(form, null, false, info.uploadId);
    expect(request).toMatchObject({ kind: "install", uploadId: info.uploadId, file: null, playbook: { version: 3, steps: [] } });
    expect(request.software).toEqual({
      name: "Notepad++", licensed: true,
      requirements: { minWindowsBuild: null, minMemoryMb: 4096, minFreeDiskMb: null, architectures: ["x64"], runtimes: ["vcredist-2015-x64"], domainJoined: null },
      install: { kind: "nsis", msiProperties: null, arguments: ["/NCRC"], timeoutSeconds: 900 },
      detection: [{ kind: "uninstall-entry", displayName: "Notepad++" }, { kind: "uninstall-entry", displayName: "Notepad++ (64-bit x64)" }]
    });
  });

  it("lists what an install package still needs", () => {
    const problems = formProblems({ ...emptyForm("install"), id: "notepad", installKind: "exe", msiProperties: "ALLUSERS=1", installTimeoutSeconds: "10" }, false);
    expect(problems).toEqual(expect.arrayContaining([
      "Id: an install package id ends with -install, such as 7zip-install.",
      "Upload the installer (.msi or .exe).",
      "Type the name the app shows in Apps & features, so the PC can check the install worked.",
      "Type the vendor's silent-install switches for this EXE.",
      "MSI properties apply only to MSI installers.",
      "The install may take 60 to 3600 seconds."
    ]));
  });

  it("loads an install package back into the form", () => {
    const draft: BuilderDraft = {
      kind: "install", signed: false,
      manifest: {
        id: "7zip-install", displayName: "Install 7-Zip", description: "Installs it.", symptoms: ["7-Zip is missing"],
        playbook: { version: 3, steps: [] }, file: { fileName: "7z.msi", type: "msi" },
        software: {
          name: "7-Zip", licensed: false, requirements: { minFreeDiskMb: 2048 },
          install: { kind: "msi", msiProperties: { ALLUSERS: "1" }, timeoutSeconds: 600 },
          detection: [{ kind: "uninstall-entry", displayName: "7-Zip" }]
        }
      }
    };
    const form = formFromDraft(draft);
    expect(form).toMatchObject({ kind: "install", appName: "7-Zip", installKind: "msi", msiProperties: "ALLUSERS=1", detectName: "7-Zip", installTimeoutSeconds: "600", minFreeDiskGB: "2" });
    expect(toRequest(form, null, true).software?.install?.msiProperties).toEqual({ ALLUSERS: "1" });
  });
});

describe("supporting files", () => {
  const file = (fileName: string, uploadId: string | null = null): SupportFileInfo =>
    ({ fileName, uploadId, sizeBytes: 10, sha256: "AA", screens: [], notes: [] });

  it("sends the kept files by name and the new ones by upload", () => {
    const request = toRequest(filled(), null, true, null, [file("menu.txt"), file("tool.exe", "b".repeat(32))]);
    expect(request.keepSupportFiles).toEqual(["menu.txt"]);
    expect(request.supportUploadIds).toEqual(["b".repeat(32)]);
    expect(toRequest(filled(), null, true)).not.toHaveProperty("keepSupportFiles");
  });

  it("replaces a file of the same name when one is uploaded again", () => {
    const list = addSupportFile([file("Menu.txt"), file("a.cab")], file("menu.txt", "c".repeat(32)));
    expect(list.map(item => [item.fileName, item.uploadId])).toEqual([["a.cab", null], ["menu.txt", "c".repeat(32)]]);
  });
});
