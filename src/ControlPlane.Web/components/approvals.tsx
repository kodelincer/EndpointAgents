"use client";

import { Check, CircleAlert, ClipboardCheck, RefreshCw, X } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { approveRequest, declineRequest, getApprovals, pendingSummary, type ApprovalRequest } from "@/lib/capabilities";

const REFRESH_MS = 15000;

/** Steps the support agent asked for that wait for a helpdesk member (software removal and other approval-tier actions). */
export function Approvals() {
  const [items, setItems] = useState<ApprovalRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string>();
  const [notes, setNotes] = useState<Record<string, string>>({});

  const refresh = useCallback(async () => {
    try { setItems(await getApprovals()); setError(undefined); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Approvals could not be loaded."); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0);
    const timer = window.setInterval(() => void refresh(), REFRESH_MS);
    return () => { window.clearTimeout(initial); window.clearInterval(timer); };
  }, [refresh]);

  const decide = async (item: ApprovalRequest, approve: boolean) => {
    if (approve && !window.confirm(`Run "${item.title}" on the user's PC now?`)) return;
    setBusy(item.approvalId);
    try {
      await (approve ? approveRequest : declineRequest)(item.approvalId, notes[item.approvalId] ?? "");
      await refresh();
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The decision was not saved."); }
    finally { setBusy(undefined); }
  };

  const pending = items.filter(item => item.status === "Pending");
  const decided = items.filter(item => item.status !== "Pending");
  const summary = pendingSummary(items);

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="approvals-title">
      <div><div className="file-heading-mark"><ClipboardCheck size={17} /></div><div>
        <h1 id="approvals-title">Approvals</h1>
        <p>Steps the support agent wants to run that need a helpdesk member first, such as removing software.
          Approving runs it on the user&apos;s PC with every normal check; the user sees the result in their chat either way.</p></div></div>
      <button className="secondary-button" onClick={() => { setLoading(true); void refresh(); }} disabled={loading}>
        <RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Approvals are unavailable</strong><span>{error}</span></div>
      <button onClick={() => void refresh()}>Retry</button></div>}

    <h2 className="section-title">Waiting ({summary.count}){summary.oldestMinutes !== null && summary.oldestMinutes > 0
      && <span className="muted"> · oldest {summary.oldestMinutes} min</span>}</h2>
    {loading && items.length === 0 ? <div className="file-list" aria-label="Loading approvals"><div className="file-skeleton" /></div>
      : pending.length === 0 ? <section className="file-empty"><span className="empty-icon"><ClipboardCheck size={23} /></span>
        <h2>Nothing to approve</h2><p>When the agent asks to remove software or run another step that needs approval, it appears here.</p></section>
      : <section className="file-list" aria-label="Waiting for approval">{pending.map(item =>
        <article key={item.approvalId} className="file-row">
          <div className="file-row-main">
            <div className="file-row-title"><strong dir="auto">{item.title}</strong>
              <span className="ticket-status">{item.kind === "package" ? "Package" : "Action"}</span><code>{item.itemId}</code></div>
            {item.reason && <p className="file-description" dir="auto">{item.reason}</p>}
            <p className="file-compatibility">PC {item.agentId.slice(0, 8)} · {new Date(item.createdAtUtc).toLocaleString()}
              {item.conversationId && <>{" "}·{" "}
                <Link href={`/support/?conversation=${encodeURIComponent(item.conversationId)}`}>Conversation</Link></>}</p>
            <input className="approval-note" value={notes[item.approvalId] ?? ""} dir="auto" aria-label="Note to the user"
              placeholder="Optional note (shown to the user when declined)"
              onChange={event => setNotes({ ...notes, [item.approvalId]: event.target.value })} />
          </div>
          <div className="file-row-actions">
            <button className="primary-button" disabled={busy === item.approvalId} onClick={() => void decide(item, true)}>
              <Check size={14} />{busy === item.approvalId ? "Running…" : "Approve and run"}</button>
            <button className="secondary-button" disabled={busy === item.approvalId} onClick={() => void decide(item, false)}>
              <X size={14} />Decline</button>
          </div>
        </article>)}</section>}

    {decided.length > 0 && <>
      <h2 className="section-title">Decided</h2>
      <section className="file-list" aria-label="Decided requests">{decided.slice(0, 50).map(item =>
        <article key={item.approvalId} className="file-row">
          <div className="file-row-main">
            <div className="file-row-title"><strong dir="auto">{item.title}</strong>
              <span className={`ticket-status ${item.status.toLowerCase()}`}>{item.status}</span></div>
            <p className="file-compatibility">{item.decidedBy ?? "helpdesk"} · {item.decidedAtUtc ? new Date(item.decidedAtUtc).toLocaleString() : ""}
              {item.note && <> · <span dir="auto">{item.note}</span></>}</p>
            {item.resultSummary && <p className="file-description">{item.resultSummary}</p>}
          </div>
        </article>)}</section>
    </>}
  </div>;
}
