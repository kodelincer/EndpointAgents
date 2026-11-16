"use client";

import { CircleAlert, Copy, KeyRound, MonitorSmartphone, RefreshCw } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { age, factName, factRows, getDeviceFacts, getDevices, hubUrlFor, installCommand, issueEnrollmentCode, minutesLeft,
  refreshDevice, revokeDevice, type EnrollmentIssue, type StoredDevice, type StoredFact } from "@/lib/inventory";
import { useAuth } from "@/components/auth-gate";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";

/** Facts kept from earlier reads, with their age. PCs are never polled; Refresh asks one PC now. */
export function Devices() {
  const { can } = useAuth();
  const admin = can("devices.manage");
  const [devices, setDevices] = useState<StoredDevice[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [open, setOpen] = useState<string>();
  const [facts, setFacts] = useState<StoredFact[]>([]);
  const [busy, setBusy] = useState<string>();
  const [filter, setFilter] = useState("");

  const refresh = useCallback(async () => {
    setLoading(true);
    try { setDevices(await getDevices()); setError(undefined); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Devices could not be loaded."); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0);
    return () => window.clearTimeout(initial);
  }, [refresh]);

  const show = async (agentId: string) => {
    if (open === agentId) { setOpen(undefined); return; }
    setOpen(agentId); setFacts([]);
    try { setFacts(await getDeviceFacts(agentId)); } catch { setFacts([]); }
  };

  const readNow = async (device: StoredDevice) => {
    setBusy(device.agentId);
    try {
      const result = await refreshDevice(device.agentId);
      if (result.problems.length) setError(`${device.machineName}: ${result.problems.join("; ")}`);
      await refresh();
      if (open === device.agentId) setFacts(await getDeviceFacts(device.agentId));
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The PC could not be read."); }
    finally { setBusy(undefined); }
  };

  const revoke = async (device: StoredDevice) => {
    if (!window.confirm(`Cut off ${device.machineName}? Its device token stops working at once. To use it again, enroll it with a new code.`)) return;
    setBusy(device.agentId);
    try { await revokeDevice(device.agentId); await refresh(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "The PC could not be revoked."); }
    finally { setBusy(undefined); }
  };

  const shown = devices.filter(device => device.machineName.toLowerCase().includes(filter.toLowerCase()));

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="devices-title">
      <div><div className="file-heading-mark"><MonitorSmartphone size={17} /></div><div><h1 id="devices-title">{pageName("/devices/")}</h1></div></div>
      <button className="secondary-button" onClick={() => void refresh()} disabled={loading}><RefreshCw className={loading ? "spin" : ""} size={15} />Refresh list</button>
    </section>
    <PageIntro route="/devices/" />
    {admin && <EnrollmentCodePanel onError={setError} />}

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Devices</strong><span>{error}</span></div><button onClick={() => setError(undefined)}>Close</button></div>}

    <input className="approval-note" value={filter} onChange={event => setFilter(event.target.value)} placeholder="Find a PC by name" aria-label="Find a PC" />

    {loading && devices.length === 0 ? <div className="file-list"><div className="file-skeleton" /></div>
      : shown.length === 0 ? <section className="file-empty"><span className="empty-icon"><MonitorSmartphone size={23} /></span>
        <h2>No devices yet</h2><p>A PC appears here when it connects or after its first check.</p></section>
      : <section className="file-list" aria-label="Devices">{shown.map(device =>
        <article key={device.agentId} className="file-row">
          <div className="file-row-main">
            <div className="file-row-title"><strong>{device.machineName}</strong>
              <span className={`ticket-status${device.online ? " resolved" : ""}`}>{device.online ? "Online" : "Offline"}</span>
              {device.version && <span className="file-chip">agent {device.version}</span>}</div>
            <p className="file-compatibility">{device.facts === 0 ? "Nothing kept yet" : `${device.facts} kinds of facts, last read ${age(device.lastReadUtc)}`}</p>
            <button type="button" className="package-toggle" onClick={() => void show(device.agentId)} aria-expanded={open === device.agentId}>
              {open === device.agentId ? "Hide facts" : "Show facts"}</button>
            {open === device.agentId && <div className="package-detail">{facts.length === 0 ? <p>Nothing kept for this PC yet.</p>
              : facts.map(fact => <div key={fact.key}>
                <h3>{factName(fact.key)} <span className="muted">· read {age(fact.readAtUtc)}{fact.fresh ? "" : " (old: asked again next time)"}</span></h3>
                <dl className="package-hashes">{factRows(fact.value).slice(0, 30).map(row =>
                  <div key={row.label}><dt>{row.label}</dt><dd>{row.value}</dd></div>)}</dl>
              </div>)}</div>}
          </div>
          <div className="file-row-actions">
            <button className="secondary-button" disabled={!device.online || busy === device.agentId} onClick={() => void readNow(device)}>
              <RefreshCw size={14} className={busy === device.agentId ? "spin" : ""} />{busy === device.agentId ? "Working…" : "Read now"}</button>
            {admin && <button className="secondary-button danger-text" disabled={busy === device.agentId} onClick={() => void revoke(device)}>Revoke</button>}
          </div>
        </article>)}</section>}
  </div>;
}

/** Admins make one code per new PC here; the PC trades it for its device token at install. */
function EnrollmentCodePanel({ onError }: { onError: (message: string) => void }) {
  const [issue, setIssue] = useState<EnrollmentIssue>();
  const [making, setMaking] = useState(false);
  const [copied, setCopied] = useState(false);
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    if (!issue) return;
    const timer = window.setInterval(() => setNow(new Date()), 15_000);
    return () => window.clearInterval(timer);
  }, [issue]);

  const make = async () => {
    setMaking(true); setCopied(false);
    try { setIssue(await issueEnrollmentCode()); setNow(new Date()); }
    catch (reason) { onError(reason instanceof Error ? reason.message : "No code could be made."); }
    finally { setMaking(false); }
  };

  const command = issue ? installCommand(issue.enrollmentCode, hubUrlFor(window.location.hostname)) : "";
  const left = issue ? minutesLeft(issue.expiresAtUtc, now) : 0;
  const copy = async () => { await navigator.clipboard.writeText(command); setCopied(true); };

  return <section className="file-row" aria-labelledby="enroll-title">
    <div className="file-row-main">
      <div className="file-row-title"><KeyRound size={15} /><strong id="enroll-title">Add a new PC</strong></div>
      <p className="file-description">Only enrolled PCs can connect. Make one code per PC, then run the line below on that PC in PowerShell as administrator. The code works once.</p>
      {issue && (left > 0
        ? <div className="package-detail">
            <p>Code <code dir="ltr">{issue.enrollmentCode}</code> <span className="muted">· {left} min left</span></p>
            <pre dir="ltr" className="file-compatibility" style={{ whiteSpace: "pre-wrap", wordBreak: "break-all" }}>{command}</pre>
            {command.includes("SERVER-NAME") && <p className="muted">Replace SERVER-NAME with the name PCs use to reach this server.</p>}
          </div>
        : <p className="file-compatibility">This code has expired. Make a new one.</p>)}
    </div>
    <div className="file-row-actions">
      <button className="secondary-button" disabled={making} onClick={() => void make()}><KeyRound size={14} />{making ? "Making…" : "New enrollment code"}</button>
      {issue && left > 0 && <button className="secondary-button" onClick={() => void copy()}><Copy size={14} />{copied ? "Copied" : "Copy line"}</button>}
    </div>
  </section>;
}
