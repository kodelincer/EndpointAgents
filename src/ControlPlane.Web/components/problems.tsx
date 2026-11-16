"use client";

import { Check, CircleAlert, ClipboardCopy, Download, FileArchive, RefreshCw, RotateCcw, Siren } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";
import { downloadLogs, formatBytes, getFleet, getProblem, getProblems, groupProblems, kindClass, kindLabel, markFixed, pcFacts, reopenProblem, requestLogs,
  type FleetPage, type FleetPc, type GroupBy, type LogBundle, type ProblemDetail, type ProblemFilter, type ProblemPage, type ProblemRow } from "@/lib/problems";

const REFRESH_MS = 30000;
const when = (value?: string | null) => value ? new Date(value).toLocaleString() : "never";

/**
 * What went wrong in our own agent on the PCs, grouped so the team knows what to fix in the next
 * version, and which PCs have gone quiet or stayed behind. Collecting and showing uses no AI.
 */
export function Problems() {
  const { can } = useAuth();
  const [filter, setFilter] = useState<ProblemFilter>({ status: "Open" });
  const [groupBy, setGroupBy] = useState<GroupBy>("none");
  const [page, setPage] = useState<ProblemPage>();
  const [fleet, setFleet] = useState<FleetPage>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [open, setOpen] = useState<string>();

  const refresh = useCallback(async () => {
    try {
      const [problems, pcs] = await Promise.all([getProblems(filter), getFleet()]);
      setPage(problems); setFleet(pcs); setError(undefined);
    } catch (reason) { setError(reason instanceof Error ? reason.message : "Problems could not be loaded."); }
    finally { setLoading(false); }
  }, [filter]);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0);
    const timer = window.setInterval(() => void refresh(), REFRESH_MS);
    return () => { window.clearTimeout(initial); window.clearInterval(timer); };
  }, [refresh]);

  if (!can("problems.view"))
    return <div className="file-page"><section className="file-empty"><h2>Reviewers and admins only</h2>
      <p>Ask an admin if you need to see problem reports.</p></section></div>;

  const set = (key: keyof ProblemFilter) => (event: { target: { value: string } }) =>
    setFilter(current => ({ ...current, [key]: event.target.value }));
  const groups = groupProblems(page?.problems ?? [], groupBy);
  const dropped = (page?.storage.droppedByLimit ?? 0) + (page?.storage.droppedDiskFull ?? 0);

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="problems-title">
      <div><div className="file-heading-mark"><Siren size={17} /></div><div>
        <h1 id="problems-title">{pageName("/problems/")}</h1></div></div>
      <button className="secondary-button" onClick={() => { setLoading(true); void refresh(); }} disabled={loading}>
        <RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>
    <PageIntro route="/problems/" />

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Problems are unavailable</strong><span>{error}</span></div>
      <button onClick={() => void refresh()}>Retry</button></div>}
    {page?.logServer.down && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>The log server is not reachable</strong>
        <span>{page.logServer.lastError} Reports are still saved here. {page.logServer.waiting} copies are waiting
          {page.logServer.dropped > 0 && <>, {page.logServer.dropped} were dropped</>}.</span></div></div>}
    {page?.storage.diskLow && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>This server&apos;s disk is nearly full</strong><span>New problem reports are not being saved until there is room.</span></div></div>}

    <section className="problem-filters" aria-label="Filters">
      <label>Status<select value={filter.status ?? ""} onChange={set("status")}>
        <option value="">All</option><option value="Open">Open</option><option value="Fixed">Fixed</option></select></label>
      <label>Kind<select value={filter.kind ?? ""} onChange={set("kind")}><option value="">All</option>
        {page?.filters.kinds.map(kind => <option key={kind} value={kind}>{kindLabel(kind)}</option>)}</select></label>
      <label>Version<select value={filter.version ?? ""} onChange={set("version")}><option value="">All</option>
        {page?.filters.versions.map(value => <option key={value}>{value}</option>)}</select></label>
      <label>Windows<select value={filter.os ?? ""} onChange={set("os")}><option value="">All</option>
        {page?.filters.windows.map(value => <option key={value}>{value}</option>)}</select></label>
      <label>Model<select value={filter.model ?? ""} onChange={set("model")}><option value="">All</option>
        {page?.filters.models.map(value => <option key={value}>{value}</option>)}</select></label>
      <label>PC<input value={filter.pc ?? ""} onChange={set("pc")} placeholder="Name" /></label>
      <label>Group by<select value={groupBy} onChange={event => setGroupBy(event.target.value as GroupBy)}>
        <option value="none">Nothing</option><option value="kind">Kind</option><option value="version">Version</option>
        <option value="windows">Windows</option><option value="model">Model</option></select></label>
    </section>

    {page && groups.length === 0 && <section className="file-empty"><span className="empty-icon"><Check size={23} /></span>
      <h2>No problems here</h2><p>Nothing matches these filters. Reports appear here as soon as a PC sends one.</p></section>}
    {groups.map(group => <div key={group.title}>
      {group.title && <h2 className="section-title">{group.title} <span className="muted">· {group.rows.length}</span></h2>}
      <section className="file-list" aria-label={group.title || "Problems"}>
        {group.rows.map(row => <ProblemCard key={row.fingerprint} row={row} expanded={open === `${group.title}/${row.fingerprint}`}
          onToggle={() => setOpen(current => current === `${group.title}/${row.fingerprint}` ? undefined : `${group.title}/${row.fingerprint}`)}
          onChanged={refresh} />)}
      </section></div>)}

    {page && <p className="file-compatibility problem-storage">Reports use {formatBytes(page.storage.databaseBytes)} in their own file.
      {dropped > 0 && <> {dropped} were not saved (over the hourly limit of a PC or a full disk).</>}</p>}

    <h2 className="section-title">PCs that went quiet <span className="muted">· no contact for over a day · {fleet?.quiet.length ?? 0}</span></h2>
    <PcList pcs={fleet?.quiet} empty="Every PC has been in contact in the last day." detail={pc => `Last contact ${when(pc.lastContactUtc)}`} />

    <h2 className="section-title">PCs on an old version <span className="muted">· {fleet?.oldVersion.length ?? 0}</span></h2>
    <PcList pcs={fleet?.oldVersion} empty="No PC is behind." detail={pc => `On ${pc.version ?? "?"}`} />

    {can("devices.manage") && <GetLogs fleet={fleet} onChanged={refresh} onError={setError} />}
  </div>;
}

function ProblemCard({ row, expanded, onToggle, onChanged }: {
  row: ProblemRow; expanded: boolean; onToggle: () => void; onChanged: () => Promise<void>;
}) {
  const [detail, setDetail] = useState<ProblemDetail>();
  const [version, setVersion] = useState("");
  const [message, setMessage] = useState<string>();

  useEffect(() => {
    if (!expanded) return;
    let cancelled = false;
    getProblem(row.fingerprint).then(result => { if (!cancelled) setDetail(result); })
      .catch(reason => { if (!cancelled) setMessage(reason instanceof Error ? reason.message : "The details could not be loaded."); });
    return () => { cancelled = true; };
  }, [expanded, row.fingerprint, row.count]);

  const act = async (action: () => Promise<void>) => {
    setMessage(undefined);
    try { await action(); await onChanged(); }
    catch (reason) { setMessage(reason instanceof Error ? reason.message : "That did not work."); }
  };
  const copy = async () => {
    if (!detail) return;
    try { await navigator.clipboard.writeText(detail.developerText); setMessage("Copied."); }
    catch { setMessage("The browser did not allow copying."); }
  };

  return <article className="file-row"><div className="file-row-main">
    <div className="file-row-title"><strong dir="auto">{row.title}</strong>
      <span className={`ticket-status ${kindClass(row.kind)}`}>{kindLabel(row.kind)}</span>
      {row.status === "Fixed" && <span className="ticket-status resolved">Fixed in {row.fixedInVersion}</span>}
      {row.reopenedAtUtc && row.status === "Open" && <span className="ticket-status escalated">Came back</span>}
      {row.onlyOn.map(note => <span key={note} className="ticket-status new">{note}</span>)}
    </div>
    <p className="file-compatibility">{row.count} time(s) on {row.pcs} PC(s) · {row.source}
      {row.versions.length > 0 && <> · version {row.versions.join(", ")}</>}
      {" "}· first {when(row.firstSeenUtc)} · last {when(row.lastSeenUtc)}</p>
    <p className="file-compatibility">
      {row.windows.map(item => `${item.label} (${item.pcs})`).join(", ")}
      {row.windows.length > 0 && row.models.length > 0 && " · "}
      {row.models.map(item => `${item.label} (${item.pcs})`).join(", ")}</p>
    {row.conversationId && <p className="file-compatibility">
      <Link href={`/support/?conversation=${encodeURIComponent(row.conversationId)}`}>Open the conversation</Link></p>}
    {expanded && detail && <>
      {detail.problem.stack && <pre className="problem-stack">{detail.problem.stack}</pre>}
      <div className="problem-table"><table>
        <thead><tr><th>PC</th><th>Version</th><th>Windows</th><th>Model</th><th>Times</th><th>Last seen</th></tr></thead>
        <tbody>{detail.occurrences.map(item => <tr key={`${item.agentId}/${item.agentVersion}`}>
          <td>{item.machineName || item.agentId.slice(0, 8)}</td><td>{item.agentVersion ?? "?"}</td>
          <td>{[item.osName, item.osEdition, item.osVersion, item.osBuild].filter(Boolean).join(" ")}</td>
          <td>{[item.manufacturer, item.model].filter(Boolean).join(" ")}</td>
          <td>{item.count}</td><td>{when(item.lastSeenUtc)}</td></tr>)}</tbody>
      </table></div>
      <div className="problem-actions">
        {row.status === "Open"
          ? <label>Fixed in version <input value={version} onChange={event => setVersion(event.target.value)} placeholder="0.4.0" />
            <button className="secondary-button" disabled={!version.trim()}
              onClick={() => void act(() => markFixed(row.fingerprint, version.trim()))}><Check size={14} />Mark fixed</button></label>
          : <button className="secondary-button" onClick={() => void act(() => reopenProblem(row.fingerprint))}><RotateCcw size={14} />Open again</button>}
        <button className="secondary-button" onClick={() => void copy()}><ClipboardCopy size={14} />Copy for developer</button>
      </div>
    </>}
    {message && <p className="file-compatibility" role="status">{message}</p>}
  </div>
  <div className="file-row-actions">
    <button className="secondary-button" aria-expanded={expanded} onClick={onToggle}>{expanded ? "Hide" : "Details"}</button>
  </div></article>;
}

function PcList({ pcs, empty, detail }: { pcs?: FleetPc[]; empty: string; detail: (pc: FleetPc) => string }) {
  if (!pcs) return null;
  if (pcs.length === 0) return <p className="file-compatibility">{empty}</p>;
  return <section className="file-list">{pcs.map(pc => <article key={pc.agentId} className="file-row"><div className="file-row-main">
    <div className="file-row-title"><strong>{pc.machineName || pc.agentId.slice(0, 8)}</strong>
      {pc.online ? <span className="ticket-status resolved">Online</span> : <span className="muted">offline</span>}</div>
    <p className="file-compatibility">{detail(pc)}{pcFacts(pc) && ` · ${pcFacts(pc)}`}</p>
  </div></article>)}</section>;
}

/** "Get logs": a fixed collection from one PC, for admins. It takes no script and no path. */
function GetLogs({ fleet, onChanged, onError }: {
  fleet?: FleetPage; onChanged: () => Promise<void>; onError: (message: string) => void;
}) {
  const [agentId, setAgentId] = useState("");
  const [busy, setBusy] = useState(false);
  const online = (fleet?.all ?? []).filter(pc => pc.online);

  const collect = async () => {
    setBusy(true);
    try { await requestLogs(agentId); await onChanged(); }
    catch (reason) { onError(reason instanceof Error ? reason.message : "The logs could not be collected."); }
    finally { setBusy(false); }
  };
  const download = async (bundle: LogBundle) => {
    try { await downloadLogs(bundle); }
    catch (reason) { onError(reason instanceof Error ? reason.message : "The logs could not be downloaded."); }
  };

  return <>
    <h2 className="section-title">Get logs from one PC</h2>
    <section className="file-list"><article className="file-row"><div className="file-row-main">
      <p className="file-compatibility">Collects our own logs and our entries from the Windows event log on that PC, with personal
        data cut on the PC, at most 10 MB. Kept here for 30 days.</p>
      <div className="problem-actions">
        <label>PC <select value={agentId} onChange={event => setAgentId(event.target.value)}>
          <option value="">Choose an online PC</option>
          {online.map(pc => <option key={pc.agentId} value={pc.agentId}>{pc.machineName || pc.agentId.slice(0, 8)}</option>)}
        </select></label>
        <button className="primary-button" disabled={!agentId || busy} onClick={() => void collect()}>
          <FileArchive size={14} />{busy ? "Collecting…" : "Get logs"}</button>
      </div>
    </div></article>
    {fleet?.bundles.map(bundle => <article key={bundle.bundleId} className="file-row"><div className="file-row-main">
      <div className="file-row-title"><strong>{bundle.machineName}</strong>
        <span className={`ticket-status ${bundle.status === "Ready" ? "resolved" : bundle.status === "Failed" ? "escalated" : "open"}`}>{bundle.status}</span></div>
      <p className="file-compatibility">Asked by {bundle.requestedBy} · {when(bundle.requestedAtUtc)}
        {bundle.status === "Ready" && <> · {formatBytes(bundle.sizeBytes)}</>}{bundle.message && <> · {bundle.message}</>}</p>
    </div>
    {bundle.status === "Ready" && <div className="file-row-actions">
      <button className="secondary-button" onClick={() => void download(bundle)}><Download size={14} />Download</button></div>}
    </article>)}</section>
  </>;
}
