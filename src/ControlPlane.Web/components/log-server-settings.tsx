"use client";

import { CircleAlert, Save, Send } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { defaultsFor, getLogServer, isUnencrypted, protocolsFor, saveLogServer, testLogServer,
  type LogServerSettings, type LogServerState, type LogServerType } from "@/lib/problems";

const when = (value?: string | null) => value ? new Date(value).toLocaleString() : "never";

const typeHelp: Record<LogServerType, string> = {
  Splunk: "Splunk HTTP Event Collector. In Splunk: Settings > Data inputs > HTTP Event Collector > New Token.",
  Graylog: "Graylog GELF over HTTP. In Graylog: System > Inputs > GELF HTTP.",
  Syslog: "Syslog RFC 5424, for ELK, Wazuh, rsyslog or anything else that reads syslog."
};

/** Admins choose an outside log server. The control plane sends a copy of every problem report there. */
export function LogServerSection() {
  const [state, setState] = useState<LogServerState>();
  const [draft, setDraft] = useState<LogServerSettings>();
  const [token, setToken] = useState("");
  const [error, setError] = useState<string>();
  const [note, setNote] = useState<string>();
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (reset: boolean) => {
    try {
      const next = await getLogServer();
      setState(next);
      setDraft(current => reset || !current ? next.settings : current);
      setError(undefined);
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The settings could not be loaded."); }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void load(true), 0);
    const timer = window.setInterval(() => void load(false), 15000);
    return () => { window.clearTimeout(initial); window.clearInterval(timer); };
  }, [load]);

  const update = (change: Partial<LogServerSettings>) => draft && setDraft({ ...draft, ...change });
  const run = async (action: () => Promise<void>) => {
    setBusy(true); setNote(undefined); setError(undefined);
    try { await action(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "That did not work."); }
    finally { setBusy(false); }
  };
  const save = () => run(async () => {
    if (!draft) return;
    const result = await saveLogServer(draft, token);
    setState(result); setDraft(result.settings); setToken(""); setNote("Saved.");
  });
  const test = () => run(async () => {
    if (!draft) return;
    const result = await testLogServer(draft, token);
    if (result.ok) setNote(result.message); else setError(result.message);
  });
  const status = state?.status;

  return <>
    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>That did not work</strong><span>{error}</span></div></div>}
    {status?.down && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>The log server is not reachable</strong>
        <span>{status.lastError} Reports are still saved here.</span></div></div>}

    <h2 className="section-title">Log server</h2>
    {draft && <section className="file-list" aria-label="Log server"><article className="file-row"><div className="file-row-main">
      <p className="file-compatibility">The control plane, not the PCs, sends a copy of every problem report here, so only this
        server needs a firewall rule. Reports are cleaned on the PC first. If the log server is down, copies wait in a small
        queue and the oldest are dropped; the PCs and the dashboard are never held up. Off by default.</p>
      <label className="package-toggle"><input type="checkbox" checked={draft.enabled}
        onChange={event => update({ enabled: event.target.checked })} /> Send reports to this log server</label>
      <div className="settings-grid">
        <label>Type<select value={draft.type} onChange={event => {
          const type = event.target.value as LogServerType;
          update({ type, ...defaultsFor(type) });
        }}>
          <option value="Splunk">Splunk</option><option value="Graylog">Graylog</option><option value="Syslog">Syslog (ELK, Wazuh, rsyslog)</option>
        </select></label>
        <label>Address<input value={draft.host} placeholder="logs.example.local" onChange={event => update({ host: event.target.value })} /></label>
        <label>Port<input type="number" min={1} max={65535} value={draft.port} onChange={event => update({ port: Number(event.target.value) })} /></label>
        <label>Protocol<select value={draft.protocol} onChange={event => update({ protocol: event.target.value })}>
          {protocolsFor(draft.type).map(protocol => <option key={protocol} value={protocol}>{protocol.toUpperCase()}</option>)}
        </select></label>
        {draft.type === "Splunk" && <label>Index (optional)<input value={draft.index ?? ""} onChange={event => update({ index: event.target.value })} /></label>}
        {draft.type !== "Syslog" && <label>{draft.type === "Splunk" ? "HEC token" : "Token (optional)"}
          <input type="password" autoComplete="off" value={token} onChange={event => setToken(event.target.value)}
            placeholder={state?.hasToken ? "Saved. Leave empty to keep it." : ""} /></label>}
        {!isUnencrypted(draft) && <label>Certificate fingerprint (optional)<input value={draft.certificateThumbprint ?? ""}
          placeholder="SHA-256, only for a server with its own certificate"
          onChange={event => update({ certificateThumbprint: event.target.value })} /></label>}
      </div>
      <p className="file-compatibility">{typeHelp[draft.type]}</p>
      {isUnencrypted(draft) && <p className="file-compatibility">This protocol is not encrypted: anyone on the network path can read the reports.</p>}
      {note && <p className="file-compatibility" role="status">{note}</p>}
      {status?.enabled && <p className="file-compatibility">Last success {when(status.lastSuccessUtc)}
        {status.lastError && <> · last error {when(status.lastErrorUtc)}: {status.lastError}</>}
        {" "}· {status.sent} sent · {status.waiting} waiting · {status.dropped} dropped</p>}
      {draft.updatedBy && <p className="file-compatibility">Last changed by {draft.updatedBy}
        {draft.updatedAtUtc && <> · {new Date(draft.updatedAtUtc).toLocaleString()}</>}</p>}
    </div>
    <div className="file-row-actions">
      <button className="secondary-button" disabled={busy || !draft.host.trim()} onClick={() => void test()}><Send size={14} />Send test</button>
      <button className="primary-button" disabled={busy} onClick={() => void save()}><Save size={14} />{busy ? "Working…" : "Save"}</button>
    </div></article></section>}
  </>;
}
