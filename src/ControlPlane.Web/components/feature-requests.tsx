"use client";

import { useAuth } from "@/components/auth-gate";
import { CircleAlert, Lightbulb, RefreshCw } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useMemo, useState } from "react";
import { getFeatureRequests, setFeatureRequestStatus } from "@/lib/control-plane";
import { featureRequestCounts, featureStatuses } from "@/lib/feature-requests";
import type { FeatureRequest } from "@/lib/types";

/** Requests users made for abilities the support agent does not have yet. */
export function FeatureRequests() {
  const [items, setItems] = useState<FeatureRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [filter, setFilter] = useState<FeatureRequest["status"] | "All">("All");
  const [busy, setBusy] = useState<string>();

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setItems(await getFeatureRequests());
      setError(undefined);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Feature requests could not be loaded.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0);
    return () => window.clearTimeout(initial);
  }, [refresh]);

  const counts = useMemo(() => featureRequestCounts(items), [items]);
  const visible = items.filter(item => filter === "All" || item.status === filter);

  const canTriage = useAuth().can("feature-requests.triage");

  const changeStatus = async (item: FeatureRequest, status: FeatureRequest["status"]) => {
    setBusy(item.requestId);
    try {
      await setFeatureRequestStatus(item.requestId, status);
      setItems(current => current.map(entry => entry.requestId === item.requestId
        ? { ...entry, status } : entry));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "The status could not be changed.");
    } finally {
      setBusy(undefined);
    }
  };

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="features-title">
      <div><div className="file-heading-mark"><Lightbulb size={17} /></div><div>
        <h1 id="features-title">Feature requests</h1>
        <p>Things users asked for that the support agent cannot do yet. Each was saved only after the user agreed.</p></div></div>
      <button className="secondary-button" onClick={() => void refresh()} disabled={loading}>
        <RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Feature requests are unavailable</strong><span>{error}</span></div>
      <button onClick={() => void refresh()}>Retry</button></div>}

    <div className="support-filter feature-filter" aria-label="Filter by status">
      {(["All", ...featureStatuses] as const).map(option => <button key={option} type="button"
        className={filter === option ? "selected" : ""} onClick={() => setFilter(option)}>
        {option}<span>{option === "All" ? items.length : counts[option]}</span></button>)}
    </div>

    {loading ? <div className="file-list" aria-label="Loading feature requests"><div className="file-skeleton" /></div>
      : visible.length === 0 ? <section className="file-empty"><span className="empty-icon"><Lightbulb size={23} /></span>
        <h2>No feature requests</h2><p>When a user asks for something the agent cannot do and agrees to save it, it appears here.</p></section>
      : <section className="file-list" aria-label="Feature requests">{visible.map(item =>
        <article key={item.requestId} className="file-row feature-row">
          <div className="file-row-main">
            <div className="file-row-title"><strong>{item.summary}</strong>
              <span className={`ticket-status ${item.status.toLowerCase()}`}>{item.status}</span></div>
            {item.userWords && <p className="file-description" dir="auto">“{item.userWords}”</p>}
            <p className="file-compatibility">{item.userName || "Endpoint user"} · {item.machineName || item.agentId || "Helpdesk"} ·
              {" "}{new Date(item.createdAtUtc).toLocaleString()}
              {item.conversationId && <>{" "}·{" "}
              <Link href={`/support/?conversation=${encodeURIComponent(item.conversationId)}`}>Conversation</Link></>}</p>
          </div>
          <label className="feature-status"><span className="sr-only">Status</span>
            <select value={item.status} disabled={busy === item.requestId || !canTriage}
              title={canTriage ? undefined : "Reviewers and admins change the status."}
              onChange={event => void changeStatus(item, event.target.value as FeatureRequest["status"])}>
              {featureStatuses.map(status => <option key={status}>{status}</option>)}
            </select></label>
        </article>)}</section>}
  </div>;
}
