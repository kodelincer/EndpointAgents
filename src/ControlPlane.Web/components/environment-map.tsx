"use client";

import { CircleAlert, Map as MapIcon, Plus, RefreshCw, Save, Siren, Trash2 } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";
import { createIncident, deleteTarget, getEnvironmentSettings, getIncidents, getTargets, parseNumberList,
  parseTextList, saveEnvironmentSettings, saveTarget, setIncidentStatus, suggestId, targetDetailFields,
  type EnvironmentTarget, type Incident } from "@/lib/capabilities";

type Tab = "targets" | "standards" | "incidents";

const kinds = [
  { value: "webapp", label: "Web app", example: "https://hr.corp.local", address: "Address" },
  { value: "share", label: "Shared folder", example: "\\\\fs01\\finance", address: "Address" },
  { value: "printer", label: "Printer", example: "\\\\print01\\HP-2F", address: "Address" },
  { value: "host", label: "Server", example: "dc01.corp.local", address: "Address" },
  { value: "app", label: "Removable software", example: "Foxit PDF Reader*",
    address: "Name in Apps & features (* allowed)" },
  { value: "device", label: "Driver catalog (device model)", example: "USB\\VID_04F9&PID_0042",
    address: "Hardware IDs (Device Manager > Details), separated by ;" },
  { value: "vpn", label: "VPN", example: "vpn.corp.example", address: "VPN server host name" },
  { value: "wifi", label: "Company Wi-Fi", example: "CORP-STAFF", address: "Network name (SSID)" },
  { value: "site", label: "Allowed work site", example: "*.tax.gov.ir", address: "Host name, or *.domain for all its sites" }
];

/** Kinds that are network places the agent can test; the others are helpdesk lists. */
const networkKinds = ["webapp", "share", "printer", "host", "vpn", "wifi", "site"];

const standards = [
  { key: "network.internalSuffixes", label: "Internal DNS suffixes", hint: "Comma-separated, e.g. corp.local. Names under them are hidden from the cloud model." },
  { key: "proxy.mode", label: "Proxy standard", hint: "none, pac or server. Used by the proxy reset fix." },
  { key: "proxy.pacUrl", label: "PAC address", hint: "When proxy.mode is pac." },
  { key: "proxy.server", label: "Proxy server", hint: "host:port, when proxy.mode is server." },
  { key: "proxy.bypass", label: "Proxy bypass list", hint: "Semicolon list, e.g. *.corp.local;<local>" },
  { key: "time.standardZone", label: "Standard time zone", hint: "Windows id, e.g. Iran Standard Time." },
  { key: "network.dnsFromDhcp", label: "DNS servers come from DHCP", hint: "true when DHCP hands out the right DNS servers, so hand-typed ones are a fault the agent may remove." },
  { key: "trust.rootThumbprints", label: "Organisation root CA thumbprints", hint: "Comma-separated SHA-1 thumbprints." },
  { key: "network.ipFromDhcp", label: "PCs get their IP address from DHCP", hint: "true when every PC should use DHCP, so a hand-set IPv4 address is a fault the agent may reset (undoable)." },
  { key: "power.standardPlan", label: "Standard power plan", hint: "balanced, high or saver. Used by the power plan fix." }
];

const empty: EnvironmentTarget = { id: "", kind: "webapp", label: "", address: "", ports: [], expectedAddresses: [] };

/** The organisation's map: what the agent may test, connect to and compare against. */
export function EnvironmentMap() {
  const { can } = useAuth();
  const manage = can("environment.manage");
  const [tab, setTab] = useState<Tab>("targets");
  const [targets, setTargets] = useState<EnvironmentTarget[]>([]);
  const [settings, setSettings] = useState<Record<string, string>>({});
  const [incidents, setIncidents] = useState<Incident[]>([]);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState<EnvironmentTarget & { ports_?: string; expected_?: string; isNew?: boolean }>();

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      const [map, values, outages] = await Promise.all([getTargets(), getEnvironmentSettings(), getIncidents()]);
      setTargets(map); setSettings(values); setIncidents(outages); setError(undefined);
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The environment map could not be loaded."); }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { const id = window.setTimeout(() => void refresh(), 0); return () => window.clearTimeout(id); }, [refresh]);

  const run = async (work: () => Promise<unknown>, done: string) => {
    try { await work(); setNotice(done); setError(undefined); await refresh(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "The change was not saved."); }
  };

  const saveEditing = () => editing && void run(async () => {
    await saveTarget({ ...editing, id: editing.id || suggestId(editing.label),
      ports: parseNumberList(editing.ports_ ?? editing.ports.join(", ")),
      expectedAddresses: parseTextList(editing.expected_ ?? editing.expectedAddresses.join(", ")),
      driveLetter: editing.kind === "share" && editing.driveLetter ? editing.driveLetter : null,
      details: Object.fromEntries(Object.entries(editing.details ?? {})
        .filter(([key, value]) => value.trim() && (targetDetailFields[editing.kind] ?? []).some(field => field.key === key))) });
    setEditing(undefined);
  }, "Target saved.");

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="environment-title">
      <div><div className="file-heading-mark"><MapIcon size={17} /></div><div>
        <h1 id="environment-title">{pageName("/environment/")}</h1></div></div>
      <button className="secondary-button" onClick={() => void refresh()} disabled={loading}>
        <RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>
    <PageIntro route="/environment/" />

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Not saved</strong><span>{error}</span></div><button onClick={() => setError(undefined)}>Dismiss</button></div>}
    {notice && !error && <p className="success-text" role="status">{notice}</p>}

    <div className="support-filter capability-tabs" role="tablist">
      {([["targets", `Targets (${targets.length})`], ["standards", "Standards"],
        ["incidents", `Incidents (${incidents.filter(item => item.status === "Active").length} active)`]] as const)
        .map(([key, label]) => <button key={key} role="tab" aria-selected={tab === key}
          className={tab === key ? "selected" : ""} onClick={() => setTab(key)}>{label}</button>)}
    </div>

    {tab === "targets" && <section className="capability-group">
      {manage ? <button className="primary-button" onClick={() => setEditing({ ...empty, isNew: true })}><Plus size={14} />Add target</button>
        : <p className="capability-note">Only an admin can change the environment map.</p>}
      {editing && <div className="environment-editor">
        <label>Kind<select value={editing.kind} onChange={event => setEditing({ ...editing, kind: event.target.value })}>
          {kinds.map(kind => <option key={kind.value} value={kind.value}>{kind.label}</option>)}</select></label>
        <label>Label (what users call it)<input value={editing.label} dir="auto"
          onChange={event => setEditing({ ...editing, label: event.target.value })} /></label>
        <label>Id<input value={editing.id} disabled={!editing.isNew} placeholder={suggestId(editing.label) || "hr-portal"}
          onChange={event => setEditing({ ...editing, id: event.target.value })} /></label>
        <label>{kinds.find(kind => kind.value === editing.kind)?.address ?? "Address"}<input value={editing.address} dir="ltr" placeholder={kinds.find(kind => kind.value === editing.kind)?.example}
          onChange={event => setEditing({ ...editing, address: event.target.value })} /></label>
        {(targetDetailFields[editing.kind] ?? []).map(field => <label key={field.key}>{field.label}
          {field.choices ? <select value={editing.details?.[field.key] ?? ""}
            onChange={event => setEditing({ ...editing, details: { ...editing.details, [field.key]: event.target.value } })}>
            <option value="">Choose…</option>{field.choices.map(choice => <option key={choice} value={choice}>{choice}</option>)}</select>
            : <input value={editing.details?.[field.key] ?? ""} dir="ltr"
              onChange={event => setEditing({ ...editing, details: { ...editing.details, [field.key]: event.target.value } })} />}
          <small>{field.hint}</small></label>)}
        {(editing.kind === "host" || editing.kind === "webapp" || editing.kind === "vpn") && <label>Ports the agent may test
          <input value={editing.ports_ ?? editing.ports.join(", ")} placeholder="443, 8080"
            onChange={event => setEditing({ ...editing, ports_: event.target.value })} /></label>}
        {networkKinds.includes(editing.kind) && <label>Expected IP addresses (optional)<input value={editing.expected_ ?? editing.expectedAddresses.join(", ")}
          placeholder="10.0.0.10" onChange={event => setEditing({ ...editing, expected_: event.target.value })} /></label>}
        {editing.kind === "share" && <label>Standard drive letter (optional)<input value={editing.driveLetter ?? ""}
          placeholder="F:" onChange={event => setEditing({ ...editing, driveLetter: event.target.value.toUpperCase() })} /></label>}
        <label>Location<input value={editing.location ?? ""} dir="auto" onChange={event => setEditing({ ...editing, location: event.target.value })} /></label>
        <label className="wide">Notes for the helpdesk<input value={editing.notes ?? ""} dir="auto"
          onChange={event => setEditing({ ...editing, notes: event.target.value })} /></label>
        <div className="wide"><button className="primary-button" onClick={saveEditing}><Save size={14} />Save</button>
          <button className="secondary-button" onClick={() => setEditing(undefined)}>Cancel</button></div>
      </div>}
      {targets.length === 0 && !loading && <p className="capability-note">No targets yet. Start with the most-used internal web apps, the shared drives every user maps, and office printers.</p>}
      {targets.map(target => <article key={target.id} className="file-row">
        <div className="file-row-main">
          <div className="file-row-title"><strong dir="auto">{target.label}</strong><code>{target.id}</code>
            <span className="ticket-status">{kinds.find(kind => kind.value === target.kind)?.label ?? target.kind}</span>
            {target.driveLetter && <span className="ticket-status">{target.driveLetter}</span>}</div>
          <p className="file-description" dir="ltr">{target.address}{target.ports.length > 0 && ` · ports ${target.ports.join(", ")}`}
            {target.expectedAddresses.length > 0 && ` · expects ${target.expectedAddresses.join(", ")}`}
            {Object.entries(target.details ?? {}).map(([key, value]) => ` · ${key} ${value}`).join("")}</p>
          {(target.location || target.notes) && <p className="file-compatibility" dir="auto">{[target.location, target.notes].filter(Boolean).join(" · ")}</p>}
        </div>
        {manage && <div className="file-row-actions">
          <button className="secondary-button" onClick={() => setEditing({ ...target })}>Edit</button>
          <button className="secondary-button" aria-label={`Delete ${target.label}`}
            onClick={() => window.confirm(`Delete ${target.label}?`) && void run(() => deleteTarget(target.id), "Target deleted.")}>
            <Trash2 size={14} /></button>
        </div>}
      </article>)}
    </section>}

    {tab === "standards" && <section className="capability-group environment-editor">
      {standards.map(item => <label key={item.key} className="wide">{item.label} <code>{item.key}</code>
        <input value={settings[item.key] ?? ""} dir="ltr" disabled={!manage} onChange={event => setSettings({ ...settings, [item.key]: event.target.value })} />
        <small>{item.hint}</small></label>)}
      <div className="wide"><button className="primary-button" disabled={!manage} onClick={() => void run(() => saveEnvironmentSettings(settings), "Standards saved.")}>
        <Save size={14} />Save standards</button></div>
    </section>}

    {tab === "incidents" && <IncidentPanel incidents={incidents} targets={targets} onRun={run} />}
  </div>;
}

function IncidentPanel({ incidents, targets, onRun }: { incidents: Incident[]; targets: EnvironmentTarget[];
  onRun: (work: () => Promise<unknown>, done: string) => Promise<void> }) {
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [affected, setAffected] = useState<string[]>([]);
  const labelOf = (id: string) => targets.find(target => target.id === id)?.label ?? id;
  return <section className="capability-group">
    <p className="capability-note">Declare an outage so the agent tells users it is known instead of repairing their PCs. Resolve it when service is back.</p>
    <div className="environment-editor">
      <label className="wide">Title<input value={title} dir="auto" onChange={event => setTitle(event.target.value)} placeholder="Finance app is down" /></label>
      <label className="wide">What users should hear<input value={description} dir="auto" onChange={event => setDescription(event.target.value)} /></label>
      <fieldset className="wide incident-targets"><legend>Affected targets</legend>
        {targets.filter(target => networkKinds.includes(target.kind)).map(target => <label key={target.id}><input type="checkbox" checked={affected.includes(target.id)}
          onChange={event => setAffected(event.target.checked ? [...affected, target.id] : affected.filter(id => id !== target.id))} />
          <span dir="auto">{target.label}</span></label>)}</fieldset>
      <div className="wide"><button className="primary-button" disabled={!title.trim()}
        onClick={() => void onRun(async () => { await createIncident(title, description, affected); setTitle(""); setDescription(""); setAffected([]); },
          "Incident declared.")}><Siren size={14} />Declare incident</button></div>
    </div>
    {incidents.map(incident => <article key={incident.incidentId} className="file-row">
      <div className="file-row-main">
        <div className="file-row-title"><strong dir="auto">{incident.title}</strong>
          <span className={`ticket-status ${incident.status.toLowerCase()}`}>{incident.status}</span></div>
        {incident.description && <p className="file-description" dir="auto">{incident.description}</p>}
        <p className="file-compatibility">{new Date(incident.startedAtUtc).toLocaleString()}
          {incident.affectedTargets.length > 0 && ` · ${incident.affectedTargets.map(labelOf).join(", ")}`}</p>
      </div>
      <button className="secondary-button" onClick={() => void onRun(() => setIncidentStatus(incident.incidentId,
        incident.status === "Active" ? "Resolved" : "Active"), "Incident updated.")}>
        {incident.status === "Active" ? "Resolve" : "Reopen"}</button>
    </article>)}
  </section>;
}
