/** Install packages: the signed "software" section, the "No approval needed" list and approval details. */
import { apiFetch } from "./control-plane";

export type DetectionRule = {
  kind: string; productCode?: string | null; displayName?: string | null; minVersion?: string | null;
  root?: string | null; path?: string | null; familyName?: string | null;
};
export type SoftwareSpec = {
  name?: string | null; version?: string | null; licensed?: boolean; provides?: string | null;
  requirements?: {
    minWindowsBuild?: number | null; editions?: string[] | null; architectures?: string[] | null;
    minMemoryMb?: number | null; minFreeDiskMb?: number | null; minCpuCores?: number | null;
    minScreenWidth?: number | null; minScreenHeight?: number | null; runtimes?: string[] | null;
    domainJoined?: boolean | null; mustNotBeInstalled?: DetectionRule[] | null;
  } | null;
  install?: {
    kind: string; msiProperties?: Record<string, string> | null; arguments?: string[] | null;
    copyTarget?: string | null; copyFolder?: string | null; timeoutSeconds?: number | null;
    returnCodes?: { success?: number[] | null; restart?: number[] | null; retry?: number[] | null } | null;
    manualReason?: string | null;
  } | null;
  detection?: DetectionRule[] | null;
};
export type NoApprovalEntry = { packageId: string; addedBy: string; addedAtUtc: string };

const gb = (mb: number) => `${Math.round(mb / 102.4) / 10} GB`;

/** One plain line per requirement the administrator signed. */
export function requirementLines(spec: SoftwareSpec): string[] {
  const r = spec.requirements;
  if (!r) return ["No system requirements."];
  return [
    r.minWindowsBuild ? `Windows build ${r.minWindowsBuild} or newer` : undefined,
    r.editions?.length ? `Windows edition ${r.editions.join(" or ")}` : undefined,
    r.architectures?.length ? `Processor ${r.architectures.join(" or ")}` : undefined,
    r.minMemoryMb ? `At least ${gb(r.minMemoryMb)} of memory` : undefined,
    r.minFreeDiskMb ? `At least ${gb(r.minFreeDiskMb)} free on drive C:` : undefined,
    r.minCpuCores ? `At least ${r.minCpuCores} processor cores` : undefined,
    r.minScreenWidth || r.minScreenHeight
      ? `Screen at least ${r.minScreenWidth ?? 0} x ${r.minScreenHeight ?? 0}` : undefined,
    r.runtimes?.length ? `Needs ${r.runtimes.join(", ")} (installed first when the catalog has it)` : undefined,
    r.domainJoined === true ? "PC must be in the domain" : r.domainJoined === false ? "PC must not be in the domain" : undefined,
    ...(r.mustNotBeInstalled ?? []).map(rule => `${detectionText(rule)} must not be installed`)
  ].filter((line): line is string => Boolean(line));
}

export function detectionText(rule: DetectionRule) {
  switch (rule.kind) {
    case "msi-product": return `MSI product ${rule.productCode}${rule.minVersion ? ` ${rule.minVersion}+` : ""}`;
    case "uninstall-entry": return `"${rule.displayName}" in Apps & features${rule.minVersion ? ` ${rule.minVersion}+` : ""}`;
    case "file": return `${rule.root}\\${rule.path}${rule.minVersion ? ` version ${rule.minVersion}+` : ""}`;
    case "msix-family": return `Store app ${rule.familyName}`;
    default: return rule.kind;
  }
}

const kindText: Record<string, string> = {
  msi: "MSI, run by msiexec silently (/qn /norestart)",
  msix: "MSIX, added for all users by DISM",
  inno: "Inno Setup EXE (/VERYSILENT)",
  nsis: "NSIS EXE (/S)",
  "wix-burn": "WiX bundle EXE (/quiet)",
  installshield: "InstallShield EXE (/s)",
  squirrel: "Squirrel EXE (--silent)",
  exe: "EXE with the vendor's silent switches",
  "copy-files": "ZIP copied into place",
  manual: "Cannot run silently: a helpdesk member installs it"
};

/** How the endpoint installs it and checks it afterwards. */
export function installLines(spec: SoftwareSpec): string[] {
  const install = spec.install;
  if (!install) return ["Runs as a BAT or EXE package with its playbook."];
  const codes = install.returnCodes;
  return [
    kindText[install.kind] ?? install.kind,
    install.msiProperties && Object.keys(install.msiProperties).length
      ? `MSI properties: ${Object.entries(install.msiProperties).map(([key, value]) => `${key}=${value}`).join(" ")}` : undefined,
    install.arguments?.length ? `Extra switches: ${install.arguments.join(" ")}` : undefined,
    install.copyTarget ? `Copied to ${install.copyTarget}${install.copyFolder ? `\\${install.copyFolder}` : ""}` : undefined,
    install.manualReason ? `Why manual: ${install.manualReason}` : undefined,
    install.kind !== "manual" ? `Exit codes: ${(codes?.success ?? [0]).join(", ")} worked; ${(codes?.restart ?? [3010, 1641]).join(", ")} worked and needs a restart; ${(codes?.retry ?? [1618]).join(", ")} try later` : undefined,
    install.timeoutSeconds ? `Stops after ${install.timeoutSeconds} s` : undefined,
    spec.detection?.length ? `Installed when: ${spec.detection.map(detectionText).join(" or ")}` : undefined,
    spec.provides ? `Provides the runtime ${spec.provides} to other packages` : undefined
  ].filter((line): line is string => Boolean(line));
}

/** Why the package can never be on the "No approval needed" list, or null when it can. */
export function cannotSkipApproval(item: { problem?: string | null; software?: SoftwareSpec | null }) {
  if (!item.problem?.startsWith("install-") && !item.software?.install) return "Only install packages can be on the list.";
  if (item.software?.licensed) return "Licensed software always needs approval.";
  return null;
}

/** Install packages are the ones the "No approval needed" switch applies to. */
export function isInstallPackage(item: { problem?: string | null; software?: SoftwareSpec | null }) {
  return Boolean(item.problem?.startsWith("install-") || item.software?.install);
}

export type ApprovalDetails = {
  software?: string; version?: string | null; licensed?: boolean; requirementsMet?: boolean | null;
  requirements?: { rule: string; passed: boolean; needed: string; found: string }[] | null;
  installFirst?: string[] | null;
};

/** The install details the control plane stored on a package approval, or null. */
export function approvalDetails(argumentsJson: string): ApprovalDetails | null {
  try {
    const value = JSON.parse(argumentsJson) as ApprovalDetails;
    return value && typeof value === "object" && typeof value.software === "string" ? value : null;
  } catch { return null; }
}

export async function getNoApproval(): Promise<NoApprovalEntry[]> {
  const response = await apiFetch("/api/software/no-approval");
  if (!response.ok) throw new Error(`Control plane returned ${response.status}.`);
  return response.json() as Promise<NoApprovalEntry[]>;
}

export async function setNoApproval(packageId: string, noApproval: boolean): Promise<NoApprovalEntry[]> {
  const response = await apiFetch(`/api/software/no-approval/${encodeURIComponent(packageId)}`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ noApproval })
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { message?: string };
    throw new Error(error.message ?? (response.status === 403
      ? "Only admins can change this list." : `Control plane returned ${response.status}.`));
  }
  return response.json() as Promise<NoApprovalEntry[]>;
}
