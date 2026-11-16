"use client";

import { CircleAlert, HeartPulse, Plus, RefreshCw, Trash2 } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";
import {
  createHealthRule, deleteHealthRule, describeRule, getHealthChecks, getHealthResults, getHealthRules,
  onFailureText, parseMachines, reportOnlyDaysLeft, setHealthRuleEnabled,
  type HealthCheck, type HealthResult, type HealthRuleInput, type HealthRuleSummary
} from "@/lib/health";

/** Health rules and the Compliance view: IT picks five answers; the control plane runs them without AI. */
export function HealthRules() {
  const { can } = useAuth();
  const admin = can("devices.manage");
  const [checks, setChecks] = useState<HealthCheck[]>([]);
  const [rules, setRules] = useState<HealthRuleSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [open, setOpen] = useState<string>();
  const [results, setResults] = useState<HealthResult[]>([]);
  const [adding, setAdding] = useState(false);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      const [checkList, ruleList] = await Promise.all([getHealthChecks(), getHealthRules()]);
      setChecks(checkList); setRules(ruleList); setError(undefined);
    } catch (reason) { setError(reason instanceof Error ? reason.message : "Health rules could not be loaded."); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0);
    return () => window.clearTimeout(initial);
  }, [refresh]);

  const showResults = async (ruleId: string) => {
    if (open === ruleId) { setOpen(undefined); return; }
    setOpen(ruleId);
    try { setResults(await getHealthResults(ruleId)); } catch { setResults([]); }
  };

  const act = async (work: () => Promise<unknown>) => {
    try { await work(); await refresh(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "The change was not saved."); }
  };

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="health-title">
      <div><div className="file-heading-mark"><HeartPulse size={17} /></div><div><h1 id="health-title">{pageName("/health/")}</h1></div></div>
      <div className="file-row-actions">
        {admin && <button className="primary-button" onClick={() => setAdding(!adding)}><Plus size={15} />New rule</button>}
        <button className="secondary-button" onClick={() => void refresh()} disabled={loading}><RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
      </div>
    </section>
    <PageIntro route="/health/" />

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Health rules</strong><span>{error}</span></div><button onClick={() => setError(undefined)}>Close</button></div>}

    {adding && <NewRule checks={checks} onCancel={() => setAdding(false)}
      onSave={input => act(async () => { await createHealthRule(input); setAdding(false); })} />}

    {loading && rules.length === 0 ? <div className="file-list"><div className="file-skeleton" /></div>
      : rules.length === 0 ? <section className="file-empty"><span className="empty-icon"><HeartPulse size={23} /></span>
        <h2>No health rules yet</h2><p>Start with a few: low disk space, not restarted for a week, old antivirus definitions, clock out of sync.</p></section>
      : <section className="file-list" aria-label="Health rules">{rules.map(summary => {
        const rule = summary.rule;
        const check = checks.find(item => item.id === rule.checkId);
        const days = reportOnlyDaysLeft(rule);
        const total = summary.passed + summary.fixed + summary.failed + summary.errors;
        return <article key={rule.ruleId} className="file-row">
          <div className="file-row-main">
            <div className="file-row-title"><strong>{rule.name}</strong>
              {!rule.enabled ? <span className="ticket-status">Paused</span>
                : days > 0 ? <span className="ticket-status">Only reporting: {days} day{days === 1 ? "" : "s"} left</span>
                : <span className="ticket-status resolved">{onFailureText[rule.onFailure]}</span>}</div>
            <p className="file-description">{describeRule(rule, check)}</p>
            <p className="file-compatibility health-counts">
              <span className="passed">{summary.passed} pass</span> · <span className="passed">{summary.fixed} fixed</span> · <span className="failed">{summary.failed} fail</span>
              {summary.errors > 0 && <> · {summary.errors} not checked</>}
              {days > 0 && summary.wouldAct > 0 && rule.onFailure !== "report" && <> · would act on {summary.wouldAct} after the first week</>}
              {total === 0 && " · not run yet"}{summary.lastCheckedUtc && ` · last ${new Date(summary.lastCheckedUtc).toLocaleString()}`}</p>
            <button type="button" className="package-toggle" onClick={() => void showResults(rule.ruleId)} aria-expanded={open === rule.ruleId}>
              {open === rule.ruleId ? "Hide PCs" : "Show PCs"}</button>
            {open === rule.ruleId && <table className="health-results"><thead><tr><th>PC</th><th>Result</th><th>Found</th><th>Done</th><th>When</th></tr></thead>
              <tbody>{results.map(result => <tr key={result.agentId}><td>{result.machineName}</td>
                <td className={result.outcome}>{result.outcome}</td><td>{result.finding}</td>
                <td>{result.actionTaken ?? (result.wouldAct ? "would act after the first week" : "")}</td>
                <td>{new Date(result.checkedAtUtc).toLocaleString()}</td></tr>)}</tbody></table>}
          </div>
          {admin && <div className="file-row-actions">
            <button className="secondary-button" onClick={() => void act(() => setHealthRuleEnabled(rule.ruleId, !rule.enabled))}>
              {rule.enabled ? "Pause" : "Resume"}</button>
            <button className="secondary-button" aria-label={`Delete ${rule.name}`}
              onClick={() => window.confirm(`Delete "${rule.name}" and its results?`) && void act(() => deleteHealthRule(rule.ruleId))}><Trash2 size={14} /></button>
          </div>}
        </article>;
      })}</section>}
  </div>;
}

function NewRule({ checks, onSave, onCancel }: { checks: HealthCheck[]; onSave: (input: HealthRuleInput) => void; onCancel: () => void }) {
  const [checkId, setCheckId] = useState(checks[0]?.id ?? "");
  const check = useMemo(() => checks.find(item => item.id === checkId), [checks, checkId]);
  const [name, setName] = useState("");
  const [option, setOption] = useState<string>();
  const [fix, setFix] = useState<string>();
  const [frequency, setFrequency] = useState<HealthRuleInput["frequency"]>("daily");
  const [machines, setMachines] = useState("");
  const [onFailure, setOnFailure] = useState<HealthRuleInput["onFailure"]>("report");

  const pickCheck = (id: string) => {
    setCheckId(id); setOption(undefined); setFix(undefined); setOnFailure("report");
  };
  const chosenOption = option ?? check?.defaultOption ?? undefined;
  const chosenFix = fix ?? check?.fixes[0]?.id;

  return <form className="health-form" onSubmit={event => {
    event.preventDefault();
    onSave({ name: name || check?.name || checkId, checkId, option: check?.options.length ? chosenOption : null,
      fixActionId: onFailure === "fix" ? chosenFix : null, frequency, machines: parseMachines(machines), onFailure });
  }}>
    <label>1. Check<select value={checkId} onChange={event => pickCheck(event.target.value)}>
      {checks.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
    {check && <p className="file-compatibility">{check.description}</p>}
    {check && check.options.length > 0 && <label>{check.optionLabel}<select value={chosenOption} onChange={event => setOption(event.target.value)}>
      {check.options.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}</select></label>}
    <label>2. When it fails<select value={onFailure} onChange={event => setOnFailure(event.target.value as HealthRuleInput["onFailure"])}>
      <option value="report">{onFailureText.report}</option>
      {check && check.fixes.length > 0 && <option value="fix">{onFailureText.fix}</option>}
      {check?.canTellUser && <option value="tell-user">{onFailureText["tell-user"]}: “{check.notice}”</option>}</select></label>
    {onFailure === "fix" && check && <label>3. Fix<select value={chosenFix} onChange={event => setFix(event.target.value)}>
      {check.fixes.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>}
    <label>4. How often<select value={frequency} onChange={event => setFrequency(event.target.value as HealthRuleInput["frequency"])}>
      <option value="daily">Daily</option><option value="weekly">Weekly</option></select></label>
    <label>5. Which PCs<input value={machines} onChange={event => setMachines(event.target.value)}
      placeholder="Empty for every PC, or PC names for a pilot, separated by commas" /></label>
    <label>Name<input value={name} onChange={event => setName(event.target.value)} placeholder={check?.name} maxLength={80} /></label>
    <p className="file-compatibility">The first week it only reports, so you see how many PCs would be touched before any fix runs or any user is told.</p>
    <div className="file-row-actions"><button className="primary-button" type="submit" disabled={!check}>Save rule</button>
      <button className="secondary-button" type="button" onClick={onCancel}>Cancel</button></div>
  </form>;
}
