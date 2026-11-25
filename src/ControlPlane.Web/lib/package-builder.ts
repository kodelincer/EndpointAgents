import { apiFetch } from "./control-plane";
import type { SoftwareSpec } from "./software";
import type { BatPackage, Playbook } from "./types";

/**
 * The kinds of package the builder makes: the three new-package.ps1 -Kind values, and
 * "install" for a vendor MSI or EXE (new-install-package.ps1 -Typed).
 */
export type BuilderKind = "general" | "office" | "windows" | "install";

/** How an installer runs; the PC adds the kind's own silent switches. */
export type InstallKind = "msi" | "inno" | "nsis" | "wix-burn" | "installshield" | "squirrel" | "exe";

/** What the control plane read from an uploaded installer. It is never run. */
export type InstallerInfo = {
  uploadId: string; fileName: string; sizeBytes: number; sha256: string; fileType: "msi" | "exe";
  technology: InstallKind; silentSwitches: string[]; productName?: string | null; signer?: string | null; notes: string[];
};

/** A file in the package's files\ folder, or one just uploaded for it (uploadId set). */
export type SupportFileInfo = {
  uploadId?: string | null; fileName: string; sizeBytes: number; sha256: string; signer?: string | null;
  screens: ScreenSuggestion[]; notes: string[];
};

export const INSTALL_KIND_TEXT: Record<InstallKind, string> = {
  msi: "MSI (msiexec /qn /norestart)",
  inno: "Inno Setup (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-)",
  nsis: "NSIS (/S)",
  "wix-burn": "WiX bundle (/quiet /norestart)",
  installshield: "InstallShield (/s /v\"/qn /norestart\")",
  squirrel: "Squirrel (--silent)",
  exe: "Other EXE: type the vendor's silent switches"
};

/** Runtimes a package can need, the same list as the control plane's KnownRuntimes. */
export const RUNTIMES = ["dotnet-desktop-6", "dotnet-desktop-8", "dotnet-desktop-9", "dotnet-desktop-10",
  "vcredist-2015-x64", "vcredist-2015-x86", "webview2", "java-8"];

export type MenuOption = { value: string; label?: string | null; goesTo?: string | null };

/** A place where the BAT waits for input, read from its text by the control plane. */
export type ScreenSuggestion = {
  line: number; kind: "menu" | "question" | "choice" | "pause"; section?: string | null;
  expect?: string | null; submit: "line" | "key"; options: MenuOption[]; shown: string[]; notes: string[];
};

export type ScanResult = {
  fileName: string; sha256: string; sizeBytes: number; lineCount: number;
  screens: ScreenSuggestion[]; lintErrors: string[]; lintWarnings: string[]; notes: string[];
};

/** One row of the steps table. Numbers are kept as text while the person types. */
export type StepRow = {
  key: string; expect: string; send: string; submit: "line" | "key";
  timeoutSeconds: string; optional: boolean; maxRepeats: string; note?: string;
};

export type BuilderForm = {
  id: string; kind: BuilderKind; displayName: string; description: string;
  symptoms: string; problem: string; order: string; requiresApproval: boolean;
  steps: StepRow[];
  successMarker: string; failureMarkers: string; successExitCodes: string;
  arguments: string; restartExitCodes: string; runTimeoutSeconds: string;
  minOfficeVersion: string; maxOfficeVersionExclusive: string; officeProductReleaseIds: string;
  minWindowsVersion: string; maxWindowsVersionExclusive: string; editionIds: string;
  reviewedBy: string; source: string; notes: string;
  /** Install packages only. Numbers are kept as text while the person types. */
  appName: string; installKind: InstallKind; installArguments: string; msiProperties: string;
  detectName: string; detectNameAlt: string; installTimeoutSeconds: string; licensed: boolean;
  minWindowsBuild: string; minMemoryGB: string; minFreeDiskGB: string; architectures: string[];
  runtimes: string[]; domainJoined: boolean;
};

export type BuildRequest = {
  id: string; kind: BuilderKind; displayName: string; description: string;
  playbook: Playbook; file?: { fileName: string; fileBase64: string } | null; replace: boolean;
  symptoms?: string[]; problem?: string | null; order?: number | null; requiresApproval: boolean;
  office?: { minOfficeVersion: string; maxOfficeVersionExclusive: string; officeProductReleaseIds: string[] } | null;
  windows?: { minWindowsVersion: string; maxWindowsVersionExclusive: string; editionIds: string[] } | null;
  compatibility?: { minWindowsVersion?: string | null; maxWindowsVersionExclusive?: string | null } | null;
  review: { reviewedBy?: string | null; source?: string | null; notes?: string | null };
  uploadId?: string | null; software?: SoftwareSpec | null;
  keepSupportFiles?: string[] | null; supportUploadIds?: string[] | null;
};

export type BuildResult = { saved: boolean; error?: string | null; status?: BatPackage | null };

/** A package read back from the server to edit in the form. */
export type BuilderDraft = {
  kind: BuilderKind; signed: boolean; scan?: ScanResult | null; supportFiles?: SupportFileInfo[] | null;
  manifest: {
    id: string; displayName: string; description: string; symptoms?: string[] | null;
    problem?: string | null; order?: number | null; requiresApproval?: boolean;
    playbook?: Playbook | null;
    bat?: { fileName: string } | null; file?: { fileName: string; type?: string; publisher?: string | null } | null;
    software?: SoftwareSpec | null;
    compatibility?: { minWindowsVersion?: string | null; maxWindowsVersionExclusive?: string | null } | null;
    office?: { minOfficeVersion: string; maxOfficeVersionExclusive: string; officeProductReleaseIds?: string[] | null } | null;
    windows?: { minWindowsVersion: string; maxWindowsVersionExclusive: string; editionIds?: string[] | null } | null;
    review?: { reviewedBy?: string | null; source?: string | null; notes?: string | null } | null;
  };
};

export const KIND_TEXT: Record<BuilderKind, { name: string; help: string }> = {
  office: { name: "Office activation", help: "Joins the Office activation repairs the agent tries when Office is not activated." },
  windows: { name: "Windows activation", help: "Joins the Windows activation repairs the agent tries when Windows is not activated." },
  general: { name: "General repair", help: "Any other repair. The agent offers it when a user describes one of its problems." },
  install: { name: "Install software", help: "A vendor MSI or EXE. The PC checks its requirements, installs it silently and checks it is there." }
};

let nextKey = 0;
const newKey = () => `step-${++nextKey}`;

export function emptyStep(): StepRow {
  return { key: newKey(), expect: "", send: "", submit: "line", timeoutSeconds: "60", optional: false, maxRepeats: "1" };
}

export function emptyForm(kind: BuilderKind = "office"): BuilderForm {
  return {
    id: "", kind, displayName: "", description: "", symptoms: "", problem: "", order: "", requiresApproval: false,
    steps: [], successMarker: "", failureMarkers: "", successExitCodes: "0",
    arguments: "", restartExitCodes: "", runTimeoutSeconds: "480",
    minOfficeVersion: "16.0", maxOfficeVersionExclusive: "17.0", officeProductReleaseIds: "",
    minWindowsVersion: kind === "windows" ? "10.0.19041" : kind === "install" ? "10.0.10240" : "10.0.17763",
    maxWindowsVersionExclusive: kind === "windows" ? "10.0.30000" : "", editionIds: "",
    reviewedBy: "", source: "", notes: "",
    appName: "", installKind: "msi", installArguments: "", msiProperties: "", detectName: "", detectNameAlt: "",
    installTimeoutSeconds: "900", licensed: false, minWindowsBuild: "", minMemoryGB: "", minFreeDiskGB: "",
    architectures: [], runtimes: [], domainJoined: false
  };
}

/**
 * Turns a suggestion and the option the person picked into a step. The person still
 * picks the answer: the builder never chooses a menu option by itself.
 */
export function stepFromSuggestion(screen: ScreenSuggestion, answer: string): StepRow {
  const option = screen.options.find(item => item.value === answer);
  return {
    ...emptyStep(),
    expect: screen.expect ?? "",
    send: answer,
    submit: screen.submit,
    note: option?.label ? `${screen.kind === "pause" ? "Continue" : option.label}` : undefined
  };
}

/** Plain words for a suggestion's heading. */
export function describeScreen(screen: ScreenSuggestion) {
  const where = screen.section ? ` in :${screen.section}` : "";
  const kind = { menu: "Menu", question: "Question", choice: "Choice (one key)", pause: "Pause" }[screen.kind];
  return `${kind}, line ${screen.line}${where}`;
}

/** Splits a list typed one per line (or comma-separated for short codes). */
export function splitLines(value: string) {
  return value.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
}

export function splitList(value: string) {
  return value.split(/[\n,]/).map(item => item.trim()).filter(Boolean);
}

function numbers(value: string) {
  return splitList(value).map(item => Number(item));
}

/** Problems the person should fix before saving; the server checks everything again. */
export function formProblems(form: BuilderForm, hasFile: boolean): string[] {
  const problems: string[] = [];
  if (!/^[a-z0-9][a-z0-9._-]{0,63}$/.test(form.id) || !/[a-z0-9]$/.test(form.id))
    problems.push("Id: lowercase letters, numbers, dots, underscores or hyphens, such as office-reseller-fix.");
  if (form.kind === "install") return [...problems, ...installProblems(form, hasFile)];
  if (!hasFile) problems.push("Upload the BAT file.");
  if (!form.displayName.trim()) problems.push("Give the package a name.");
  if (!form.description.trim()) problems.push("Describe what the package does.");
  if (form.kind === "general" && splitLines(form.symptoms).length === 0)
    problems.push("Add at least one way a user would describe the problem.");
  form.steps.forEach((step, index) => {
    const label = `Step ${index + 1}`;
    if (!step.expect.trim()) problems.push(`${label}: fill in the screen text to wait for.`);
    if (!step.send.trim()) problems.push(`${label}: fill in the answer.`);
    if (step.submit === "key" && step.send.trim().length !== 1) problems.push(`${label}: a key press is exactly one character.`);
    if (step.send.length > 64) problems.push(`${label}: the answer can be at most 64 characters.`);
    const timeout = Number(step.timeoutSeconds);
    if (!Number.isInteger(timeout) || timeout < 5 || timeout > 300) problems.push(`${label}: wait time must be 5 to 300 seconds.`);
    const repeats = Number(step.maxRepeats);
    if (!Number.isInteger(repeats) || repeats < 1 || repeats > 20) problems.push(`${label}: repeats must be 1 to 20.`);
  });
  if (form.steps.length > 32) problems.push("A playbook can have at most 32 steps.");
  const exitCodes = numbers(form.successExitCodes);
  if (exitCodes.length === 0 || exitCodes.some(code => !Number.isInteger(code)))
    problems.push("Success exit codes: whole numbers, such as 0.");
  if (/TODO/.test(JSON.stringify(form))) problems.push("Replace every TODO.");
  return problems;
}

const optionalNumber = (value: string) => value.trim() === "" ? null : Number(value);

function installProblems(form: BuilderForm, hasFile: boolean): string[] {
  const problems: string[] = [];
  if (!form.id.endsWith("-install")) problems.push("Id: an install package id ends with -install, such as 7zip-install.");
  if (!hasFile) problems.push("Upload the installer (.msi or .exe).");
  if (!form.displayName.trim()) problems.push("Give the package a name.");
  if (!form.description.trim()) problems.push("Describe what the package does.");
  if (splitLines(form.symptoms).length === 0) problems.push("Add at least one way a user would ask for the application.");
  if (!form.detectName.trim()) problems.push("Type the name the app shows in Apps & features, so the PC can check the install worked.");
  if (form.installKind === "exe" && splitArguments(form.installArguments).length === 0)
    problems.push("Type the vendor's silent-install switches for this EXE.");
  if (form.installKind !== "msi" && form.msiProperties.trim()) problems.push("MSI properties apply only to MSI installers.");
  if (form.installKind === "msi" && form.installArguments.trim()) problems.push("Extra switches apply only to EXE installers; use MSI properties.");
  for (const line of splitLines(form.msiProperties))
    if (!/^[A-Z0-9_.]+=[^"\r\n]+$/.test(line)) problems.push(`MSI property "${line}": write it as NAME=value with a capital-letter name.`);
  if (/["\r\n]/.test(form.installArguments.replace(/\n/g, " "))) problems.push("Extra switches cannot contain quotes.");
  const timeout = Number(form.installTimeoutSeconds);
  if (!Number.isInteger(timeout) || timeout < 60 || timeout > 3600) problems.push("The install may take 60 to 3600 seconds.");
  const build = optionalNumber(form.minWindowsBuild);
  if (build !== null && (!Number.isInteger(build) || build < 10240)) problems.push("Lowest Windows build: a number such as 19045.");
  for (const [label, value] of [["Memory", form.minMemoryGB], ["Free disk", form.minFreeDiskGB]] as const) {
    const amount = optionalNumber(value);
    if (amount !== null && !(amount > 0)) problems.push(`${label}: a number of GB, such as 4.`);
  }
  if (/TODO/.test(JSON.stringify(form))) problems.push("Replace every TODO.");
  return problems;
}

/** Switches typed one per line or separated by spaces. */
function splitArguments(value: string) {
  return value.split(/\s+/).map(item => item.trim()).filter(Boolean);
}

/** The signed "software" section from the install part of the form. */
export function softwareFromForm(form: BuilderForm): SoftwareSpec {
  const properties = Object.fromEntries(splitLines(form.msiProperties).map(line => {
    const index = line.indexOf("=");
    return [line.slice(0, index).trim(), line.slice(index + 1).trim()];
  }));
  const gbToMb = (value: string) => { const amount = optionalNumber(value); return amount === null ? null : Math.round(amount * 1024); };
  const requirements = {
    minWindowsBuild: optionalNumber(form.minWindowsBuild), minMemoryMb: gbToMb(form.minMemoryGB),
    minFreeDiskMb: gbToMb(form.minFreeDiskGB), architectures: form.architectures.length ? form.architectures : null,
    runtimes: form.runtimes.length ? form.runtimes : null, domainJoined: form.domainJoined ? true : null
  };
  const hasRequirements = Object.values(requirements).some(value => value !== null);
  const names = [form.detectName, form.detectNameAlt].map(name => name.trim()).filter(Boolean);
  return {
    name: form.appName.trim() || null, licensed: form.licensed,
    requirements: hasRequirements ? requirements : null,
    install: {
      kind: form.installKind,
      msiProperties: form.installKind === "msi" && Object.keys(properties).length ? properties : null,
      arguments: form.installKind !== "msi" && splitArguments(form.installArguments).length ? splitArguments(form.installArguments) : null,
      timeoutSeconds: Number(form.installTimeoutSeconds)
    },
    detection: names.map(displayName => ({ kind: "uninstall-entry", displayName }))
  };
}

/** The request the control plane turns into package.json. */
export function toRequest(form: BuilderForm, file: { fileName: string; fileBase64: string } | null,
  replace: boolean, uploadId: string | null = null, support: SupportFileInfo[] | null = null): BuildRequest {
  if (form.kind === "install") return {
    id: form.id.trim(), kind: "install", displayName: form.displayName.trim(), description: form.description.trim(),
    playbook: { version: 3, steps: [] }, file: null, replace, uploadId,
    symptoms: splitLines(form.symptoms), problem: form.problem.trim() || null,
    order: form.order.trim() ? Number(form.order) : null, requiresApproval: form.requiresApproval,
    compatibility: { minWindowsVersion: form.minWindowsVersion.trim() || null, maxWindowsVersionExclusive: form.maxWindowsVersionExclusive.trim() || null },
    software: softwareFromForm(form),
    review: { reviewedBy: form.reviewedBy.trim() || null, source: form.source.trim() || null, notes: form.notes.trim() || null }
  };
  const general = form.kind === "general";
  const playbook: Playbook = {
    version: general || form.steps.length === 0 ? 3 : 2,
    steps: form.steps.map(step => ({
      expect: step.expect.trim(), send: step.send.trim(), submit: step.submit,
      timeoutSeconds: Number(step.timeoutSeconds), optional: step.optional, maxRepeats: Number(step.maxRepeats)
    })),
    successExitCodes: numbers(form.successExitCodes),
    successMarker: form.successMarker.trim() || null,
    failureMarkers: splitLines(form.failureMarkers)
  };
  if (playbook.version === 3) {
    playbook.arguments = splitLines(form.arguments);
    playbook.timeoutSeconds = Number(form.runTimeoutSeconds) || 480;
    playbook.restartExitCodes = numbers(form.restartExitCodes);
  }
  return {
    id: form.id.trim(), kind: form.kind, displayName: form.displayName.trim(), description: form.description.trim(),
    playbook, file, replace,
    symptoms: general ? splitLines(form.symptoms) : [],
    problem: general && form.problem.trim() ? form.problem.trim() : null,
    order: general && form.order.trim() ? Number(form.order) : null,
    requiresApproval: general && form.requiresApproval,
    office: form.kind === "office" ? {
      minOfficeVersion: form.minOfficeVersion.trim(), maxOfficeVersionExclusive: form.maxOfficeVersionExclusive.trim(),
      officeProductReleaseIds: splitList(form.officeProductReleaseIds)
    } : null,
    windows: form.kind === "windows" ? {
      minWindowsVersion: form.minWindowsVersion.trim(), maxWindowsVersionExclusive: form.maxWindowsVersionExclusive.trim(),
      editionIds: splitList(form.editionIds)
    } : null,
    compatibility: general ? {
      minWindowsVersion: form.minWindowsVersion.trim() || null,
      maxWindowsVersionExclusive: form.maxWindowsVersionExclusive.trim() || null
    } : null,
    review: { reviewedBy: form.reviewedBy.trim() || null, source: form.source.trim() || null, notes: form.notes.trim() || null },
    ...(support ? supportRequest(support) : {})
  };
}

/** Which existing supporting files stay and which uploads are added; the server works out every hash. */
export function supportRequest(support: SupportFileInfo[]) {
  return {
    keepSupportFiles: support.filter(item => !item.uploadId).map(item => item.fileName),
    supportUploadIds: support.flatMap(item => item.uploadId ? [item.uploadId] : [])
  };
}

/** Adds an upload to the list, replacing a file of the same name (names are not case-sensitive). */
export function addSupportFile(support: SupportFileInfo[], added: SupportFileInfo) {
  return [...support.filter(item => item.fileName.toLowerCase() !== added.fileName.toLowerCase()), added]
    .sort((a, b) => a.fileName.localeCompare(b.fileName));
}

/** Fills the form from an existing package so it can be changed and saved again. */
export function formFromDraft(draft: BuilderDraft): BuilderForm {
  const { manifest } = draft;
  const playbook = manifest.playbook;
  const form = emptyForm(draft.kind);
  return {
    ...form,
    id: manifest.id, displayName: manifest.displayName ?? "", description: manifest.description ?? "",
    symptoms: (manifest.symptoms ?? []).join("\n"), problem: manifest.problem ?? "",
    order: manifest.order ? String(manifest.order) : "", requiresApproval: Boolean(manifest.requiresApproval),
    steps: (playbook?.steps ?? []).map(step => ({
      key: newKey(), expect: step.expect, send: step.send, submit: step.submit === "key" ? "key" : "line",
      timeoutSeconds: String(step.timeoutSeconds), optional: Boolean(step.optional), maxRepeats: String(step.maxRepeats ?? 1)
    })),
    successMarker: playbook?.successMarker ?? "", failureMarkers: (playbook?.failureMarkers ?? []).join("\n"),
    successExitCodes: (playbook?.successExitCodes ?? [0]).join(", "),
    arguments: (playbook?.arguments ?? []).join("\n"), restartExitCodes: (playbook?.restartExitCodes ?? []).join(", "),
    runTimeoutSeconds: String(playbook?.timeoutSeconds ?? 480),
    minOfficeVersion: manifest.office?.minOfficeVersion ?? form.minOfficeVersion,
    maxOfficeVersionExclusive: manifest.office?.maxOfficeVersionExclusive ?? form.maxOfficeVersionExclusive,
    officeProductReleaseIds: (manifest.office?.officeProductReleaseIds ?? []).join(", "),
    minWindowsVersion: manifest.windows?.minWindowsVersion ?? manifest.compatibility?.minWindowsVersion ?? form.minWindowsVersion,
    maxWindowsVersionExclusive: manifest.windows?.maxWindowsVersionExclusive ?? manifest.compatibility?.maxWindowsVersionExclusive ?? form.maxWindowsVersionExclusive,
    editionIds: (manifest.windows?.editionIds ?? []).join(", "),
    reviewedBy: manifest.review?.reviewedBy ?? "", source: manifest.review?.source ?? "", notes: manifest.review?.notes ?? "",
    ...installFromSoftware(manifest.software, form)
  };
}

function installFromSoftware(software: SoftwareSpec | null | undefined, form: BuilderForm): Partial<BuilderForm> {
  if (!software?.install) return {};
  const install = software.install;
  const requirements = software.requirements;
  const names = (software.detection ?? []).filter(rule => rule.kind === "uninstall-entry").map(rule => rule.displayName ?? "");
  const gb = (mb?: number | null) => mb ? String(Math.round(mb / 102.4) / 10) : "";
  return {
    appName: software.name ?? "", licensed: Boolean(software.licensed),
    installKind: (install.kind in INSTALL_KIND_TEXT ? install.kind : "exe") as InstallKind,
    installArguments: (install.arguments ?? []).join(" "),
    msiProperties: Object.entries(install.msiProperties ?? {}).map(([key, value]) => `${key}=${value}`).join("\n"),
    detectName: names[0] ?? "", detectNameAlt: names[1] ?? "",
    installTimeoutSeconds: String(install.timeoutSeconds ?? form.installTimeoutSeconds),
    minWindowsBuild: requirements?.minWindowsBuild ? String(requirements.minWindowsBuild) : "",
    minMemoryGB: gb(requirements?.minMemoryMb), minFreeDiskGB: gb(requirements?.minFreeDiskMb),
    architectures: requirements?.architectures ?? [], runtimes: requirements?.runtimes ?? [],
    domainJoined: requirements?.domainJoined === true
  };
}

/** Fills empty fields from what was read from an uploaded installer; the person can change all of it. */
export function formFromInstaller(form: BuilderForm, info: InstallerInfo, keepId: boolean): BuilderForm {
  const app = form.appName || info.productName || "";
  const base = idFromFileName(app || info.fileName.replace(/\.(msi|exe)$/i, "")).replace(/-install$/, "");
  return {
    ...form,
    id: keepId || form.id ? form.id : base ? `${base.slice(0, 56)}-install` : "",
    appName: app,
    installKind: info.technology,
    detectName: form.detectName || info.productName || "",
    displayName: form.displayName || (app ? `Install ${app}` : ""),
    description: form.description || (app ? `Checks this PC's system requirements and whether ${app} is installed and, if not, installs it silently for all users.` : ""),
    symptoms: form.symptoms || (app ? `${app} is not installed\nI need ${app} installed on my PC` : "")
  };
}

/** A safe id suggestion from the BAT's file name, such as "Office Fix v2.bat" -> "office-fix-v2". */
export function idFromFileName(fileName: string) {
  return fileName.replace(/\.bat$/i, "").toLowerCase().replace(/[^a-z0-9._-]+/g, "-")
    .replace(/^[^a-z0-9]+/, "").replace(/[^a-z0-9]+$/, "").slice(0, 64);
}

/** Reads a chosen file as base64 for the JSON request. */
export async function readFileBase64(file: File): Promise<string> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = "";
  for (let index = 0; index < bytes.length; index += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
  return btoa(binary);
}

async function send<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await apiFetch(path, { ...init, headers: { "Content-Type": "application/json", ...init.headers } });
  if (!response.ok) {
    const body = await response.json().catch(() => undefined) as { message?: string } | undefined;
    throw new Error(body?.message ?? (response.status === 403 ? "Your role does not allow this." : `Control plane returned ${response.status}.`));
  }
  return response.json() as Promise<T>;
}

export const scanBat = (fileName: string, fileBase64: string) =>
  send<ScanResult>("/api/files/packages/builder/scan", { method: "POST", body: JSON.stringify({ fileName, fileBase64 }) });

/** Sends the installer itself as the request body (no base64), so large files work. */
export const uploadInstaller = (file: File) => uploadRaw<InstallerInfo>("uploads", file);

/** Sends a supporting file the same way. */
export const uploadSupportFile = (file: File) => uploadRaw<SupportFileInfo>("support-uploads", file);

async function uploadRaw<T>(route: string, file: File): Promise<T> {
  const response = await apiFetch(`/api/files/packages/builder/${route}?fileName=${encodeURIComponent(file.name)}`, {
    method: "POST", body: file, headers: { "Content-Type": "application/octet-stream" }
  });
  if (!response.ok) {
    const body = await response.json().catch(() => undefined) as { message?: string } | undefined;
    throw new Error(body?.message ?? (response.status === 413 ? "The file is too large." : `Control plane returned ${response.status}.`));
  }
  return response.json() as Promise<T>;
}

export const loadDraft = (id: string) =>
  send<BuilderDraft>(`/api/files/packages/builder/${encodeURIComponent(id)}`);

/** Whether an admin can approve and sign packages here, and the key PCs must trust for that. */
export type ApprovalInfo = {
  available: boolean; keyId?: string | null; publicKey?: string | null;
  problem?: string | null; requireSecondPerson: boolean;
};

export const getApprovalInfo = () => send<ApprovalInfo>("/api/files/packages/approval");

/** Approves the exact revision on screen: both hashes must still match on the server. */
export const approvePackage = (item: BatPackage) =>
  send<BuildResult>(`/api/files/packages/builder/${encodeURIComponent(item.id)}/approve`, {
    method: "POST",
    body: JSON.stringify({ fileSha256: item.actualBatSha256, playbookSha256: item.computedPlaybookSha256 })
  });

/**
 * Where a package stands with dashboard approval. Only an Unsigned BAT, EXE or MSI package
 * can be approved, and the person who saved it in the builder may not approve it.
 */
export function approvalState(item: BatPackage, info: ApprovalInfo | undefined, userName: string, isAdmin: boolean) {
  if (item.status !== "Unsigned" || !["bat", "exe", "msi"].includes(item.fileType ?? "bat")) return { show: false as const };
  const builtBy = item.review?.builtBy ?? null;
  const own = Boolean(info?.requireSecondPerson && builtBy && builtBy.toLowerCase() === userName.toLowerCase());
  const blocked = !isAdmin ? "An admin approves it."
    : !info?.available ? (info?.problem ?? "Approving here is not available.")
    : own ? "You saved it yourself, so a different admin must approve it." : null;
  return { show: true as const, builtBy, blocked };
}

/** BAT packages and typed MSI/EXE installs; other EXE packages are made with the scripts. */
export function isBuilderPackage(item: BatPackage) {
  if (item.software?.install) return ["msi", "exe"].includes(item.fileType ?? "") && item.software.install.kind in INSTALL_KIND_TEXT;
  return (item.fileType ?? "bat") === "bat" && !item.software;
}

export const savePackage = (request: BuildRequest) =>
  send<BuildResult>("/api/files/packages/builder", { method: "POST", body: JSON.stringify(request) });
