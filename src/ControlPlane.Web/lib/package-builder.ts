import { apiFetch } from "./control-plane";
import type { BatPackage, Playbook } from "./types";

/** The three kinds of package the builder makes, the same as new-package.ps1 -Kind. */
export type BuilderKind = "general" | "office" | "windows";

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
};

export type BuildRequest = {
  id: string; kind: BuilderKind; displayName: string; description: string;
  playbook: Playbook; file?: { fileName: string; fileBase64: string } | null; replace: boolean;
  symptoms?: string[]; problem?: string | null; order?: number | null; requiresApproval: boolean;
  office?: { minOfficeVersion: string; maxOfficeVersionExclusive: string; officeProductReleaseIds: string[] } | null;
  windows?: { minWindowsVersion: string; maxWindowsVersionExclusive: string; editionIds: string[] } | null;
  compatibility?: { minWindowsVersion?: string | null; maxWindowsVersionExclusive?: string | null } | null;
  review: { reviewedBy?: string | null; source?: string | null; notes?: string | null };
};

export type BuildResult = { saved: boolean; error?: string | null; status?: BatPackage | null };

/** A package read back from the server to edit in the form. */
export type BuilderDraft = {
  kind: BuilderKind; signed: boolean; scan?: ScanResult | null;
  manifest: {
    id: string; displayName: string; description: string; symptoms?: string[] | null;
    problem?: string | null; order?: number | null; requiresApproval?: boolean;
    playbook?: Playbook | null;
    bat?: { fileName: string } | null; file?: { fileName: string } | null;
    compatibility?: { minWindowsVersion?: string | null; maxWindowsVersionExclusive?: string | null } | null;
    office?: { minOfficeVersion: string; maxOfficeVersionExclusive: string; officeProductReleaseIds?: string[] | null } | null;
    windows?: { minWindowsVersion: string; maxWindowsVersionExclusive: string; editionIds?: string[] | null } | null;
    review?: { reviewedBy?: string | null; source?: string | null; notes?: string | null } | null;
  };
};

export const KIND_TEXT: Record<BuilderKind, { name: string; help: string }> = {
  office: { name: "Office activation", help: "Joins the Office activation repairs the agent tries when Office is not activated." },
  windows: { name: "Windows activation", help: "Joins the Windows activation repairs the agent tries when Windows is not activated." },
  general: { name: "General repair", help: "Any other repair. The agent offers it when a user describes one of its problems." }
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
    minWindowsVersion: kind === "windows" ? "10.0.19041" : "10.0.17763",
    maxWindowsVersionExclusive: kind === "windows" ? "10.0.30000" : "", editionIds: "",
    reviewedBy: "", source: "", notes: ""
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

/** The request the control plane turns into package.json. */
export function toRequest(form: BuilderForm, file: { fileName: string; fileBase64: string } | null,
  replace: boolean): BuildRequest {
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
    review: { reviewedBy: form.reviewedBy.trim() || null, source: form.source.trim() || null, notes: form.notes.trim() || null }
  };
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
    reviewedBy: manifest.review?.reviewedBy ?? "", source: manifest.review?.source ?? "", notes: manifest.review?.notes ?? ""
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
 * Where a package stands with dashboard approval. Only an Unsigned BAT package (no install
 * section) can be approved, and the person who saved it in the builder may not approve it.
 */
export function approvalState(item: BatPackage, info: ApprovalInfo | undefined, userName: string, isAdmin: boolean) {
  if (item.status !== "Unsigned" || item.fileType === "exe" || item.software) return { show: false as const };
  const builtBy = item.review?.builtBy ?? null;
  const own = Boolean(info?.requireSecondPerson && builtBy && builtBy.toLowerCase() === userName.toLowerCase());
  const blocked = !isAdmin ? "An admin approves it."
    : !info?.available ? (info?.problem ?? "Approving here is not available.")
    : own ? "You saved it yourself, so a different admin must approve it." : null;
  return { show: true as const, builtBy, blocked };
}

export const savePackage = (request: BuildRequest) =>
  send<BuildResult>("/api/files/packages/builder", { method: "POST", body: JSON.stringify(request) });
