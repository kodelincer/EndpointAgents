"use client";

import { Check, CircleAlert, Clipboard, Clock3, LifeBuoy, Search, Server, UserRound } from "lucide-react";
import { useMemo, useState } from "react";
import { useControlPlane } from "@/lib/control-plane-context";
import { connectionLabel } from "@/lib/support";
import type { SupportRequest } from "@/lib/types";

function relative(value: string) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

const ibsngStates = ["Unknown", "Disconnected", "Connecting", "Authenticating", "Connected", "Failed", "Inconsistent"];
const ibsngProtocols = ["Auto", "L2TP", "PPTP", "SSTP", "IKEv2"];

/** Live endpoint-originated support requests with diagnostic evidence. */
export function HelpdeskRequests() {
  const { supportRequests, loading, loadError, refresh } = useControlPlane();
  const [query, setQuery] = useState("");
  const [selectedId, setSelectedId] = useState<string>();
  const [copied, setCopied] = useState(false);
  const visible = useMemo(() => supportRequests.filter(request =>
    `${request.subject} ${request.messageText} ${request.agentId} ${request.machineName} ${request.userName}`
      .toLowerCase().includes(query.toLowerCase())).reverse(), [supportRequests, query]);
  const selected = supportRequests.find(request => request.requestId === selectedId) ?? visible[0];

  const copy = async (request: SupportRequest) => {
    await navigator.clipboard.writeText(`Request ${request.requestId}\nEndpoint: ${request.agentId}\nUser: ${request.userName}\nSubject: ${request.subject}\n\n${request.messageText}`);
    setCopied(true); window.setTimeout(() => setCopied(false), 1500);
  };

  return <main className="support-page">
    <header className="support-heading"><div><span className="support-heading-mark"><LifeBuoy size={19} /></span><div>
      <h1>Helpdesk requests</h1><p>Problems submitted by users through the Endpoint Agent Tray.</p>
    </div></div><button className="secondary-button" onClick={() => void refresh()}>Refresh</button></header>

    {loadError && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Helpdesk requests are unavailable</strong><span>{loadError}</span></div></div>}
    {loading ? <div className="support-skeleton"><div /><div /><div /></div> : supportRequests.length === 0 ?
      <section className="support-empty"><span className="empty-icon"><LifeBuoy size={23} /></span><h2>No helpdesk requests yet</h2><p>When a connected endpoint user submits a problem from the Tray, it appears here live with current connection evidence.</p></section> :
      <div className="support-workspace">
        <section className="support-inbox" aria-label="Helpdesk request inbox">
          <label className="support-search"><Search size={15} /><span className="sr-only">Search helpdesk requests</span><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search endpoint, user, or message" /></label>
          <div className="support-list">{visible.length === 0 ? <p className="support-no-match">No requests match this search.</p> : visible.map(request =>
            <button className={request.requestId === selected?.requestId ? "selected" : ""} key={request.requestId} onClick={() => setSelectedId(request.requestId)}>
              <span className="support-item-top"><strong>{request.subject}</strong><time>{relative(request.receivedAtUtc)}</time></span>
              <span className="support-item-meta"><Server size={12} />{request.machineName}<UserRound size={12} />{request.userName}</span>
              <span className="support-preview" dir={request.textDirection}>{request.messageText}</span>
            </button>)}</div>
        </section>
        {selected && <SupportDetail request={selected} copied={copied} onCopy={() => void copy(selected)} />}
      </div>}
  </main>;
}

function SupportDetail({ request, copied, onCopy }: { request: SupportRequest; copied: boolean; onCopy: () => void }) {
  const runtime = request.runtime;
  return <article className="support-detail">
    <header><div><span>Request {request.requestId.slice(0, 8)}</span><h2>{request.subject}</h2></div><button className="secondary-button" onClick={onCopy}>{copied ? <Check size={14} /> : <Clipboard size={14} />}{copied ? "Copied" : "Copy request"}</button></header>
    <div className="support-request-meta"><span><Clock3 size={14} />{new Date(request.receivedAtUtc).toLocaleString()}</span><span><Server size={14} />{request.agentId}</span><span><UserRound size={14} />{request.userName} · session {request.sessionId}</span></div>
    <section className="support-message"><h3>User message</h3><p dir={request.textDirection}>{request.messageText}</p></section>
    <section className="support-evidence"><h3>Endpoint evidence</h3><dl>
      <div><dt>Connection</dt><dd>{connectionLabel(runtime.connectionStatus)}</dd></div>
      <div><dt>Functions</dt><dd>{runtime.functionMode === 1 ? "Paused" : "Active"}</dd></div>
      <div><dt>Version</dt><dd><code>{runtime.version}</code></dd></div>
      <div><dt>Last connected</dt><dd>{runtime.lastConnectedAtUtc ? new Date(runtime.lastConnectedAtUtc).toLocaleString() : "Not recorded"}</dd></div>
      <div><dt>Last function</dt><dd>{runtime.lastCommandMethod ?? "None"}</dd></div>
      <div><dt>Last problem</dt><dd className={runtime.lastProblemMessage ? "problem" : ""}>{runtime.lastProblemMessage ?? "None reported"}</dd></div>
    </dl></section>
    {request.ibsngDiagnostics && <section className="support-ibsng"><h3>IBSng connection diagnosis</h3>
      <div className="ibsng-summary"><strong>{ibsngStates[request.ibsngDiagnostics.connectionState] ?? "Unknown"}</strong><span>{request.ibsngDiagnostics.serverHost} · {ibsngProtocols[request.ibsngDiagnostics.protocol] ?? "Auto"}</span><p>{request.ibsngDiagnostics.summary}</p></div>
      <div className="ibsng-checks">{request.ibsngDiagnostics.checks.map(check => <div key={check.name}><span className={`check-state ${check.status.toLowerCase()}`}>{check.status}</span><strong>{check.name}</strong><p>{check.summary}</p></div>)}</div>
      {request.ibsngDiagnostics.sanitizedLogLines.length > 0 && <details className="ibsng-log"><summary>Sanitized dialer Connection Log</summary><pre>{request.ibsngDiagnostics.sanitizedLogLines.join("\n")}</pre></details>}
    </section>}
    <section className="support-logs"><h3>Attached warnings and errors <span>{request.logExcerpts.length}</span></h3>
      {request.logExcerpts.length === 0 ? <p>No log excerpts were attached.</p> : <div>{request.logExcerpts.map((entry, index) =>
        <details key={`${entry.timestampUtc}-${index}`}><summary><span className={`log-level ${entry.level.toLowerCase()}`}>{entry.level}</span><time>{new Date(entry.timestampUtc).toLocaleString()}</time><strong>{entry.source}</strong></summary><pre>{entry.message}</pre></details>)}</div>}
    </section>
  </article>;
}
