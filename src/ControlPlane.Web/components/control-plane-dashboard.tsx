"use client";

import { useAuth } from "@/components/auth-gate";
import { Activity as ActivityIcon, Check, ChevronDown, CircleAlert, Clock3, Copy,
  ChevronRight, MoreHorizontal, PanelRightClose, Play, RefreshCw, Search, Server, SlidersHorizontal, X } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { commandGroup } from "@/lib/commands";
import { runCommand } from "@/lib/control-plane";
import { useControlPlane } from "@/lib/control-plane-context";
import type { Activity, Agent, CommandDefinition, RpcRequest } from "@/lib/types";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";

function time(value: string) {
  return new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(new Date(value));
}

function relative(value: string) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  return `${Math.floor(seconds / 3600)}h ago`;
}

/** Agent registry, live activity rail, command review, and RPC trace inspection. */
export function ControlPlaneDashboard() {
  const { agents, activity, commands, loading, loadError, refresh } = useControlPlane();
  const canRun = useAuth().can("commands.run");
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<"agent" | "connected">("agent");
  const [agentFilter, setAgentFilter] = useState("");
  const [eventFilter, setEventFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("");
  const [selectedAgent, setSelectedAgent] = useState<Agent>();
  const [selectedCommand, setSelectedCommand] = useState<CommandDefinition>();
  const [selectedTrace, setSelectedTrace] = useState<Activity>();
  const [running, setRunning] = useState(false);
  const [runError, setRunError] = useState<string>();
  const [copied, setCopied] = useState<string>();
  const [railOpen, setRailOpen] = useState(true);
  const [menu, setMenu] = useState<{ x: number; y: number; agent: Agent }>();
  const [openCommandGroup, setOpenCommandGroup] = useState<string>();
  useEffect(() => {
    const close = () => { setMenu(undefined); setOpenCommandGroup(undefined); };
    window.addEventListener("click", close); window.addEventListener("resize", close);
    return () => { window.removeEventListener("click", close); window.removeEventListener("resize", close); };
  }, []);

  const lastByAgent = useMemo(() => {
    const map = new Map<string, Activity>();
    activity.forEach(item => map.set(item.agentId, item));
    return map;
  }, [activity]);

  const visibleAgents = useMemo(() => agents.filter(agent =>
    `${agent.agentId} ${agent.hostname} ${agent.userName} ${agent.version}`.toLowerCase().includes(query.toLowerCase()))
    .sort((a, b) => sort === "agent" ? a.agentId.localeCompare(b.agentId) : b.connectedAtUtc.localeCompare(a.connectedAtUtc)),
  [agents, query, sort]);

  const visibleActivity = useMemo(() => activity.filter(item =>
    (!agentFilter || item.agentId === agentFilter) && (!eventFilter || item.type === eventFilter) &&
    (!statusFilter || item.status === statusFilter)).reverse(), [activity, agentFilter, eventFilter, statusFilter]);

  const commandGroups = useMemo(() => Object.entries(Object.groupBy(commands, command => commandGroup(command.method))), [commands]);

  const openReview = (agent: Agent, command = commands[0]) => {
    if (!command) return;
    setSelectedAgent(agent); setSelectedCommand(command); setSelectedTrace(undefined); setRunError(undefined);
  };

  const execute = async () => {
    if (!selectedAgent || !selectedCommand || running) return;
    const id = crypto.randomUUID();
    const request: RpcRequest = { jsonRpc: "2.0", id, method: selectedCommand.method, params: selectedCommand.defaultParams };
    const startedAt = Date.now();
    setRunning(true); setRunError(undefined);
    try {
      const response = await runCommand(selectedAgent.agentId, request);
      if (response.error) setRunError(response.error.message);
      setSelectedTrace({ eventId: id, type: "command.completed", timestampUtc: new Date().toISOString(),
        agentId: selectedAgent.agentId, status: response.error ? "error" : "success", requestId: id,
        request, response, durationMs: Date.now() - startedAt });
      window.setTimeout(() => void refresh().finally(() => setRunning(false)), 700);
    } catch (error) {
      setRunError(error instanceof Error ? error.message : "The command could not be sent."); setRunning(false);
    }
  };

  const copy = async (label: string, value: unknown) => {
    await navigator.clipboard.writeText(JSON.stringify(value, null, 2)); setCopied(label);
    window.setTimeout(() => setCopied(undefined), 1500);
  };

  const clearFilters = () => { setAgentFilter(""); setEventFilter(""); setStatusFilter(""); };
  const filtered = agentFilter || eventFilter || statusFilter;

  return <div className="agent-workspace">
    <div className={`workspace ${railOpen ? "with-rail" : ""}`}>
      <section className="registry" aria-labelledby="registry-title">
        <div className="section-heading"><div><h1 id="registry-title">{pageName("/agents/")}</h1></div>
          {!railOpen && <button className="secondary-button" onClick={() => setRailOpen(true)}><ActivityIcon size={15} /> Show activity</button>}
        </div>
        <PageIntro route="/agents/" />
        <div className="registry-tools">
          <label className="search-field"><Search size={16} /><span className="sr-only">Search agents</span>
            <input value={query} onChange={e => setQuery(e.target.value)} placeholder="Search agent, host, or user" /></label>
          <label className="sort-field"><span>Sort</span><select value={sort} onChange={e => setSort(e.target.value as typeof sort)}><option value="agent">Agent ID</option><option value="connected">Connected time</option></select><ChevronDown size={14} /></label>
        </div>

        {loadError && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Dashboard data is unavailable</strong><span>{loadError}</span></div><button onClick={() => void refresh()}>Retry</button></div>}
        {loading ? <div className="skeleton-list" aria-label="Loading endpoints">{[1,2,3].map(i => <div className="skeleton-row" key={i} />)}</div> :
        visibleAgents.length === 0 ? <div className="empty-state"><span className="empty-icon"><Server size={23} /></span><h2>{query ? "No matching endpoints" : "Waiting for endpoints"}</h2><p>{query ? "Try a different hostname, user, or agent ID." : "Start an endpoint service configured for this control plane. It will appear here as soon as it registers."}</p></div> :
        <div className="agent-table-wrap"><table className="agent-table"><thead><tr><th>Endpoint</th><th>Host</th><th>User</th><th>Version</th><th>Connected</th><th>Last activity</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>{visibleAgents.map(agent => { const last = lastByAgent.get(agent.agentId); return <tr key={agent.agentId} tabIndex={0}
            onContextMenu={e => { e.preventDefault(); setOpenCommandGroup(undefined); setMenu({ x: e.clientX, y: e.clientY, agent }); }} onKeyDown={e => { if (e.key === "Enter") openReview(agent); }}>
            <td data-label="Agent"><span className="agent-identity"><span className="online-indicator" aria-label="Online" /><span><strong>{agent.agentId}</strong><small>{agent.machineName}</small></span></span></td>
            <td data-label="Host">{agent.hostname}</td><td data-label="User">{agent.userName}</td><td data-label="Version"><code>{agent.version}</code></td>
            <td data-label="Connected" title={new Date(agent.connectedAtUtc).toLocaleString()}>{relative(agent.connectedAtUtc)}</td>
            <td data-label="Last activity">{last ? <span className={`table-status ${last.status}`}>{last.status} · {relative(last.timestampUtc)}</span> : <span className="muted">No commands</span>}</td>
            <td className="row-actions"><button className="icon-button" aria-label={`Actions for ${agent.agentId}`} aria-haspopup="menu"
              onClick={e => { e.stopPropagation(); const box = e.currentTarget.getBoundingClientRect(); setOpenCommandGroup(undefined); setMenu({ x: box.right - 220, y: box.bottom + 6, agent }); }}><MoreHorizontal size={17} /></button></td>
          </tr>; })}</tbody></table></div>}
      </section>

      {railOpen && <aside className="activity-rail" aria-labelledby="activity-title">
        <div className="rail-heading"><div><h2 id="activity-title">Live activity</h2><span>{visibleActivity.length} shown · 500 retained</span></div><button className="icon-button" onClick={() => setRailOpen(false)} aria-label="Close activity panel"><PanelRightClose size={17} /></button></div>
        <div className="filters"><select aria-label="Filter by agent" value={agentFilter} onChange={e => setAgentFilter(e.target.value)}><option value="">All agents</option>{agents.map(a => <option key={a.agentId}>{a.agentId}</option>)}</select>
          <select aria-label="Filter by event" value={eventFilter} onChange={e => setEventFilter(e.target.value)}><option value="">All events</option><option value="command.completed">Commands</option><option value="agent.connected">Connections</option><option value="agent.disconnected">Disconnections</option></select>
          <select aria-label="Filter by status" value={statusFilter} onChange={e => setStatusFilter(e.target.value)}><option value="">All states</option><option value="success">Success</option><option value="error">Error</option><option value="pending">Pending</option></select>
          {filtered && <button className="clear-filter" onClick={clearFilters}><X size={13} /> Clear filters</button>}</div>
        <div className="activity-list">{visibleActivity.length === 0 ? <div className="rail-empty"><SlidersHorizontal size={20} /><p>{filtered ? "No events match these filters." : "Connection and command events will appear here live."}</p></div> :
          visibleActivity.map(item => <button className="activity-item" key={item.eventId} onClick={() => { setSelectedTrace(item); setSelectedAgent(undefined); }}>
            <span className={`event-symbol ${item.status}`}>{item.status === "success" || item.status === "online" ? <Check size={13} /> : item.status === "pending" ? <Clock3 size={13} /> : <CircleAlert size={13} />}</span>
            <span className="activity-content"><span><strong>{item.agentId}</strong><time>{time(item.timestampUtc)}</time></span><span>{item.type.replace(".", " ")}{item.durationMs != null ? ` · ${item.durationMs} ms` : ""}</span></span>
          </button>)}</div>
      </aside>}
    </div>

    {menu && <div className="context-menu" role="menu" style={{ left: Math.min(menu.x, window.innerWidth - 240), top: Math.min(menu.y, window.innerHeight - 160) }} onClick={e => e.stopPropagation()}>
      <span className="menu-label">Run on {menu.agent.agentId}</span>{commandGroups.map(([group, groupCommands]) => <div className="command-group" key={group}>
        <button role="menuitem" aria-haspopup="menu" aria-expanded={openCommandGroup === group}
          onClick={() => setOpenCommandGroup(current => current === group ? undefined : group)}>
          <span><strong>{group}</strong><small>{groupCommands?.length ?? 0} commands</small></span><ChevronRight className="submenu-chevron" size={14} />
        </button>
        {openCommandGroup === group && <div className="command-submenu" role="menu" aria-label={`${group} commands`}>
          {groupCommands?.map(command => <button role="menuitem" key={command.method} onClick={() => { openReview(menu.agent, command); setMenu(undefined); setOpenCommandGroup(undefined); }}>
            <Play size={14} /><span><strong>{command.name}</strong><small>{command.method}</small></span>
          </button>)}
        </div>}
      </div>)}</div>}

    {(selectedAgent && selectedCommand || selectedTrace) && <><button className="drawer-backdrop" aria-label="Close panel" onClick={() => { setSelectedAgent(undefined); setSelectedTrace(undefined); }} /><aside className="drawer" aria-label={selectedTrace ? "RPC trace details" : "Review command"}>
      <div className="drawer-header"><div><span>{selectedTrace ? "RPC trace" : "Command review"}</span><h2>{selectedTrace ? selectedTrace.agentId : selectedCommand?.name}</h2></div><button className="icon-button" onClick={() => { setSelectedAgent(undefined); setSelectedTrace(undefined); }} aria-label="Close panel"><X size={18} /></button></div>
      {selectedTrace ? <TraceInspector trace={selectedTrace} copied={copied} onCopy={copy} /> : selectedAgent && selectedCommand && <div className="drawer-body">
        <div className="review-target"><span className="online-indicator" /><div><span>Target endpoint</span><strong>{selectedAgent.agentId}</strong><small>{selectedAgent.hostname} · {selectedAgent.userName}</small></div></div>
        <p className="command-description">{selectedCommand.description}</p>
        <JsonBlock title="JSON-RPC request" value={{ jsonRpc: "2.0", id: "Generated when sent", method: selectedCommand.method, params: selectedCommand.defaultParams }} copied={copied === "request"} onCopy={() => copy("request", { jsonRpc: "2.0", method: selectedCommand.method, params: selectedCommand.defaultParams })} />
        {runError && <div className="command-error" role="alert"><CircleAlert size={16} />{runError}</div>}
        <div className="drawer-actions"><button className="secondary-button" onClick={() => setSelectedAgent(undefined)}>Cancel</button>{!canRun && <span className="drawer-note">Only admins can run endpoint commands.</span>}<button className="primary-button" disabled={running || !canRun} onClick={() => void execute()}>{running ? <><RefreshCw className="spin" size={15} /> Running…</> : <><Play size={15} /> Run command</>}</button></div>
      </div>}
    </aside></>}
  </div>;
}

/** Renders inspectable JSON with a consistent copy affordance. */
function JsonBlock({ title, value, copied, onCopy }: { title: string; value: unknown; copied: boolean; onCopy: () => void }) {
  return <section className="json-section"><div><h3>{title}</h3><button onClick={onCopy}>{copied ? <Check size={14} /> : <Copy size={14} />}{copied ? "Copied" : "Copy"}</button></div><pre>{JSON.stringify(value, null, 2)}</pre></section>;
}

/** Correlates request, response, duration, and endpoint identity for one trace. */
function TraceInspector({ trace, copied, onCopy }: { trace: Activity; copied?: string; onCopy: (label: string, value: unknown) => void }) {
  return <div className="drawer-body trace-body"><div className="trace-summary"><span className={`event-symbol ${trace.status}`}>{trace.status === "success" || trace.status === "online" ? <Check size={13} /> : <CircleAlert size={13} />}</span><div><strong>{trace.status}</strong><span>{trace.type} · {new Date(trace.timestampUtc).toLocaleString()}</span></div>{trace.durationMs != null && <code>{trace.durationMs} ms</code>}</div>
    {trace.requestId && <dl className="trace-meta"><div><dt>Request ID</dt><dd>{trace.requestId}</dd></div><div><dt>Agent</dt><dd>{trace.agentId}</dd></div></dl>}
    {trace.request && <JsonBlock title="Request" value={trace.request} copied={copied === "trace-request"} onCopy={() => onCopy("trace-request", trace.request)} />}
    {trace.response && <JsonBlock title="Response" value={trace.response} copied={copied === "trace-response"} onCopy={() => onCopy("trace-response", trace.response)} />}
    {!trace.request && !trace.response && <div className="connection-detail"><Server size={18} /><p>This connection event has no RPC payload.</p></div>}
  </div>;
}
