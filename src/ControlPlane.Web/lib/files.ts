import type { EndpointFile } from "./types";

export function fileRequiresEndpointToken(file: EndpointFile) {
  return file.downloadPath.startsWith("/api/files/office/")
    || file.downloadPath.startsWith("/api/files/windows/");
}

export function fileCompatibilityLabel(file: EndpointFile) {
  if (file.category === "Office bugfix") {
    return `Office ${file.minOfficeVersion ?? "any"} to < ${file.maxOfficeVersionExclusive ?? "latest"}`;
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
