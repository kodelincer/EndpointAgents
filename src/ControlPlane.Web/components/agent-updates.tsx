"use client";

import { CircleAlert, CloudDownload, RefreshCw, Save } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";
import { deviceState, getAgentUpdates, saveAgentUpdateSettings, summarize, targetFor,
  type AgentUpdateOverview, type AgentUpdateSettings, type DeviceUpdateState } from "@/lib/agent-updates";

const REFRESH_MS = 30000;

const stateLabel: Record<DeviceUpdateState, string> = {
  "up-to-date": "Up to date", waiting: "Waiting", updating: "Updating",
  "rolled-back": "Rolled back", failed: "Failed", unknown: "Not reported"
};
const stateClass: Record<DeviceUpdateState, string> = {
  "up-to-date": "resolved", waiting: "open", updating: "new", "rolled-back": "escalated", failed: "escalated", unknown: ""
};

const hours = Array.from({ length: 24 }, (_, hour) => hour);

/**
 * Admins choose which endpoint agent version PCs run: a pilot version for a few PCs first,
 * then a version for everyone. PCs update themselves when nobody is chatting and roll back
 * by themselves when the new version fails.
 */
export function AgentUpdates() {
  const { can } = useAuth();
  const [data, setData] = useState<AgentUpdateOverview>();
  const [draft, setDraft] = useState<AgentUpdateSettings>();
  const [error, setError] = useState<string>();
  const [saved, setSaved] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  const refresh = useCallback(async (resetDraft = false) => {
    try {
      const next = await getAgentUpdates();
      setData(next);
      setDraft(current => resetDraft || !current ? next.settings : current);
      setError(undefined);
    } catch (reason) { setError(reason instanceof Error ? reason.message : "Agent updates could not be loaded."); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(true), 0);
    const timer = window.setInterval(() => void refresh(), REFRESH_MS);
    return () => { window.clearTimeout(initial); window.clearInterval(timer); };
  }, [refresh]);

  if (!can("devices.manage"))
    return <div className="file-page"><section className="file-empty"><h2>Admins only</h2>
      <p>Only admins choose which agent version the PCs run.</p></section></div>;

  const ready = (data?.packages ?? []).filter(item => item.status === "Ready");
  const settings = data?.settings;
  const counts = data && settings ? summarize(data.devices, settings) : undefined;

  const save = async () => {
    if (!draft) return;
    if (draft.targetVersion && draft.targetVersion !== settings?.targetVersion
      && !window.confirm(`Update every PC to ${draft.targetVersion}? Try it on pilot PCs first if you have not.`)) return;
    setSaving(true); setSaved(undefined);
    try {
      const result = await saveAgentUpdateSettings(draft);
      setDraft(result); setSaved("Saved. Online PCs check within a minute.");
      await refresh(true);
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The settings were not saved."); }
    finally { setSaving(false); }
  };

  const togglePilot = (agentId: string) => draft && setDraft({
    ...draft,
    pilotAgentIds: draft.pilotAgentIds.includes(agentId)
      ? draft.pilotAgentIds.filter(id => id !== agentId) : [...draft.pilotAgentIds, agentId]
  });

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="agent-updates-title">
      <div><div className="file-heading-mark"><CloudDownload size={17} /></div><div>
        <h1 id="agent-updates-title">{pageName("/agent-updates/")}</h1></div></div>
      <button className="secondary-button" onClick={() => { setLoading(true); void refresh(); }} disabled={loading}>
        <RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>
    <PageIntro route="/agent-updates/" />

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Agent updates are unavailable</strong><span>{error}</span></div>
      <button onClick={() => void refresh()}>Retry</button></div>}

    {draft && <>
      <h2 className="section-title">Rollout</h2>
      <section className="file-list" aria-label="Rollout settings"><article className="file-row">
        <div className="file-row-main">
          <div className="file-row-title"><label>Pilot version{" "}
            <select value={draft.pilotVersion ?? ""} onChange={event => setDraft({ ...draft, pilotVersion: event.target.value || null })}>
              <option value="">None</option>
              {ready.map(item => <option key={item.version} value={item.version}>{item.version}</option>)}
            </select></label>
            <span className="muted">for {draft.pilotAgentIds.length} pilot PC(s), ticked in the table below</span></div>
          <div className="file-row-title"><label>Version for everyone{" "}
            <select value={draft.targetVersion ?? ""} onChange={event => setDraft({ ...draft, targetVersion: event.target.value || null })}>
              <option value="">Keep what they have</option>
              {ready.map(item => <option key={item.version} value={item.version}>{item.version}</option>)}
            </select></label></div>
          <div className="file-row-title"><label>Update only between{" "}
            <select value={draft.windowStartHour ?? ""} aria-label="Window start hour"
              onChange={event => setDraft({ ...draft, windowStartHour: event.target.value === "" ? null : Number(event.target.value),
                windowEndHour: event.target.value === "" ? null : draft.windowEndHour ?? 17 })}>
              <option value="">any time</option>
              {hours.map(hour => <option key={hour} value={hour}>{hour}:00</option>)}
            </select>
            {draft.windowStartHour !== null && <>{" "}and{" "}
              <select value={draft.windowEndHour ?? ""} aria-label="Window end hour"
                onChange={event => setDraft({ ...draft, windowEndHour: Number(event.target.value) })}>
                {hours.map(hour => <option key={hour} value={hour}>{hour}:00</option>)}
              </select> (server time)</>}</label></div>
          <label className="package-toggle"><input type="checkbox" checked={draft.paused}
            onChange={event => setDraft({ ...draft, paused: event.target.checked })} /> Pause all updates</label>
          {settings?.updatedBy && <p className="file-compatibility">Last changed by {settings.updatedBy}
            {settings.updatedAtUtc && <> · {new Date(settings.updatedAtUtc).toLocaleString()}</>}</p>}
          {saved && <p className="file-compatibility">{saved}</p>}
        </div>
        <div className="file-row-actions">
          <button className="primary-button" disabled={saving} onClick={() => void save()}>
            <Save size={14} />{saving ? "Saving…" : "Save"}</button>
        </div>
      </article></section>
    </>}

    <h2 className="section-title">Versions in the file repository</h2>
    {(data?.packages.length ?? 0) === 0
      ? <section className="file-empty"><span className="empty-icon"><CloudDownload size={23} /></span>
        <h2>No agent versions yet</h2><p>Run <code>scripts\new-agent-update.ps1 -Version 0.3.0 -Sign</code> on the admin PC.
          It builds the service, the Tray and the updater and puts the signed version here.</p></section>
      : <section className="file-list" aria-label="Agent versions">{data!.packages.map(item =>
        <article key={item.version} className="file-row"><div className="file-row-main">
          <div className="file-row-title"><strong>{item.version}</strong>
            <span className={`ticket-status ${item.status === "Ready" ? "resolved" : "escalated"}`}>{item.status}</span>
            {item.signedBy && <code>key {item.signedBy}</code>}</div>
          {item.releaseNotes && <p className="file-description" dir="auto">{item.releaseNotes}</p>}
          {item.problem && <p className="file-description">{item.problem}</p>}
          <p className="file-compatibility">{item.sizeBytes ? `${(item.sizeBytes / 1024 / 1024).toFixed(1)} MB · ` : ""}
            {item.minWindowsBuild ? `Windows build ${item.minWindowsBuild} or newer` : ""}</p>
        </div></article>)}</section>}

    <h2 className="section-title">PCs{counts && <span className="muted"> · {counts["up-to-date"]} up to date
      · {counts.waiting} waiting · {counts.updating} updating · {counts["rolled-back"] + counts.failed} need a look</span>}</h2>
    {data && draft && <section className="file-list" aria-label="PCs">{data.devices.map(device => {
      const state = deviceState(device, data.settings);
      return <article key={device.agentId} className="file-row"><div className="file-row-main">
        <div className="file-row-title"><strong>{device.machineName || device.agentId.slice(0, 8)}</strong>
          <span className={`ticket-status ${stateClass[state]}`}>{stateLabel[state]}</span>
          <code>{device.currentVersion ?? "?"}</code>{!device.online && <span className="muted">offline</span>}</div>
        <p className="file-compatibility">Target {targetFor(device, data.settings) ?? "none"}
          {device.lastCheckUtc && <> · last check {new Date(device.lastCheckUtc).toLocaleString()}</>}
          {device.lastStatus && <> · last update {device.lastFromVersion} → {device.lastToVersion}: {device.lastStatus}</>}</p>
        {device.lastMessage && (state === "rolled-back" || state === "failed")
          && <p className="file-description">{device.lastMessage}</p>}
      </div>
      <div className="file-row-actions">
        <label className="package-toggle"><input type="checkbox" checked={draft.pilotAgentIds.includes(device.agentId)}
          onChange={() => togglePilot(device.agentId)} /> Pilot</label>
      </div></article>;
    })}</section>}
  </div>;
}
