"use client";

import {
  Activity, Bot, Check, ChevronRight, CircleAlert, Clipboard, Clock3, Code2,
  LifeBuoy, MessageSquareReply, Play, RefreshCw, Search, Server, ShieldCheck,
  UserRound, Wifi, WifiOff, Wrench
} from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useControlPlane } from "@/lib/control-plane-context";
import {
  getHelpdeskConversation, getHelpdeskConversations, patchHelpdeskConversation,
  runHelpdeskDiagnostic, sendHelpdeskReply
} from "@/lib/control-plane";
import { connectionLabel } from "@/lib/support";
import { mergeHelpdeskConversations, mergeHelpdeskDetail, repairAttempts } from "@/lib/helpdesk";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";
import type {
  ChatMessage, HelpdeskConversationDetail, HelpdeskConversationSummary,
  HelpdeskTraceEvent, SupportRequest
} from "@/lib/types";

const statusOptions = ["All", "Escalated", "New", "Open", "Resolved"] as const;
const diagnosticOptions = [
  ["endpoint.get_info", "Endpoint information"],
  ["system.get_info", "System information"],
  ["system.windows_activation_status", "Windows activation"],
  ["software.office_inventory", "Office inventory"],
  ["software.office_activation_status", "Office activation"],
  ["network.ibsng_diagnose", "IBSng connection"]
] as const;
const roles = ["Endpoint user", "Hermes", "System", "Helpdesk operator"] as const;

function relative(value: string) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

function isRtl(value: string) { return /[\u0590-\u08ff]/.test(value); }

/** Durable endpoint conversations with human takeover and diagnostic evidence. */
export function HelpdeskRequests() {
  const { supportRequests, loading, loadError, helpdeskVersion, connection } = useControlPlane();
  const [conversations, setConversations] = useState<HelpdeskConversationSummary[]>([]);
  const [detail, setDetail] = useState<HelpdeskConversationDetail>();
  // ?conversation=<id> (linked from the Feature requests page) opens that conversation.
  const [selectedId, setSelectedId] = useState<string | undefined>(() =>
    typeof window === "undefined" ? undefined
      : new URLSearchParams(window.location.search).get("conversation") ?? undefined);
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<(typeof statusOptions)[number]>("All");
  const [localVersion, setLocalVersion] = useState(0);
  const [busy, setBusy] = useState(false);
  const [fetching, setFetching] = useState(true);
  const [error, setError] = useState<string>();

  const reload = useCallback(() => setLocalVersion(value => value + 1), []);
  useEffect(() => {
    let current = true;
    getHelpdeskConversations().then(items => {
      if (current) { setConversations(existing => mergeHelpdeskConversations(existing, items)); setError(undefined); }
    }).catch(reason => current && setError(reason instanceof Error ? reason.message : "Could not load conversations."))
      .finally(() => current && setFetching(false));
    return () => { current = false; };
  }, [helpdeskVersion, localVersion]);

  const visible = useMemo(() => conversations.filter(item => {
    const matchesStatus = status === "All" || item.status === status;
    const haystack = `${item.preview} ${item.agentId} ${item.machineName} ${item.userName}`.toLowerCase();
    return matchesStatus && haystack.includes(query.trim().toLowerCase());
  }), [conversations, query, status]);
  const visibleLegacy = useMemo(() => supportRequests.filter(item =>
    `${item.subject} ${item.messageText} ${item.agentId} ${item.machineName} ${item.userName}`
      .toLowerCase().includes(query.trim().toLowerCase())), [supportRequests, query]);
  const selectedVisible = selectedId && (visible.some(item => item.conversationId === selectedId)
    || visibleLegacy.some(item => `legacy:${item.requestId}` === selectedId));
  const activeId = selectedVisible ? selectedId
    : visible[0]?.conversationId ?? (visibleLegacy[0] ? `legacy:${visibleLegacy[0].requestId}` : undefined);
  const selectedLegacy = activeId?.startsWith("legacy:")
    ? supportRequests.find(item => `legacy:${item.requestId}` === activeId) : undefined;

  useEffect(() => {
    if (!activeId || activeId.startsWith("legacy:")) return;
    let current = true;
    getHelpdeskConversation(activeId).then(value => {
      if (!current) return;
      setDetail(existing => mergeHelpdeskDetail(existing, value));
      setError(undefined);
      if (value.conversation.unreadCount > 0)
        void patchHelpdeskConversation(activeId, { readThroughSequence: value.conversation.lastSequence });
    }).catch(reason => current && setError(reason instanceof Error ? reason.message : "Could not load conversation."));
    return () => { current = false; };
  }, [activeId, helpdeskVersion, localVersion]);

  const mutate = async (action: () => Promise<unknown>) => {
    setBusy(true); setError(undefined);
    try { await action(); reload(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "The helpdesk update failed."); }
    finally { setBusy(false); }
  };

  const empty = !fetching && conversations.length === 0 && supportRequests.length === 0;
  return <main className="support-page">
    <header className="support-heading">
      <div><span className="support-heading-mark"><LifeBuoy size={19} /></span><div>
        <h1>{pageName("/support/")}</h1>
      </div></div>
      <span className={`support-live-state ${connection}`}>
        {connection === "live" ? <Wifi size={14} /> : <WifiOff size={14} />}{connection}
      </span>
    </header>
    <PageIntro route="/support/" />

    {(loadError || error) && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Helpdesk update failed</strong><span>{error ?? loadError}</span></div></div>}

    {(loading || fetching) && conversations.length === 0 ?
      <div className="support-skeleton"><div /><div /><div /></div> : empty ?
      <section className="support-empty"><span className="empty-icon"><LifeBuoy size={23} /></span>
        <h2>No helpdesk conversations yet</h2>
        <p>When a connected endpoint user sends a message from the Tray, the conversation will appear here live.</p>
      </section> :
      <div className="support-workspace">
        <section className="support-inbox" aria-label="Helpdesk conversation inbox">
          <div className="support-inbox-tools">
            <label className="support-search"><Search size={15} /><span className="sr-only">Search helpdesk</span>
              <input value={query} onChange={event => setQuery(event.target.value)}
                placeholder="Search user, endpoint, or message" /></label>
            <div className="support-filter" aria-label="Filter by conversation status">
              {statusOptions.map(option => <button key={option} type="button"
                className={status === option ? "selected" : ""} onClick={() => setStatus(option)}>{option}</button>)}
            </div>
          </div>
          <div className="support-list">
            {visible.map(item => <button className={item.conversationId === activeId ? "selected" : ""}
              key={item.conversationId} onClick={() => setSelectedId(item.conversationId)}>
              <span className="support-item-top"><strong>{item.userName || "Endpoint user"}</strong>
                <time>{relative(item.updatedAtUtc)}</time></span>
              <span className="support-item-meta">
                <span className={`support-dot ${item.isOnline ? "online" : "offline"}`} />
                {item.machineName || item.agentId}<span className={`ticket-status ${item.status.toLowerCase()}`}>{item.status}</span>
                {item.unreadCount > 0 && <span className="unread-count">{item.unreadCount}</span>}
              </span>
              <span className="support-preview" dir={item.textDirection}>{item.preview || "Conversation opened"}</span>
            </button>)}
            {visibleLegacy.length > 0 && <div className="legacy-divider">Legacy submissions · read only</div>}
            {visibleLegacy.map(item => <button className={`legacy ${`legacy:${item.requestId}` === activeId ? "selected" : ""}`}
              key={item.requestId} onClick={() => setSelectedId(`legacy:${item.requestId}`)}>
              <span className="support-item-top"><strong>{item.subject}</strong><time>{relative(item.receivedAtUtc)}</time></span>
              <span className="support-item-meta"><Server size={12} />{item.machineName}<UserRound size={12} />{item.userName}</span>
              <span className="support-preview" dir={item.textDirection}>{item.messageText}</span>
            </button>)}
            {visible.length === 0 && visibleLegacy.length === 0 && <p className="support-no-match">No conversations match this filter.</p>}
          </div>
        </section>
        {selectedLegacy ? <LegacyDetail request={selectedLegacy} /> : detail && detail.conversation.conversationId === activeId ?
          <ConversationDetail detail={detail} busy={busy} onMutate={mutate} /> :
          <div className="support-detail-loading"><RefreshCw size={18} className="spin" />Loading conversation…</div>}
      </div>}
  </main>;
}

function ConversationDetail({ detail, busy, onMutate }: {
  detail: HelpdeskConversationDetail; busy: boolean; onMutate: (action: () => Promise<unknown>) => Promise<void>;
}) {
  const conversation = detail.conversation;
  const [tab, setTab] = useState<"transcript" | "evidence" | "raw">("transcript");
  const [message, setMessage] = useState("");
  const [diagnostic, setDiagnostic] = useState(diagnosticOptions[0][0] as string);
  const [traceDirection, setTraceDirection] = useState("All");
  const operator = conversation.responderMode === "Operator";
  const filteredTrace = detail.trace.filter(item => traceDirection === "All" || item.direction === traceDirection);

  const send = async () => {
    const body = message.trim();
    if (!body || !operator) return;
    await onMutate(() => sendHelpdeskReply(conversation.conversationId, body, isRtl(body) ? "rtl" : "ltr"));
    setMessage("");
  };

  return <article className="support-detail helpdesk-detail">
    <header className="conversation-header"><div className="conversation-title">
      <span className={`endpoint-state ${conversation.isOnline ? "online" : "offline"}`}>
        {conversation.isOnline ? <Wifi size={13} /> : <WifiOff size={13} />}
        {conversation.isOnline ? "Endpoint online" : "Endpoint offline"}</span>
      <h2>{conversation.userName || "Endpoint user"}</h2>
      <p>{conversation.machineName || conversation.agentId} · session {conversation.sessionId}</p>
    </div><div className="conversation-actions">
      <label><span>Status</span><select value={conversation.status} disabled={busy}
        onChange={event => void onMutate(() => patchHelpdeskConversation(conversation.conversationId,
          { status: event.target.value }))}>
        <option>Escalated</option><option>New</option><option>Open</option><option>Resolved</option>
      </select></label>
      <button type="button" className={operator ? "takeover active" : "takeover"} disabled={busy}
        onClick={() => void onMutate(() => patchHelpdeskConversation(conversation.conversationId,
          { operatorTakeover: !operator }))}>
        {operator ? <Bot size={14} /> : <ShieldCheck size={14} />}{operator ? "Return to Hermes" : "Take over"}
      </button>
    </div></header>

    <EscalationPanel detail={detail} />

    <nav className="support-detail-tabs" aria-label="Conversation detail">
      <button className={tab === "transcript" ? "selected" : ""} onClick={() => setTab("transcript")}>
        <MessageSquareReply size={14} />Transcript <span>{detail.messages.length}</span></button>
      <button className={tab === "evidence" ? "selected" : ""} onClick={() => setTab("evidence")}>
        <Activity size={14} />Evidence</button>
      <button className={tab === "raw" ? "selected" : ""} onClick={() => setTab("raw")}>
        <Code2 size={14} />Raw trace <span>{detail.trace.length}</span></button>
    </nav>

    {tab === "transcript" && <div className="conversation-pane">
      <div className="message-stream" aria-live="polite">
        {detail.messages.map(item => <Message key={item.messageId} message={item} />)}
      </div>
      <div className={`operator-composer ${operator ? "active" : "locked"}`}>
        {!operator && <div className="composer-lock"><Bot size={15} /><span>Hermes is responding. Take over to send a human reply.</span></div>}
        <textarea aria-label="Reply to endpoint user" value={message} maxLength={200} disabled={!operator || busy || !conversation.isOnline}
          placeholder={conversation.isOnline ? "Write a clear reply to the endpoint user…" : "Endpoint is offline; replies are unavailable."}
          onChange={event => setMessage(event.target.value)} onKeyDown={event => {
            if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); void send(); }
          }} />
        <div><span>{message.length}/200 · Shift+Enter for a new line</span>
          <button className="primary-button" disabled={!operator || busy || !conversation.isOnline || !message.trim()}
            onClick={() => void send()}>Send reply <ChevronRight size={14} /></button></div>
      </div>
    </div>}

    {tab === "evidence" && <div className="evidence-pane">
      <section><h3>Current endpoint evidence</h3><dl className="evidence-grid">
        <div><dt>Connection</dt><dd><span className={`support-dot ${conversation.isOnline ? "online" : "offline"}`} />{conversation.isOnline ? "Connected" : "Offline"}</dd></div>
        <div><dt>Agent ID</dt><dd><code>{conversation.agentId}</code></dd></div>
        <div><dt>Machine</dt><dd>{conversation.machineName || "Not reported"}</dd></div>
        <div><dt>Agent version</dt><dd><code>{conversation.version || "Not reported"}</code></dd></div>
        <div><dt>Windows user</dt><dd>{conversation.userName || "Not reported"}</dd></div>
        <div><dt>Updated</dt><dd>{new Date(conversation.updatedAtUtc).toLocaleString()}</dd></div>
      </dl></section>
      <section className="diagnostic-runner"><h3>Capture fresh diagnostic evidence</h3>
        <p>Runs an approved read-only check and stores its request and response with this conversation.</p>
        <div><select value={diagnostic} disabled={busy || !conversation.isOnline}
          onChange={event => setDiagnostic(event.target.value)}>
          {diagnosticOptions.map(([value, label]) => <option value={value} key={value}>{label}</option>)}</select>
          <button className="secondary-button" disabled={busy || !conversation.isOnline}
            onClick={() => void onMutate(() => runHelpdeskDiagnostic(conversation.conversationId, diagnostic))}>
            <Play size={14} />Run diagnostic</button></div>
      </section>
      <section><h3>Captured diagnostic results</h3>
        <div className="diagnostic-results">{detail.trace.filter(item => item.eventType === "rpc.response").length === 0 ?
          <p>No helpdesk diagnostics have been captured yet.</p> : detail.trace.filter(item => item.eventType === "rpc.response").map(item =>
            <details key={item.eventId}><summary><span className={`trace-outcome ${item.outcome.toLowerCase()}`}>{item.outcome}</span>
              <time>{new Date(item.timestampUtc).toLocaleString()}</time></summary><pre>{JSON.stringify(item.payload, null, 2)}</pre></details>)}</div>
      </section>
    </div>}

    {tab === "raw" && <div className="raw-pane">
      <div className="raw-toolbar"><div><Code2 size={15} /><span>Sanitized application payloads</span></div>
        <label><span className="sr-only">Filter raw trace direction</span><select value={traceDirection}
          onChange={event => setTraceDirection(event.target.value)}><option>All</option>
          <option value="AgentToControlPlane">Inbound from agent</option>
          <option value="ControlPlaneToAgent">Outbound to agent</option>
          <option value="Agent">Support agent decisions</option></select></label></div>
      <p className="raw-notice">Authentication tokens, run capabilities, and authorization headers are never included.</p>
      <div className="trace-list">{filteredTrace.map(item => <Trace key={item.eventId} item={item} />)}</div>
    </div>}
  </article>;
}

/** Why the support agent handed this conversation over, and every repair it already tried. */
function EscalationPanel({ detail }: { detail: HelpdeskConversationDetail }) {
  const conversation = detail.conversation;
  const attempts = repairAttempts(detail);
  if (!conversation.escalationReason && attempts.length === 0) return null;
  return <section className={`escalation-panel ${conversation.status === "Escalated" ? "open" : ""}`}
    aria-label="Agent escalation">
    {conversation.escalationReason && <>
      <h3><LifeBuoy size={14} />{conversation.status === "Escalated" ? "Needs the helpdesk" : "Was escalated"}
        {conversation.escalatedAtUtc && <time>{new Date(conversation.escalatedAtUtc).toLocaleString()}</time>}</h3>
      <p className="escalation-reason">{conversation.escalationReason}</p>
      {conversation.escalationSummary && <p className="escalation-summary">{conversation.escalationSummary}</p>}
    </>}
    {attempts.length > 0 && <>
      <h4>Repairs the agent tried</h4>
      <ol className="escalation-attempts">{attempts.map(attempt =>
        <li key={`${attempt.attemptedAtUtc}-${attempt.artifactId}`}>
          <code>{attempt.artifactId}</code>
          <span className={`trace-outcome ${attempt.outcome === "Completed" ? "success" : "error"}`}>
            {attempt.outcome === "Completed" ? "Finished" : "Failed"}</span>
          <time>{new Date(attempt.attemptedAtUtc).toLocaleString()}</time>
          {attempt.error && <span className="escalation-error">{attempt.error}</span>}
        </li>)}</ol>
    </>}
  </section>;
}

function Message({ message }: { message: ChatMessage }) {
  const user = message.role === 0;
  return <div className={`chat-message role-${message.role} ${user ? "from-user" : "from-control"}`}>
    <div className="message-author">{message.role === 1 ? <Bot size={13} /> : message.role === 3 ? <ShieldCheck size={13} /> : <UserRound size={13} />}
      <strong>{roles[message.role]}</strong><time>{new Date(message.createdAtUtc).toLocaleTimeString()}</time></div>
    <p dir={message.textDirection}>{message.body}</p>
  </div>;
}

function Trace({ item }: { item: HelpdeskTraceEvent }) {
  const [copied, setCopied] = useState(false);
  const json = JSON.stringify(item.payload, null, 2);
  return <details className="trace-event"><summary>
    <span className={`trace-direction ${item.direction === "AgentToControlPlane" ? "inbound" : item.direction === "Agent" ? "agent" : "outbound"}`}>
      {item.direction === "AgentToControlPlane" ? "IN" : item.direction === "Agent" ? "AI" : "OUT"}</span>
    <code>{item.eventType}</code><span className={`trace-outcome ${item.outcome.toLowerCase()}`}>{item.outcome}</span>
    <time>{new Date(item.timestampUtc).toLocaleTimeString()}</time></summary>
    <div className="trace-json"><button type="button" onClick={() => void navigator.clipboard.writeText(json).then(() => {
      setCopied(true); window.setTimeout(() => setCopied(false), 1200);
    })}>{copied ? <Check size={13} /> : <Clipboard size={13} />}{copied ? "Copied" : "Copy JSON"}</button><pre>{json}</pre></div>
  </details>;
}

function LegacyDetail({ request }: { request: SupportRequest }) {
  return <article className="support-detail legacy-detail"><header><div><span>Legacy request {request.requestId.slice(0, 8)}</span>
    <h2>{request.subject}</h2></div><span className="legacy-badge">Read only</span></header>
    <div className="support-request-meta"><span><Clock3 size={14} />{new Date(request.receivedAtUtc).toLocaleString()}</span>
      <span><Server size={14} />{request.agentId}</span><span><UserRound size={14} />{request.userName} · session {request.sessionId}</span></div>
    <section className="support-message"><h3>User message</h3><p dir={request.textDirection}>{request.messageText}</p></section>
    <section className="support-evidence"><h3>Captured endpoint evidence</h3><dl>
      <div><dt>Connection</dt><dd>{connectionLabel(request.runtime.connectionStatus)}</dd></div>
      <div><dt>Functions</dt><dd>{request.runtime.functionMode === 1 ? "Paused" : "Active"}</dd></div>
      <div><dt>Version</dt><dd><code>{request.runtime.version}</code></dd></div>
      <div><dt>Last problem</dt><dd className={request.runtime.lastProblemMessage ? "problem" : ""}>{request.runtime.lastProblemMessage ?? "None reported"}</dd></div>
    </dl></section>
    <section className="legacy-warning"><Wrench size={16} /><p>This endpoint used the retired one-shot request protocol. Update its agent to enable live replies and conversation history.</p></section>
  </article>;
}
