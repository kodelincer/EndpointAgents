"use client";

import { CircleAlert, KeyRound, PlugZap, RefreshCw, Save, Settings as SettingsIcon } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { LogServerSection } from "@/components/log-server-settings";
import { getKmsSettings, kmsInputError, saveKmsSettings, testKmsServer, type KmsTestResult } from "@/lib/capabilities";

function testText(result: KmsTestResult) {
  if (!result.nameResolves) return `${result.host} does not resolve from the control-plane server.`;
  return result.open ? `${result.host}:${result.port} answers from the control-plane server (${result.milliseconds ?? 0} ms).`
    : `${result.host}:${result.port} does not answer from the control-plane server.`;
}

/** Organisation-wide settings: the KMS server for activation and, for admins, the outside log server. */
export function SettingsPage() {
  const { can } = useAuth();
  const manage = can("environment.manage");
  const [host, setHost] = useState("");
  const [port, setPort] = useState("");
  const [defaultPort, setDefaultPort] = useState(1688);
  const [configured, setConfigured] = useState(false);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [test, setTest] = useState<KmsTestResult>();

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      const kms = await getKmsSettings();
      setHost(kms.host); setPort(kms.port); setDefaultPort(kms.defaultPort); setConfigured(kms.configured); setError(undefined);
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The settings could not be loaded."); }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { const id = window.setTimeout(() => void refresh(), 0); return () => window.clearTimeout(id); }, [refresh]);

  const inputError = kmsInputError(host, port);
  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    try { await work(); setError(undefined); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "The change was not saved."); }
    finally { setBusy(false); }
  };
  const save = () => void run(async () => {
    await saveKmsSettings(host, port);
    setNotice(host.trim() ? "KMS server saved. The agent now activates Windows and Office with it." : "KMS server cleared.");
    await refresh();
  });
  const check = () => void run(async () => { setTest(undefined); setTest(await testKmsServer(host, port)); });

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="settings-title">
      <div><div className="file-heading-mark"><SettingsIcon size={17} /></div><div>
        <h1 id="settings-title">Settings</h1>
        <p>Organisation settings the endpoint actions use. The agent never sees these addresses; it only runs the catalogued
          actions that read them.</p></div></div>
      <button className="secondary-button" onClick={() => void refresh()} disabled={loading}>
        <RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Not saved</strong><span>{error}</span></div><button onClick={() => setError(undefined)}>Dismiss</button></div>}
    {notice && !error && <p className="success-text" role="status">{notice}</p>}

    <section className="capability-group environment-editor" aria-labelledby="kms-title">
      <h2 id="kms-title" className="wide"><KeyRound size={15} /> KMS server for Windows and Office activation</h2>
      <p className="capability-note wide">When this is set, the agent first checks that the PC reaches this server, then activates
        volume Windows and Office (2016 and later, Click-to-Run included) with it. If the server does not answer, it falls back to
        the activation packages or hands the ticket to the helpdesk. {configured ? "Status: set." : "Status: not set."}</p>
      <label>KMS server<input value={host} dir="ltr" placeholder="kms.corp.local or 172.16.0.2" disabled={!manage}
        onChange={event => { setHost(event.target.value); setTest(undefined); setNotice(undefined); }} />
        <small>Host name or IPv4 address, without a port.</small></label>
      <label>Port<input value={port} dir="ltr" inputMode="numeric" placeholder={String(defaultPort)} disabled={!manage}
        onChange={event => { setPort(event.target.value); setTest(undefined); setNotice(undefined); }} />
        <small>Leave empty for {defaultPort}.</small></label>
      {inputError && <p className="danger-text wide" role="alert">{inputError}</p>}
      {test && <p className={`wide ${test.open ? "success-text" : "danger-text"}`} role="status">{testText(test)}</p>}
      {manage ? <div className="wide file-row-actions">
        <button className="secondary-button" disabled={busy || !host.trim() || !!inputError} onClick={check}>
          <PlugZap size={14} />Test from server</button>
        <button className="primary-button" disabled={busy || !!inputError} onClick={save}><Save size={14} />Save</button>
      </div> : <p className="capability-note wide">Only an admin can change these settings.</p>}
    </section>

    {can("users.manage") && <LogServerSection />}
  </div>;
}
