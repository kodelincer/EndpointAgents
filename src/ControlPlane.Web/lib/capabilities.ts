import { apiFetch, CONTROL_PLANE_URL } from "./control-plane";

export type ActionTier = "Look" | "SafeFix" | "Disruptive" | "ItApproval" | "OperatorOnly";
export type Language = "en" | "fa";

export interface ActionParameterInfo {
  name: string; kind: string; description: string; required: boolean;
  choices?: { value: string; label: string }[] | null; targetKinds?: string[] | null; min: number; max: number;
}

export interface ActionStats { actionId: string; runs: number; succeeded: number; failed: number; blocked: number; lastRunUtc?: string | null }

export interface ActionInfo {
  id: string; area: string; tier: ActionTier; runAs: "System" | "User"; name: string; nameFa: string;
  description: string; returns: string; timeoutSeconds: number; defaultMaxPerHour: number; maxPerHour: number;
  enabled: boolean; requiresApproval?: boolean; approvalConfigurable?: boolean; verifiedBy?: string | null; undoActionId?: string | null; exampleResult?: string | null;
  usesSettings?: string[] | null; parameters: ActionParameterInfo[]; stats?: ActionStats | null;
}

export interface Capabilities {
  generatedAtUtc: string; areas: string[]; targetKinds: string[]; settingKeys: string[];
  accountChecksConfigured: boolean; skillsPublishToHermes: boolean; tools: string[]; actions: ActionInfo[];
}

export interface EnvironmentTarget {
  id: string; kind: string; label: string; address: string; ports: number[]; expectedAddresses: string[];
  driveLetter?: string | null; location?: string | null; notes?: string | null; updatedAtUtc?: string;
  details?: Record<string, string> | null;
}

export type ApprovalStatus = "Pending" | "Declined" | "Running" | "Done" | "Failed";

/** A step the agent asked for that waits for a helpdesk member. */
export interface ApprovalRequest {
  approvalId: string; kind: "action" | "package"; itemId: string; title: string; agentId: string;
  userSid?: string | null; conversationId?: string | null; argumentsJson: string; reason: string;
  status: ApprovalStatus; createdAtUtc: string; decidedAtUtc?: string | null; decidedBy?: string | null;
  note?: string | null; resultSummary?: string | null;
}

export interface Incident {
  incidentId: string; title: string; description: string; affectedTargets: string[];
  status: "Active" | "Resolved"; startedAtUtc: string; resolvedAtUtc?: string | null; createdBy?: string | null;
}

export interface ActionRun {
  runId: string; actionId: string; agentId: string; conversationId?: string | null; source: string;
  argumentsJson: string; status: string; summary: string; resultJson?: string | null;
  verificationJson?: string | null; undoJson?: string | null; undoneAtUtc?: string | null;
  startedAtUtc: string; completedAtUtc?: string | null;
}

export type SkillStatus = "Draft" | "InReview" | "Published" | "Retired";

export interface HelpdeskSkill {
  name: string; status: SkillStatus; description: string; body: string; author: string; reviewer?: string | null;
  version: number; publishedVersion?: number | null; reviewNote?: string | null; sourceConversationId?: string | null;
  createdAtUtc: string; updatedAtUtc: string; publishedAtUtc?: string | null;
}

export interface SkillVersion { name: string; version: number; description: string; body: string; author: string; note?: string | null; createdAtUtc: string }
export interface SkillStats { skillName: string; conversations: number; resolved: number; escalated: number; lastUsedUtc?: string | null }
export interface SkillIssue { severity: "error" | "warning"; message: string }
export interface SkillDetail { skill: HelpdeskSkill; versions: SkillVersion[]; rendered: string; issues: SkillIssue[] }
export interface SkillDraft { name: string; description: string; body: string; note?: string | null; sourceConversationId?: string | null }
export interface SkillPublishResult { published: boolean; writtenToHermes: boolean; path?: string | null; error?: string | null; issues: SkillIssue[] }

async function request<T>(path: string, method = "GET", body?: unknown): Promise<T> {
  const response = await apiFetch(path, {
    method,
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { message?: string; error?: string };
    throw new Error(error.message ?? error.error ?? (response.status === 403
      ? "Your role does not allow this." : `Control plane returned ${response.status}.`));
  }
  if (response.status === 204) return undefined as T;
  const type = response.headers.get("content-type") ?? "";
  return (type.includes("json") ? response.json() : response.text()) as Promise<T>;
}

const encode = encodeURIComponent;

export const getCapabilities = () => request<Capabilities>("/api/capabilities");
export const saveActionSetting = (id: string, enabled: boolean, maxPerHour: number | null,
  requiresApproval = false) =>
  request<void>(`/api/actions/${encode(id)}/settings`, "PUT", { enabled, maxPerHour, requiresApproval });

export const getApprovals = (pendingOnly = false) =>
  request<ApprovalRequest[]>(`/api/approvals?pendingOnly=${pendingOnly}&limit=200`);
export const approveRequest = (id: string, note: string) =>
  request<ApprovalRequest>(`/api/approvals/${encode(id)}/approve`, "POST", { note });
export const declineRequest = (id: string, note: string) =>
  request<ApprovalRequest>(`/api/approvals/${encode(id)}/decline`, "POST", { note });
export const requestMissingAction = (summary: string, details: string) =>
  request<void>("/api/actions/requests", "POST", { summary, details });
export const getActionRuns = (limit = 100) => request<ActionRun[]>(`/api/actions/runs?limit=${limit}`);
export const undoActionRun = (runId: string) => request<unknown>(`/api/actions/runs/${encode(runId)}/undo`, "POST");

export const getTargets = () => request<EnvironmentTarget[]>("/api/environment/targets");
export const saveTarget = (target: EnvironmentTarget) =>
  request<void>(`/api/environment/targets/${encode(target.id)}`, "PUT", target);
export const deleteTarget = (id: string) => request<void>(`/api/environment/targets/${encode(id)}`, "DELETE");
export const getEnvironmentSettings = () => request<Record<string, string>>("/api/environment/settings");
export const saveEnvironmentSettings = (values: Record<string, string>) =>
  request<void>("/api/environment/settings", "PUT", values);

export const getIncidents = () => request<Incident[]>("/api/incidents");
export const createIncident = (title: string, description: string, affectedTargets: string[]) =>
  request<Incident>("/api/incidents", "POST", { title, description, affectedTargets });
export const setIncidentStatus = (id: string, status: Incident["status"]) =>
  request<void>(`/api/incidents/${encode(id)}`, "PATCH", { status });

export const getSkills = () => request<{ skill: HelpdeskSkill; stats?: SkillStats | null }[]>("/api/skills");
export const getSkill = (name: string) => request<SkillDetail>(`/api/skills/${encode(name)}`);
export const getSkillTemplate = () => request<{ body: string }>("/api/skills/template");
export const lintSkill = (draft: SkillDraft) => request<SkillIssue[]>("/api/skills/lint", "POST", draft);
export const saveSkillDraft = (draft: SkillDraft) =>
  request<{ skill: HelpdeskSkill; issues: SkillIssue[] }>("/api/skills", "POST", draft);
export const draftFromConversation = (conversationId: string) =>
  request<SkillDraft>(`/api/skills/from-conversation/${encode(conversationId)}`);
export const submitSkill = (name: string) => request<void>(`/api/skills/${encode(name)}/submit`, "POST");
export const publishSkill = (name: string, version: number | null, note: string) =>
  request<SkillPublishResult>(`/api/skills/${encode(name)}/publish`, "POST", { version, note });
export const rejectSkill = (name: string, note: string) =>
  request<void>(`/api/skills/${encode(name)}/reject`, "POST", { note });
export const retireSkill = (name: string, note: string) =>
  request<void>(`/api/skills/${encode(name)}/retire`, "POST", { note });
export const skillDownloadUrl = (name: string) => `${CONTROL_PLANE_URL}/api/skills/${encode(name)}/download`;

// ---------------- Pure helpers (tested) ----------------

export const tierText: Record<ActionTier, Record<Language, string>> = {
  Look: { en: "Look (read-only)", fa: "فقط خواندنی" },
  SafeFix: { en: "Safe fix (no prompt)", fa: "اصلاح ایمن (بدون پرسش)" },
  Disruptive: { en: "Disruptive (user agrees first)", fa: "مزاحم (با موافقت کاربر)" },
  ItApproval: { en: "IT approval (helpdesk approves first)", fa: "با تأیید پشتیبانی" },
  OperatorOnly: { en: "Helpdesk only", fa: "فقط پشتیبانی" }
};

export const areaText: Record<string, Record<Language, string>> = {
  network: { en: "Network and web apps", fa: "شبکه و سامانه‌های وب" },
  domain: { en: "Sign-in, domain and time", fa: "ورود، دامنه و ساعت" },
  printers: { en: "Printers", fa: "چاپگرها" },
  drives: { en: "Shared drives and profile", fa: "درایوهای اشتراکی و پروفایل" },
  browsers: { en: "Browsers and certificates", fa: "مرورگر و گواهی" },
  office: { en: "Outlook and Teams", fa: "اوت‌لوک و تیمز" },
  health: { en: "PC health", fa: "سلامت سیستم" },
  desktop: { en: "Desktop and devices", fa: "دسکتاپ و دستگاه‌ها" },
  software: { en: "Software", fa: "نرم‌افزار" },
  devices: { en: "Devices and drivers", fa: "دستگاه‌ها و درایورها" }
};

/** Extra fields an environment entry of each kind carries (mirrors EnvironmentTargetDetails). */
export const targetDetailFields: Record<string, { key: string; label: string; hint: string; choices?: string[] }[]> = {
  app: [
    { key: "publisher", label: "Publisher (optional)", hint: "As Apps & features shows it; * allowed, e.g. Foxit*" },
    { key: "packageId", label: "Uninstall package (optional)", hint: "For apps not installed with Windows Installer, e.g. uninstall-anydesk" }
  ],
  device: [{ key: "packageId", label: "Driver package", hint: "The signed package that installs this model's driver, e.g. driver-brother-l2350" }],
  vpn: [
    { key: "type", label: "VPN type", hint: "builtin = Windows VPN connection; client = a vendor client (FortiClient, Cisco, GlobalProtect…)", choices: ["builtin", "client"] },
    { key: "connection", label: "Windows VPN connection name", hint: "For type builtin, exactly as in Settings > VPN" },
    { key: "service", label: "Client service name", hint: "For type client, e.g. FA_Scheduler, vpnagent, PanGPS, OpenVPNService" }
  ]
};

/** Counts pending approvals and the oldest one's age in minutes. */
export function pendingSummary(approvals: ApprovalRequest[], now = Date.now()) {
  const pending = approvals.filter(item => item.status === "Pending");
  const oldest = pending.reduce<number | null>((min, item) => {
    const time = Date.parse(item.createdAtUtc);
    return min === null || time < min ? time : min;
  }, null);
  return { count: pending.length, oldestMinutes: oldest === null ? null : Math.max(0, Math.round((now - oldest) / 60000)) };
}

/** Actions matching the search text, area and tier, in catalogue order. */
export function filterActions(actions: ActionInfo[], query: string, area: string, tier: string) {
  const text = query.trim().toLowerCase();
  return actions.filter(action =>
    (area === "all" || action.area === area) && (tier === "all" || action.tier === tier)
    && (!text || [action.id, action.name, action.nameFa, action.description]
      .some(value => value.toLowerCase().includes(text))));
}

/** Groups actions by area, keeping the given area order. */
export function groupByArea(actions: ActionInfo[], areas: string[]) {
  return areas.map(area => ({ area, actions: actions.filter(action => action.area === area) }))
    .filter(group => group.actions.length > 0);
}

/** Success rate in percent, or null when the action has not run. */
export function successRate(stats?: ActionStats | null) {
  if (!stats || stats.runs === 0) return null;
  return Math.round((stats.succeeded / stats.runs) * 100);
}

/** "443, 8443" -> [443, 8443]; ignores blanks and non-numbers. */
export function parseNumberList(value: string) {
  return value.split(/[,\s]+/).map(item => Number(item)).filter(item => Number.isInteger(item) && item > 0);
}

/** "a, b ,," -> ["a", "b"] */
export function parseTextList(value: string) {
  return value.split(",").map(item => item.trim()).filter(Boolean);
}

/** Lowercase id suggestion from a label, e.g. "HR Portal" -> "hr-portal". */
export function suggestId(label: string) {
  return label.toLowerCase().normalize("NFKD").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 48);
}

export function issueCounts(issues: SkillIssue[]) {
  return { errors: issues.filter(issue => issue.severity === "error").length,
    warnings: issues.filter(issue => issue.severity === "warning").length };
}

export const skillStatusText: Record<SkillStatus, string> = {
  Draft: "Draft", InReview: "Waiting for review", Published: "Published", Retired: "Retired"
};
