import { describe, expect, it } from "vitest";
import {
  fileCompatibilityLabel,
  fileRequirementLabels,
  fileRequiresEndpointToken
} from "./files";
import type { EndpointFile } from "./types";

const officeFile: EndpointFile = {
  artifactId: "m365-reset", fileName: "m365-reset.ps1", sizeBytes: 10,
  lastModifiedUtc: "2026-07-21T00:00:00Z", sha256: "A".repeat(64),
  downloadPath: "/api/files/office/m365-reset", category: "Office",
  supportedProductFamilies: ["Microsoft365Apps"], activationStates: ["Unlicensed"],
  minOfficeVersion: "16.0", requiresInteractiveUser: true,
  requiresOfficeAppsClosed: true, requiresRestart: false
};

describe("file repository presentation", () => {
  it("summarizes Office compatibility", () => {
    expect(fileCompatibilityLabel(officeFile)).toBe(
      "Microsoft365Apps · Unlicensed · 16.0–latest");
  });

  it("lists only enabled requirements", () => {
    expect(fileRequirementLabels(officeFile)).toEqual(
      ["Interactive user", "Close Office apps"]);
  });

  it("summarizes exclusive Office bugfix version ranges", () => {
    expect(fileCompatibilityLabel({
      ...officeFile,
      category: "Office bugfix",
      maxOfficeVersionExclusive: "17.0"
    })).toBe("Office 16.0 to < 17.0");
  });

  it("identifies artifact routes that require an enrolled endpoint token", () => {
    expect(fileRequiresEndpointToken(officeFile)).toBe(true);
    expect(fileRequiresEndpointToken({
      ...officeFile,
      category: "Windows",
      downloadPath: "/api/files/windows/bugfix/windows-activation-repair"
    })).toBe(true);
    expect(fileRequiresEndpointToken({
      ...officeFile,
      category: "IBSng",
      downloadPath: "/api/files/ibsng-dialer"
    })).toBe(false);
  });
});
