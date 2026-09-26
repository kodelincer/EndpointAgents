import type { EndpointFile } from "./types";

export function fileRequiresEndpointToken(file: EndpointFile) {
  return file.downloadPath.startsWith("/api/files/office/")
    || file.downloadPath.startsWith("/api/files/windows/");
}

export function fileCompatibilityLabel(file: EndpointFile) {
  if (file.category === "Office bugfix") {
    const ranges = [{
      minOfficeVersion: file.minOfficeVersion,
      maxOfficeVersionExclusive: file.maxOfficeVersionExclusive,
      officeProductReleaseIds: file.officeProductReleaseIds,
      minWindowsVersion: file.minWindowsVersion,
      maxWindowsVersionExclusive: file.maxWindowsVersionExclusive
    }, ...(file.additionalOfficeCompatibility ?? [])];
    return ranges.map((range) => [
      `Office ${range.minOfficeVersion ?? "any"} to < ${range.maxOfficeVersionExclusive ?? "latest"}`,
      range.officeProductReleaseIds?.length
        ? `Product IDs: ${range.officeProductReleaseIds.join(", ")}` : "",
      range.minWindowsVersion || range.maxWindowsVersionExclusive
        ? `Windows ${range.minWindowsVersion ?? "any"} to < ${range.maxWindowsVersionExclusive ?? "latest"}` : ""
    ].filter(Boolean).join(" · ")).join(" | ");
  }
  if (file.category === "Windows") {
    const ranges = [{
      minWindowsVersion: file.minWindowsVersion,
      maxWindowsVersionExclusive: file.maxWindowsVersionExclusive,
      editionIds: file.editionIds
    }, ...(file.additionalWindowsCompatibility ?? [])];
    return ranges.map((range) => {
      const versions = range.minWindowsVersion || range.maxWindowsVersionExclusive
        ? `Windows ${range.minWindowsVersion ?? "any"} to < ${range.maxWindowsVersionExclusive ?? "latest"}`
        : "Windows · all versions";
      return range.editionIds?.length
        ? `${versions} · Editions: ${range.editionIds.join(", ")}` : versions;
    }).join(" | ");
  }
  if (file.category !== "Office") return file.category;
  const families = file.supportedProductFamilies?.join(", ") || "Office";
  const states = file.activationStates?.join(", ") || "Any diagnostic state";
  const versions = file.minOfficeVersion || file.maxOfficeVersion
    ? `${file.minOfficeVersion ?? "any"}–${file.maxOfficeVersion ?? "latest"}` : "all versions";
  return `${families} · ${states} · ${versions}`;
}

export function fileRequirementLabels(file: EndpointFile) {
  return [
    file.requiresInteractiveUser ? "Interactive user" : undefined,
    file.requiresOfficeAppsClosed ? "Close Office apps" : undefined,
    file.requiresRestart ? "Restart required" : undefined
  ].filter((value): value is string => Boolean(value));
}
