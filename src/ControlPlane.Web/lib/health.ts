/** Health rules: fixed checks IT members combine into scheduled rules (no AI, no scripts). */
import { apiFetch } from "./control-plane";

export type HealthCheck = {
  id: string; name: string; nameFa: string; description: string; optionLabel: string;
  defaultOption?: string | null; options: { value: string; label: string }[];
  fixes: { id: string; name: string }[]; canTellUser: boolean; notice?: string | null;
};
export type HealthRuleInput = {
  name: string; checkId: string; option?: string | null; fixActionId?: string | null;
  frequency: "daily" | "weekly"; machines: string[]; onFailure: "report" | "fix" | "tell-user";
};
export type HealthRule = HealthRuleInput & {
  ruleId: string; enabled: boolean; createdBy: string; createdAtUtc: string; reportOnlyUntilUtc: string;
};
export type HealthRuleSummary = {
  rule: HealthRule; passed: number; fixed: number; failed: number; errors: number; wouldAct: number;
  lastCheckedUtc?: string | null;
};
export type HealthResult = {
  ruleId: string; agentId: string; machineName: string; checkedAtUtc: string;
  outcome: "passed" | "failed" | "fixed" | "error"; finding: string; actionTaken?: string | null; wouldAct: boolean;
};

export const onFailureText: Record<HealthRuleInput["onFailure"], string> = {
  report: "Only report", fix: "Fix it by itself", "tell-user": "Tell the user in the Tray"
};

/** Split a typed list of PC names; an empty list means every PC. */
export function parseMachines(text: string): string[] {
  return [...new Set(text.split(/[\s,;]+/).map(name => name.trim().toUpperCase()).filter(Boolean))];
}

/** Days left in the first report-only week, or 0 when the rule acts. */
export function reportOnlyDaysLeft(rule: Pick<HealthRule, "reportOnlyUntilUtc">, now = new Date()) {
  const left = new Date(rule.reportOnlyUntilUtc).getTime() - now.getTime();
  return left <= 0 ? 0 : Math.ceil(left / 86_400_000);
}

/** One line that says what the rule does, in plain words. */
export function describeRule(rule: HealthRuleInput, check?: HealthCheck) {
  const option = check?.options.find(item => item.value === rule.option)?.label;
  const fix = check?.fixes.find(item => item.id === rule.fixActionId)?.name;
  const action = rule.onFailure === "fix" ? `runs "${fix ?? rule.fixActionId}"`
    : rule.onFailure === "tell-user" ? `tells the user "${check?.notice ?? "a fixed notice"}"` : "only reports it";
  const pcs = rule.machines.length === 0 ? "every PC" : `${rule.machines.length} PC${rule.machines.length === 1 ? "" : "s"}`;
  return `${check?.name ?? rule.checkId}${option ? ` (${check?.optionLabel.toLowerCase()} ${option})` : ""}, ${rule.frequency} on ${pcs}; on failure it ${action}.`;
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await apiFetch(path, init);
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { message?: string };
    throw new Error(error.message ?? (response.status === 403 ? "Only admins can change health rules."
      : `Control plane returned ${response.status}.`));
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

const json = (method: string, body: unknown): RequestInit =>
  ({ method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });

export const getHealthChecks = () => call<HealthCheck[]>("/api/health/checks");
export const getHealthRules = () => call<HealthRuleSummary[]>("/api/health/rules");
export const getHealthResults = (ruleId: string) => call<HealthResult[]>(`/api/health/rules/${encodeURIComponent(ruleId)}/results`);
export const createHealthRule = (input: HealthRuleInput) => call<HealthRule>("/api/health/rules", json("POST", input));
export const setHealthRuleEnabled = (ruleId: string, enabled: boolean) =>
  call<void>(`/api/health/rules/${encodeURIComponent(ruleId)}`, json("PATCH", { enabled }));
export const deleteHealthRule = (ruleId: string) =>
  call<void>(`/api/health/rules/${encodeURIComponent(ruleId)}`, { method: "DELETE" });
