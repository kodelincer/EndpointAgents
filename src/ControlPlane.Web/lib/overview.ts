import { apiFetch } from "./control-plane";

export type OverviewCount = { key: string; label: string; count: number };
export type OverviewDay = { date: string; values: Record<string, number> };

export type OverviewSnapshot = {
  generatedAtUtc: string; days: number; sinceUtc: string;
  devices: { enrolled: number; online: number; offline: number; seenLast24Hours: number; notSeen7Days: number;
    windows: OverviewCount[]; agentVersions: OverviewCount[];
    longestOffline: { agentId: string; machineName: string; lastSeenUtc?: string | null }[] };
  chats: { started: number; solved: number; escalated: number; stillOpen: number; openNow: number; escalatedNow: number;
    agentRuns: number; agentRunsFailed: number; daily: OverviewDay[];
    recentEscalations: { conversationId: string; userName: string; machineName: string; reason?: string | null; escalatedAtUtc?: string | null }[] };
  operations: { runs: number; worked: number; failed: number; blocked: number; bySource: OverviewCount[]; daily: OverviewDay[];
    top: { id: string; name: string; runs: number; worked: number; failed: number; blocked: number }[] };
  packages: { artifactId: string; tries: number; worked: number; failed: number; lastTriedUtc: string }[];
  approvals: { pending: number; oldestPendingUtc?: string | null; decidedInRange: OverviewCount[];
    waiting: { approvalId: string; kind: string; title: string; createdAtUtc: string }[] };
  featureRequests: { total: number; new: number; inRange: number; byStatus: OverviewCount[];
    newest: { requestId: string; summary: string; status: string; createdAtUtc: string }[] };
  health: { rules: number; passed: number; fixed: number; failed: number; errors: number;
    byRule: { ruleId: string; name: string; enabled: boolean; passed: number; fixed: number; failed: number; errors: number; lastCheckedUtc?: string | null }[] };
  updates: { byStatus: OverviewCount[]; rolledBackInRange: number;
    problems: { agentId: string; machineName: string; status: string; fromVersion?: string | null; toVersion?: string | null; message?: string | null; reportedAtUtc?: string | null }[] };
  lessons: OverviewCount[];
  skills: { name: string; uses: number; solved: number; escalated: number }[];
  /** Agent problem reports from PCs; null until that feature exists. */
  problems: { open: number; newInRange: number } | null;
};

export const RANGE_CHOICES = [7, 14, 30, 90] as const;

/** Reads the whole Overview in one call; days are counted on this browser's calendar. */
export async function getOverview(days: number): Promise<OverviewSnapshot> {
  const offset = -new Date().getTimezoneOffset();
  const response = await apiFetch(`/api/overview?days=${days}&utcOffsetMinutes=${offset}`);
  if (!response.ok) throw new Error(response.status === 403
    ? "Your role does not allow this." : `Control plane returned ${response.status}.`);
  return response.json() as Promise<OverviewSnapshot>;
}

/** Share as a whole percent, or null when there is nothing to divide. */
export function percent(part: number, whole: number): number | null {
  return whole > 0 ? Math.round((part / whole) * 100) : null;
}

/** 1,284 / 12.9K / 4.2M */
export function compact(value: number): string {
  if (Math.abs(value) < 10_000) return value.toLocaleString("en-US");
  return new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 }).format(value);
}

/** Clean axis ticks from 0 to a round maximum at or above the data. */
export function niceTicks(max: number, count = 4): number[] {
  if (max <= 0) return [0, 1];
  const raw = max / count;
  const power = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 5, 10].map(f => f * power).find(s => s >= raw) ?? raw;
  const ticks: number[] = [];
  for (let value = 0; value < max + step; value += step) { ticks.push(value); if (value >= max) break; }
  return ticks;
}

/** Sum of each day's values for the named series. */
export function dayTotal(day: OverviewDay, series: readonly string[]): number {
  return series.reduce((sum, name) => sum + (day.values[name] ?? 0), 0);
}

/** "12 Nov" from a yyyy-MM-dd key, without moving it to another day. */
export function shortDay(key: string): string {
  const [year, month, day] = key.split("-").map(Number);
  return new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", timeZone: "UTC" })
    .format(new Date(Date.UTC(year, month - 1, day)));
}

export function ago(value?: string | null): string {
  if (!value) return "never";
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}
