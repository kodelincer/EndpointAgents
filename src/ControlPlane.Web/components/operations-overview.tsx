"use client";

import { Activity, AlertTriangle, Check, CircleAlert, Clock3, Pause, Play, Power,
  RefreshCw, RotateCw, Server, Waypoints, X } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { getManagedServiceStatus, runServiceAction } from "@/lib/control-plane";
import { useControlPlane } from "@/lib/control-plane-context";
import { buildLifecycle, serviceActionNeedsConfirmation } from "@/lib/operations";
import type { Activity as ActivityRecord, ManagedServiceStatus, ServiceAction,
  ServiceActionState } from "@/lib/types";

function relative(value: string) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  return `${Math.floor(seconds / 3600)}h ago`;
}

function duration(totalSeconds?: number) {
  if (totalSeconds == null) return "—";
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  return days ? `${days}d ${hours}h` : hours ? `${hours}h ${minutes}m` : `${minutes}m`;
}

const ACTION_LABELS: Record<ServiceAction, string> = {
  start: "Start service", stop: "Stop service", restart: "Restart service",
  enterMaintenance: "Enter maintenance", exitMaintenance: "Resume commands"
};

/** Operations landing page for API health, service lifecycle, and recent incidents. */
export function OperationsOverview() {
  const { agents, activity, loading, loadError } = useControlPlane();
  const [status, setStatus] = useState<ManagedServiceStatus>();
  const [statusError, setStatusError] = useState<string>();
  const [actionState, setActionState] = useState<ServiceActionState>();
  const [confirmation, setConfirmation] = useState<ServiceAction>();

  const refreshStatus = useCallback(async () => {
    try {
      setStatus(await getManagedServiceStatus());
      setStatusError(undefined);
    } catch (error) {
      setStatusError(error instanceof Error ? error.message : "The local supervisor is unavailable.");
    }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refreshStatus(), 0);
    const poll = window.setInterval(() => void refreshStatus(), 5000);
    return () => { window.clearTimeout(initial); window.clearInterval(poll); };
  }, [refreshStatus]);

  const execute = async (action: ServiceAction) => {
    setConfirmation(undefined);
    setActionState({ action, state: "running" });
    try {
      const result = await runServiceAction(action);
      setStatus(result.status);
      setActionState({ action, state: result.accepted ? "success" : "error", message: result.message });
      window.setTimeout(() => void refreshStatus(), 1000);
    } catch (error) {
      setActionState({ action, state: "error",
        message: error instanceof Error ? error.message : "The service action failed." });
    }
  };

  const requestAction = (action: ServiceAction) => {
    if (serviceActionNeedsConfirmation(action)) setConfirmation(action);
    else void execute(action);
  };

  const recentFailures = useMemo(() => activity.filter(item =>
    item.type === "command.completed" && item.status === "error").slice(-5).reverse(), [activity]);
  const recentDisconnects = useMemo(() => activity.filter(item =>
    item.type === "agent.disconnected").slice(-5).reverse(), [activity]);
  const recentActivity = useMemo(() => activity.slice(-8).reverse(), [activity]);
  const available = new Set(status?.availableActions ?? []);
  const busy = actionState?.state === "running" || Boolean(status?.pendingAction);

  const lifecycle = buildLifecycle(status, agents.length);

  return <div className="overview-page">
    <section className="overview-heading" aria-labelledby="overview-title">
      <div><h1 id="overview-title">Operations overview</h1><p>Service lifecycle, API readiness, and endpoint activity from one local console.</p></div>
      <span className="last-check"><Clock3 size={14} />{status ? `Checked ${relative(status.checkedAtUtc)}` : "Waiting for supervisor"}</span>
    </section>

    {(statusError || loadError) && <div className="inline-error overview-error" role="alert"><CircleAlert size={17} /><div>
      <strong>{statusError ? "Service management is unavailable" : "Control-plane data is unavailable"}</strong>
      <span>{statusError ?? loadError}</span></div><button onClick={() => void refreshStatus()}>Retry</button></div>}

    <section className="lifecycle-board" aria-label="Control plane lifecycle">
      <div className="lifecycle-track">
        {lifecycle.map((item, index) => <div className={`lifecycle-stage ${item.state}`} key={item.label}>
          <span className="stage-marker">{item.state === "success" ? <Check size={14} /> : item.state === "warning" ? <Clock3 size={14} /> : <X size={14} />}</span>
          <span><small>{item.label}</small><strong>{item.value}</strong></span>
          {index < lifecycle.length - 1 && <span className="stage-link" aria-hidden="true" />}
        </div>)}
      </div>

      <div className="service-controls">
        <div><strong>{status?.serviceName ?? "EndpointAgents.ControlPlane.Api"}</strong><span>{status?.detail ?? "Local Windows service supervision"}</span></div>
        <div className="control-actions">
          <button className="secondary-button" disabled={busy || !available.has("enterMaintenance")} onClick={() => requestAction("enterMaintenance")}><Pause size={15} />Maintenance</button>
          <button className="secondary-button" disabled={busy || !available.has("exitMaintenance")} onClick={() => requestAction("exitMaintenance")}><Play size={15} />Resume</button>
          <button className="secondary-button" disabled={busy || !available.has("restart")} onClick={() => requestAction("restart")}><RotateCw size={15} />Restart</button>
          {available.has("start") ? <button className="primary-button" disabled={busy} onClick={() => requestAction("start")}><Power size={15} />Start service</button> :
            <button className="danger-button" disabled={busy || !available.has("stop")} onClick={() => requestAction("stop")}><Power size={15} />Stop service</button>}
        </div>
      </div>

      {confirmation && <div className="action-confirmation" role="alertdialog" aria-labelledby="confirmation-title">
        <AlertTriangle size={19} /><div><strong id="confirmation-title">{ACTION_LABELS[confirmation]}?</strong>
          <span>{confirmation === "stop" ? "Connected agents will disconnect and in-flight commands will end." : "The API will be briefly unavailable while the Windows service restarts."}</span></div>
        <button className="secondary-button" onClick={() => setConfirmation(undefined)}>Cancel</button>
        <button className="danger-button" onClick={() => void execute(confirmation)}>Confirm {confirmation}</button>
      </div>}

      {actionState && actionState.state !== "running" && <div className={`action-result ${actionState.state}`} role="status">
        {actionState.state === "success" ? <Check size={15} /> : <CircleAlert size={15} />}{actionState.message}
      </div>}
      {busy && <div className="action-result running" role="status"><RefreshCw className="spin" size={15} />Applying service action…</div>}
    </section>

    <section className="system-ledger" aria-label="Control plane measurements">
      <dl><div><dt>API uptime</dt><dd>{duration(status?.api?.uptimeSeconds)}</dd></div>
        <div><dt>API version</dt><dd><code>{status?.api?.version ?? "—"}</code></dd></div>
        <div><dt>Connected agents</dt><dd>{status?.api?.connectedAgents ?? agents.length}</dd></div>
        <div><dt>Retained events</dt><dd>{status?.api?.retainedActivity ?? activity.length} / 500</dd></div>
        <div><dt>Recent failures</dt><dd className={recentFailures.length ? "danger-text" : "success-text"}>{recentFailures.length}</dd></div>
        <div><dt>Recent disconnects</dt><dd>{recentDisconnects.length}</dd></div></dl>
    </section>

    <div className="overview-columns">
      <section className="operations-panel"><header><div><Activity size={16} /><h2>Latest activity</h2></div><a href="/agents/">Open agent registry</a></header>
        {loading ? <div className="skeleton-list compact"><div className="skeleton-row" /><div className="skeleton-row" /></div> :
        recentActivity.length ? <div className="operations-list">{recentActivity.map(item => <ActivityLine key={item.eventId} item={item} />)}</div> :
        <div className="panel-empty"><Waypoints size={19} /><span>Agent connections and command results will appear here.</span></div>}
      </section>
      <section className="operations-panel incident-panel"><header><div><Server size={16} /><h2>Attention queue</h2></div><span>{recentFailures.length + recentDisconnects.length} recent</span></header>
        {recentFailures.length + recentDisconnects.length ? <div className="operations-list">
          {[...recentFailures, ...recentDisconnects].sort((a, b) => b.timestampUtc.localeCompare(a.timestampUtc)).slice(0, 6)
            .map(item => <ActivityLine key={item.eventId} item={item} />)}
        </div> : <div className="panel-empty healthy"><Check size={19} /><span>No recent command failures or disconnects.</span></div>}
      </section>
    </div>
  </div>;
}

function ActivityLine({ item }: { item: ActivityRecord }) {
  const problem = item.status === "error" || item.status === "offline";
  return <div className="operation-line"><span className={`operation-symbol ${problem ? "problem" : "normal"}`}>
    {problem ? <CircleAlert size={13} /> : <Check size={13} />}</span><span><strong>{item.agentId}</strong>
      <small>{item.type.replace(".", " ")}{item.durationMs != null ? ` · ${item.durationMs} ms` : ""}</small></span>
    <time>{relative(item.timestampUtc)}</time></div>;
}
