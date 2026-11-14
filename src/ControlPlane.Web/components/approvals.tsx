"use client";

import { Check, CircleAlert, ClipboardCheck, RefreshCw, X } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { approvalDetails } from "@/lib/software";
import { approveRequest, declineRequest, getApprovals, pendingSummary, type ApprovalRequest } from "@/lib/capabilities";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";

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
    if (approve && !window.confirm(item.kind === "person" ? `Mark "${item.title}" as done? The user is told in their chat.` : `Run "${item.title}" on the user's PC now?`)) return;
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
        <h1 id="approvals-title">{pageName("/approvals/")}</h1></div></div>
      <button className="secondary-button" onClick={() => { setLoading(true); void refresh(); }} disabled={loading}>
        <RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>
    <PageIntro route="/approvals/" />

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Approvals are unavailable</strong><span>{error}</span></div>
      <button onClick={() => void refresh()}>Retry</button></div>}

    <h2 className="section-title">Waiting ({summary.count}){summary.oldestMinutes !== null && summary.oldestMinutes > 0
      && <span className="muted"> · oldest {summary.oldestMinutes} min</span>}</h2>
    {loading && items.length === 0 ? <div className="file-list" aria-label="Loading approvals"><div className="file-skeleton" /></div>
      : pending.length === 0 ? <section className="file-empty"><span className="empty-icon"><ClipboardCheck size={23} /></span>
        <h2>Nothing to approve</h2><p>When the agent asks to remove software, run another step that needs approval, or needs a person (unlock an account, a BitLocker key), it appears here.</p></section>
      : <section className="file-list" aria-label="Waiting for approval">{pending.map(item =>
        <article key={item.approvalId} className="file-row">
          <div className="file-row-main">
            <div className="file-row-title"><strong dir="auto">{item.title}</strong>
              <span className="ticket-status">{item.kind === "person" ? "Helpdesk task" : item.kind === "package" ? "Package" : "Action"}</span><code>{item.itemId}</code></div>
            {item.kind === "person" && <p className="file-compatibility">Do this yourself (for example in Active Directory), then mark it done. Nothing runs on the PC. Never write a key or password in the note: the note appears in the user&apos;s chat.</p>}
            {item.reason && <p className="file-description" dir="auto">{item.reason}</p>}
            <InstallDetails argumentsJson={item.argumentsJson} />
            <p className="file-compatibility">PC {item.agentId.slice(0, 8)} · {new Date(item.createdAtUtc).toLocaleString()}
              {item.conversationId && <>{" "}·{" "}
                <Link href={`/support/?conversation=${encodeURIComponent(item.conversationId)}`}>Conversation</Link></>}</p>
            <input className="approval-note" value={notes[item.approvalId] ?? ""} dir="auto" aria-label="Note to the user"
              placeholder={item.kind === "person" ? "Optional note for the user (no keys or passwords)" : "Optional note (shown to the user when declined)"}
              onChange={event => setNotes({ ...notes, [item.approvalId]: event.target.value })} />
          </div>
          <div className="file-row-actions">
            <button className="primary-button" disabled={busy === item.approvalId} onClick={() => void decide(item, true)}>
              <Check size={14} />{busy === item.approvalId ? "Saving…" : item.kind === "person" ? "Mark done" : "Approve and run"}</button>
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

/** Requirement results the PC reported for a software install, checked before this approval. */
function InstallDetails({ argumentsJson }: { argumentsJson: string }) {
  const details = approvalDetails(argumentsJson);
  if (!details) return null;
  return <div className="approval-install">
    <p className="file-compatibility"><strong>{details.software}{details.version ? ` ${details.version}` : ""}</strong>
      {details.licensed && <span className="ticket-status">Licensed: check a licence is free</span>}
      {details.requirementsMet === true && " · this PC meets every requirement"}</p>
    {details.requirements && details.requirements.length > 0 && <ul className="approval-requirements">
      {details.requirements.map(result => <li key={result.rule} className={result.passed ? "passed" : "failed"}>
        {result.passed ? "✓" : "✗"} {result.rule}: needs {result.needed}, PC has {result.found}</li>)}</ul>}
    {details.installFirst && details.installFirst.length > 0 &&
      <p className="file-compatibility">Installed first: {details.installFirst.join(", ")}</p>}
  </div>;
}
