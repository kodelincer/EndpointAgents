"use client";

import { CircleAlert, RefreshCw, Save, Settings } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { getHermesQueue, saveHermesQueue } from "@/lib/control-plane";
import { queueSettingsErrors, waitedFor } from "@/lib/queue-settings";
import type { HermesQueueSettings, HermesQueueStatus } from "@/lib/types";

type Draft = Record<keyof HermesQueueSettings, string>;

const fields: { key: keyof HermesQueueSettings; label: string; help: string; unit?: string }[] = [
  { key: "maxParallelRuns", label: "Max parallel requests to Hermes",
    help: "How many user requests the support agent works on at the same time. Lowering it lets running requests finish first." },
  { key: "maxQueueLength", label: "Max queue length",
    help: "How many requests may wait. When the line is full, new messages are refused and the Tray asks the user to try again in a few minutes." },
  { key: "maxWaitMinutes", label: "Max waiting time", unit: "minutes",
    help: "A request that waits longer gets a polite busy reply, so nobody waits forever." },
];

function toDraft(settings: HermesQueueSettings): Draft {
  return { maxParallelRuns: String(settings.maxParallelRuns), maxQueueLength: String(settings.maxQueueLength),
    maxWaitMinutes: String(settings.maxWaitMinutes) };
}

/** Admin limits for the control plane's request queue in front of Hermes. */
export function QueueSettings() {
  const [status, setStatus] = useState<HermesQueueStatus>();
  const [draft, setDraft] = useState<Draft>();
  const [error, setError] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  const refresh = useCallback(async (resetDraft: boolean) => {
    try {
      const next = await getHermesQueue();
      setStatus(next);
      if (resetDraft) setDraft(toDraft(next.settings));
      setError(undefined);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Settings could not be loaded.");
    }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(true), 0);
    const live = window.setInterval(() => void refresh(false), 5000);
    return () => { window.clearTimeout(initial); window.clearInterval(live); };
  }, [refresh]);

  const errors = draft ? queueSettingsErrors(draft) : {};
  const invalid = Object.keys(errors).length > 0;
  const changed = !!draft && !!status && JSON.stringify(draft) !== JSON.stringify(toDraft(status.settings));

  const save = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!draft || invalid) return;
    setSaving(true);
    try {
      const next = await saveHermesQueue({ maxParallelRuns: Number(draft.maxParallelRuns),
        maxQueueLength: Number(draft.maxQueueLength), maxWaitMinutes: Number(draft.maxWaitMinutes) });
      setStatus(next);
      setDraft(toDraft(next.settings));
      setSaved(true);
      window.setTimeout(() => setSaved(false), 2500);
      setError(undefined);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Settings could not be saved.");
    } finally {
      setSaving(false);
    }
  };

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="settings-title">
      <div><div className="file-heading-mark"><Settings size={17} /></div><div>
        <h1 id="settings-title">Settings</h1>
        <p>Limits for the request queue between users and the support agent. Changes apply right away.</p></div></div>
      <button className="secondary-button" onClick={() => void refresh(!changed)}>
        <RefreshCw size={15} />Refresh</button>
    </section>

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Settings are unavailable</strong><span>{error}</span></div>
      <button onClick={() => void refresh(true)}>Retry</button></div>}

    {status && <div className="queue-live" role="status" aria-live="polite">
      <div><span>Working now</span><strong>{status.running}<small> / {status.settings.maxParallelRuns}</small></strong></div>
      <div><span>Waiting</span><strong>{status.waiting}<small> / {status.settings.maxQueueLength}</small></strong></div>
      <p>{waitedFor(status.oldestWaitingSinceUtc) || "Nobody is waiting."}</p>
    </div>}

    {!draft ? <div className="file-list" aria-label="Loading settings"><div className="file-skeleton" /></div>
      : <form className="settings-card" onSubmit={save} noValidate>
        <h2>Request queue</h2>
        {fields.map(({ key, label, help, unit }) => <div className="settings-field" key={key}>
          <label htmlFor={key}><strong>{label}</strong><span id={`${key}-help`}>{help}</span></label>
          <div className="settings-input">
            <input id={key} inputMode="numeric" value={draft[key]} aria-describedby={`${key}-help`}
              aria-invalid={!!errors[key]} onChange={event => setDraft({ ...draft, [key]: event.target.value })} />
            {unit && <span>{unit}</span>}
            {errors[key] && <small role="alert">{errors[key]}</small>}
          </div>
        </div>)}
        <div className="settings-actions">
          {saved && <span role="status">Saved</span>}
          <button type="button" className="secondary-button" disabled={!changed || saving}
            onClick={() => status && setDraft(toDraft(status.settings))}>Discard</button>
          <button className="primary-button" disabled={!changed || saving || invalid}>
            <Save size={15} />{saving ? "Saving…" : "Save"}</button>
        </div>
      </form>}
  </div>;
}
