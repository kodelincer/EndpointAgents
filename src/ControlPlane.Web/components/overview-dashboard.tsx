"use client";

import { AlertTriangle, ArrowUpRight, BadgeCheck, Bug, CircleAlert, ClipboardCheck, CloudDownload, GraduationCap, HeartPulse,
  Lightbulb, LifeBuoy, MonitorSmartphone, Package, RefreshCw, Wrench, type LucideIcon } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { DailyColumns, Legend, RankedBars, SegmentedBar, Sparkline, SuccessMeter, type Series } from "@/components/overview-charts";
import { ago, compact, dayTotal, getOverview, percent, RANGE_CHOICES, type OverviewSnapshot } from "@/lib/overview";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";

const rangeKey = "cp.overview.days";

const CHAT_SERIES: Series[] = [
  { key: "solved", label: "Solved by the agent", color: "var(--ov-series-3)" },
  { key: "escalated", label: "Sent to helpdesk", color: "var(--ov-series-2)" },
  { key: "open", label: "Still open", color: "var(--ov-series-1)" }
];
const FIX_SERIES: Series[] = [
  { key: "worked", label: "Worked", color: "var(--ov-good)" },
  { key: "failed", label: "Failed", color: "var(--ov-critical)" },
  { key: "blocked", label: "Blocked", color: "var(--ov-warning)" }
];
const WINDOWS_COLORS: Record<string, string> = {
  win11: "var(--ov-series-1)", win10: "var(--ov-series-2)", other: "var(--ov-series-3)", unknown: "var(--ov-none)"
};
const UPDATE_LABELS: Record<string, string> = {
  Succeeded: "Updated", RolledBack: "Rolled back", Failed: "Failed", Downloading: "Downloading", Installing: "Installing"
};

function readRange() {
  try { const value = Number(window.localStorage.getItem(rangeKey)); return RANGE_CHOICES.includes(value as never) ? value : 14; }
  catch { return 14; }
}

/** The at-a-glance part of the Overview: tiles, charts and short lists from one API call. */
export function OverviewDashboard() {
  const [days, setDays] = useState(14);
  const [data, setData] = useState<OverviewSnapshot>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    const restore = window.setTimeout(() => setDays(readRange()), 0);
    return () => window.clearTimeout(restore);
  }, []);

  const load = useCallback(async (range: number) => {
    setLoading(true);
    try { setData(await getOverview(range)); setError(undefined); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "The overview could not be loaded."); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void load(days), 0);
    const poll = window.setInterval(() => void load(days), 30_000);
    return () => { window.clearTimeout(initial); window.clearInterval(poll); };
  }, [days, load]);

  const choose = (value: number) => {
    setDays(value);
    try { window.localStorage.setItem(rangeKey, String(value)); } catch { /* storage blocked */ }
  };

  return <div className="ov-dashboard">
    <section className="overview-heading" aria-labelledby="overview-title">
      <div><h1 id="overview-title">{pageName("/")}</h1></div>
      <div className="ov-toolbar">
        <div className="ov-range" role="group" aria-label="Time range">
          {RANGE_CHOICES.map(value => <button key={value} type="button" aria-pressed={days === value}
            onClick={() => choose(value)}>{value === 7 ? "7 days" : `${value} d`}</button>)}
        </div>
        <button className="icon-button" onClick={() => void load(days)} aria-label="Refresh overview" title="Refresh">
          <RefreshCw size={15} className={loading ? "spin" : undefined} /></button>
        <span className="last-check">{data ? `Updated ${ago(data.generatedAtUtc)}` : "Loading"}</span>
      </div>
    </section>
    <PageIntro route="/" />

    {error && <div className="inline-error overview-error" role="alert"><CircleAlert size={17} /><div>
      <strong>The overview numbers are unavailable</strong><span>{error}</span></div>
      <button onClick={() => void load(days)}>Retry</button></div>}

    {!data ? <div className="ov-tiles" aria-label="Loading overview">{Array.from({ length: 8 }, (_, i) => <div className="ov-tile ov-skeleton" key={i} />)}</div>
      : <Dashboard data={data} />}
  </div>;
}

function Dashboard({ data }: { data: OverviewSnapshot }) {
  const { devices, chats, operations, approvals, featureRequests, health, updates } = data;
  const range = `last ${data.days} day${data.days === 1 ? "" : "s"}`;
  const solvedShare = percent(chats.solved, chats.started);
  const workedShare = percent(operations.worked, operations.runs);
  const oldestApproval = approvals.oldestPendingUtc ? ago(approvals.oldestPendingUtc).replace(" ago", "") : null;

  return <>
    <section className="ov-tiles" aria-label="Key numbers">
      <Tile icon={MonitorSmartphone} href="/agents/" label="PCs online" value={`${devices.online}`} of={devices.enrolled}
        note={devices.offline ? `${devices.offline} offline${devices.notSeen7Days ? `, ${devices.notSeen7Days} not seen for 7+ days` : ""}` : "Every PC is connected"}
        tone={devices.notSeen7Days ? "warning" : undefined} />
      <Tile icon={LifeBuoy} href="/support/" label={`Chats, ${range}`} value={compact(chats.started)}
        note={`${chats.openNow} open now`} trend={chats.daily.map(day => dayTotal(day, ["solved", "escalated", "open"]))} />
      <Tile icon={BadgeCheck} label="Solved by the agent" value={solvedShare == null ? "—" : `${solvedShare}%`}
        note={chats.started ? `${chats.solved} of ${chats.started} chats, ${chats.escalated} sent to helpdesk` : "No chats in this range"}
        tone={solvedShare != null && solvedShare < 50 ? "warning" : undefined} />
      <Tile icon={AlertTriangle} href="/support/" label="Waiting for helpdesk" value={`${chats.escalatedNow}`}
        note={chats.escalatedNow ? "Chats the agent could not solve" : "Nothing handed over"} tone={chats.escalatedNow ? "serious" : "good"} />
      <Tile icon={Wrench} href="/capabilities/" label={`Fixes and checks run, ${range}`} value={compact(operations.runs)}
        note={workedShare == null ? "Nothing run yet" : `${workedShare}% worked, ${operations.failed} failed`}
        trend={operations.daily.map(day => dayTotal(day, ["worked", "failed", "blocked"]))}
        tone={operations.failed > operations.worked && operations.runs > 0 ? "serious" : undefined} />
      <Tile icon={ClipboardCheck} href="/approvals/" label="Approvals waiting" value={`${approvals.pending}`}
        note={approvals.pending ? `Oldest waits ${oldestApproval}` : "Nothing to approve"} tone={approvals.pending ? "warning" : "good"} />
      <Tile icon={HeartPulse} href="/health/" label="Health checks failing" value={`${health.failed}`}
        note={health.rules ? `${health.passed} passed, ${health.fixed} fixed, ${health.errors} could not check` : "No health rules yet"}
        tone={health.failed ? "serious" : health.rules ? "good" : undefined} />
      <Tile icon={Lightbulb} href="/feature-requests/" label="New feature requests" value={`${featureRequests.new}`}
        note={`${featureRequests.inRange} asked in the ${range}, ${featureRequests.total} in all`} />
      {data.problems && <Tile icon={Bug} href="/problems/" label="Agent problems" value={`${data.problems.open}`}
        note={`${data.problems.newInRange} new in the ${range}`} tone={data.problems.open ? "serious" : "good"} />}
    </section>

    <div className="ov-grid ov-grid-wide">
      <Card title="Chats per day" sub={`Each chat by what happened to it, ${range}`} href="/support/" link="Open helpdesk">
        {chats.started ? <DailyColumns days={chats.daily} series={CHAT_SERIES} label="Chats per day" unit="chats" />
          : <Empty text="No chats in this range yet." />}
        <p className="ov-footnote">The support agent ran {compact(chats.agentRuns)} time{chats.agentRuns === 1 ? "" : "s"}
          {chats.agentRunsFailed ? `, ${chats.agentRunsFailed} of them stopped with an error` : ""}.</p>
      </Card>
      <Card title="PCs" sub={`${devices.enrolled} enrolled, ${devices.seenLast24Hours} seen in the last 24 hours`} href="/devices/" link="Open devices">
        <h3 className="ov-subhead">Connection</h3>
        <SegmentedBar label="Online and offline PCs" parts={[
          { key: "online", label: "Online", color: "var(--ov-good)", count: devices.online },
          { key: "offline", label: "Offline", color: "var(--ov-none)", count: devices.offline }]} />
        <h3 className="ov-subhead">Windows version</h3>
        <SegmentedBar label="Windows versions" parts={devices.windows.map(item => ({ ...item, color: WINDOWS_COLORS[item.key] ?? "var(--ov-none)" }))} />
        <h3 className="ov-subhead">Agent version</h3>
        {devices.agentVersions.length ? <RankedBars label="PCs per agent version"
          items={devices.agentVersions.slice(0, 5).map(item => ({ key: item.key, label: item.label, value: item.count }))} />
          : <Empty text="No PCs yet." small />}
      </Card>
    </div>

    <div className="ov-grid ov-grid-wide">
      <Card title="Fixes and checks per day" sub={`Catalogued operations on PCs, ${range}`} href="/capabilities/" link="Open capabilities">
        {operations.runs ? <DailyColumns days={operations.daily} series={FIX_SERIES} label="Operations per day" unit="operations" />
          : <Empty text="No operations ran in this range." />}
        {operations.bySource.length > 0 && <p className="ov-footnote">Started by: {operations.bySource.map(item => `${item.label} ${item.count}`).join(", ")}.</p>}
      </Card>
      <Card title="Most used operations" sub={range}>
        {operations.top.length ? <div className="ov-table-wrap"><table className="ov-table">
          <thead><tr><th>Operation</th><th className="num">Runs</th><th>Worked</th><th className="num">Failed</th></tr></thead>
          <tbody>{operations.top.map(item => <tr key={item.id}><td><span className="ov-cell-title">{item.name}</span><code className="ov-cell-id">{item.id}</code></td>
            <td className="num">{item.runs}</td><td><SuccessMeter worked={item.worked} failed={item.failed} total={item.runs} /></td>
            <td className={`num${item.failed ? " danger-text" : ""}`}>{item.failed}</td></tr>)}</tbody></table></div>
          : <Empty text="Nothing ran yet." />}
      </Card>
    </div>

    <div className="ov-grid ov-grid-three">
      <Card title="Repair packages" sub={`BAT and EXE packages the agent tried, ${range}`} href="/files/" link="Open packages" icon={Package}>
        {data.packages.length ? <div className="ov-table-wrap"><table className="ov-table">
          <thead><tr><th>Package</th><th className="num">Tries</th><th>Worked</th></tr></thead>
          <tbody>{data.packages.map(item => <tr key={item.artifactId}><td><code className="ov-cell-id">{item.artifactId}</code><small className="ov-cell-note">{ago(item.lastTriedUtc)}</small></td>
            <td className="num">{item.tries}</td><td><SuccessMeter worked={item.worked} failed={item.failed} total={item.tries} /></td></tr>)}</tbody></table></div>
          : <Empty text="No package was tried in this range." />}
      </Card>
      <Card title="Health rules" sub="Latest result on each PC" href="/health/" link="Open health rules" icon={HeartPulse}>
        {health.byRule.length ? <>
          <Legend series={[{ key: "p", label: "Passed", color: "var(--ov-good)" }, { key: "x", label: "Fixed", color: "var(--ov-series-1)" },
            { key: "f", label: "Failed", color: "var(--ov-critical)" }, { key: "e", label: "Could not check", color: "var(--ov-none)" }]} />
          <ul className="ov-rule-list">{health.byRule.map(rule => {
            const total = rule.passed + rule.fixed + rule.failed + rule.errors;
            return <li key={rule.ruleId}><span className="ov-rule-name"><strong dir="auto">{rule.name}</strong>
              <small>{rule.enabled ? `${total} PC${total === 1 ? "" : "s"}, checked ${ago(rule.lastCheckedUtc)}` : "Switched off"}</small></span>
              <span className="ov-minibar" title={`Passed ${rule.passed}, fixed ${rule.fixed}, failed ${rule.failed}, could not check ${rule.errors}`}>
                {total === 0 ? <span className="ov-minibar-empty" /> : <>
                  {rule.passed > 0 && <span style={{ flexGrow: rule.passed, background: "var(--ov-good)" }} />}
                  {rule.fixed > 0 && <span style={{ flexGrow: rule.fixed, background: "var(--ov-series-1)" }} />}
                  {rule.failed > 0 && <span style={{ flexGrow: rule.failed, background: "var(--ov-critical)" }} />}
                  {rule.errors > 0 && <span style={{ flexGrow: rule.errors, background: "var(--ov-none)" }} />}</>}
              </span>
              <b className={rule.failed ? "danger-text" : undefined}>{rule.failed ? `${rule.failed} failed` : total ? "OK" : "—"}</b></li>;
          })}</ul></> : <Empty text="No health rules yet." />}
      </Card>
      <Card title="Agent updates" sub="Last update result per PC" href="/agent-updates/" link="Open agent updates" icon={CloudDownload}>
        {updates.byStatus.length ? <SegmentedBar label="Agent update results" parts={updates.byStatus.map(item => ({
          key: item.key, label: UPDATE_LABELS[item.key] ?? item.label, count: item.count,
          color: item.key === "Succeeded" ? "var(--ov-good)" : item.key === "RolledBack" ? "var(--ov-serious)"
            : item.key === "Failed" ? "var(--ov-critical)" : "var(--ov-series-1)" }))} /> : <Empty text="No update has run yet." small />}
        {updates.problems.length > 0 && <><h3 className="ov-subhead">Needs a look ({updates.rolledBackInRange} rolled back in the {range})</h3>
          <ul className="ov-list">{updates.problems.map(item => <li key={item.agentId}>
            <span><strong>{item.machineName}</strong><small>{UPDATE_LABELS[item.status] ?? item.status}: {item.fromVersion ?? "?"} to {item.toVersion ?? "?"}{item.message ? `, ${item.message}` : ""}</small></span>
            <time>{ago(item.reportedAtUtc)}</time></li>)}</ul></>}
      </Card>
    </div>

    <div className="ov-grid ov-grid-four">
      <Card title="Approvals waiting" sub={approvals.decidedInRange.length ? `Decided in the ${range}: ${approvals.decidedInRange.map(item => `${item.label} ${item.count}`).join(", ")}` : "Steps that wait for a helpdesk member"}
        href="/approvals/" link="Open approvals" icon={ClipboardCheck}>
        {approvals.waiting.length ? <ul className="ov-list">{approvals.waiting.map(item => <li key={item.approvalId}>
          <span><strong dir="auto">{item.title}</strong><small>{item.kind}</small></span><time>{ago(item.createdAtUtc)}</time></li>)}</ul>
          : <Empty text="Nothing waits for approval." small good />}
      </Card>
      <Card title="Sent to helpdesk" sub="Newest chats the agent handed over" href="/support/" link="Open helpdesk" icon={LifeBuoy}>
        {chats.recentEscalations.length ? <ul className="ov-list">{chats.recentEscalations.map(item => <li key={item.conversationId}>
          <span><strong>{item.machineName}</strong><small dir="auto">{item.reason || item.userName}</small></span><time>{ago(item.escalatedAtUtc)}</time></li>)}</ul>
          : <Empty text="No chat is waiting for the helpdesk." small good />}
      </Card>
      <Card title="Feature requests" sub="Things users asked for that the agent cannot do yet" href="/feature-requests/" link="Open requests" icon={Lightbulb}>
        {featureRequests.newest.length ? <ul className="ov-list">{featureRequests.newest.map(item => <li key={item.requestId}>
          <span><strong dir="auto">{item.summary}</strong><small>{item.status}</small></span><time>{ago(item.createdAtUtc)}</time></li>)}</ul>
          : <Empty text="No requests yet." small />}
      </Card>
      <Card title="Learning" sub={`Lessons from solved chats and skills used, ${range}`} href="/lessons/" link="Open lessons" icon={GraduationCap}>
        <div className="ov-chips">{data.lessons.length ? data.lessons.map(item => <span key={item.key} className="ov-chip">{item.label}<b>{item.count}</b></span>)
          : <span className="muted">No lessons yet.</span>}</div>
        {data.skills.length ? <RankedBars label="Skills used" items={data.skills.slice(0, 5).map(item => ({
          key: item.name, label: item.name, value: item.uses, note: `${item.solved} solved, ${item.escalated} to helpdesk` }))} />
          : <Empty text="No skill was used in this range." small />}
      </Card>
    </div>

    {devices.longestOffline.length > 0 && <div className="ov-grid">
      <Card title="Offline the longest" sub="PCs that have not connected for a while" href="/agents/" link="Open endpoints" icon={MonitorSmartphone}>
        <ul className="ov-list ov-list-inline">{devices.longestOffline.map(item => <li key={item.agentId}>
          <span><strong>{item.machineName}</strong><small><code>{item.agentId.slice(0, 8)}</code></small></span><time>last seen {ago(item.lastSeenUtc)}</time></li>)}</ul>
      </Card>
    </div>}
  </>;
}

function Tile({ icon: Icon, label, value, of, note, href, trend, tone }: {
  icon: LucideIcon; label: string; value: string; of?: number; note: string; href?: string; trend?: number[];
  tone?: "good" | "warning" | "serious" }) {
  const body = <>
    <span className="ov-tile-label"><Icon size={14} aria-hidden="true" />{label}{href && <ArrowUpRight size={12} className="ov-tile-go" aria-hidden="true" />}</span>
    <span className="ov-tile-value">{value}{of != null && <small> / {of}</small>}</span>
    <span className="ov-tile-note">{tone && <i className={`ov-dot ${tone}`} aria-hidden="true" />}{note}</span>
    {trend && trend.some(v => v > 0) && <Sparkline values={trend} label={`${label} per day`} />}
  </>;
  return href ? <Link className={`ov-tile${tone ? ` is-${tone}` : ""}`} href={href}>{body}</Link>
    : <div className={`ov-tile${tone ? ` is-${tone}` : ""}`}>{body}</div>;
}

function Card({ title, sub, href, link, icon: Icon, children }: {
  title: string; sub?: string; href?: string; link?: string; icon?: LucideIcon; children: React.ReactNode }) {
  return <section className="ov-card">
    <header><div>{Icon && <Icon size={15} aria-hidden="true" />}<h2>{title}</h2></div>{href && <Link href={href}>{link ?? "Open"}</Link>}</header>
    {sub && <p className="ov-card-sub">{sub}</p>}
    <div className="ov-card-body">{children}</div>
  </section>;
}

function Empty({ text, small, good }: { text: string; small?: boolean; good?: boolean }) {
  return <div className={`ov-empty${small ? " is-small" : ""}${good ? " is-good" : ""}`}>{text}</div>;
}
