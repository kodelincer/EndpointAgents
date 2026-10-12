import { useState } from "react";
import { LoaderCircle, Pause, Play, Power, RotateCw } from "lucide-react";
import { host } from "./bridge";
import { connectionLabel, healthTone, relative, serviceLabel } from "./format";
import type { ServiceActionResult, TrayStatus } from "./types";

type Tone = "ok" | "warn" | "bad" | "idle";
type Action = "start" | "stop" | "pause" | "resume";

/**
 * The route a request travels from this PC to the helpdesk, drawn as a line of stops so the first
 * broken stop shows where help is blocked. Service controls sit apart from the readout.
 */
export function StatusPage({ status }: { status: TrayStatus | null }) {
  const [running, setRunning] = useState<Action | null>(null);
  const [result, setResult] = useState("");
  const [confirmStop, setConfirmStop] = useState(false);
  const runtime = status?.runtime;
  const operational = status?.service === "Running" || status?.service === "Interactive";

  const stops: { name: string; value: string; tone: Tone }[] = [
    {
      name: "سرویس پس‌زمینه",
      value: status ? serviceLabel[status.service] : "در حال بررسی",
      tone: !status ? "idle" : operational ? "ok" : status.service === "Stopped" || status.service === "NotInstalled" ? "bad" : "warn",
    },
    {
      name: "کانال محلی",
      value: runtime ? "برقرار" : operational ? "پاسخ نمی‌دهد" : "—",
      tone: runtime ? "ok" : operational ? "bad" : "idle",
    },
    {
      name: "سرور پشتیبانی",
      value: runtime ? connectionLabel[runtime.connectionStatus] : "—",
      tone: !runtime ? "idle" : runtime.connectionStatus === "Connected" ? "ok"
        : runtime.connectionStatus === "Faulted" || runtime.connectionStatus === "Disconnected" ? "bad" : "warn",
    },
    {
      name: "عملکردها",
      value: runtime ? (runtime.functionMode === "Active" ? "فعال" : "در مکث") : "—",
      tone: !runtime ? "idle" : runtime.functionMode === "Active" ? "ok" : "warn",
    },
  ];

  const can: Record<Action, boolean> = {
    start: !running && status?.service === "Stopped",
    stop: !running && status?.service === "Running",
    pause: !running && operational && runtime?.functionMode === "Active",
    resume: !running && operational && runtime?.functionMode === "Paused",
  };

  const run = async (action: Action) => {
    setConfirmStop(false);
    setRunning(action);
    setResult("در انتظار تأیید مدیر سیستم…");
    try {
      const outcome = await host.call<ServiceActionResult>("service.control", { action });
      setResult(outcome.message);
    } catch {
      setResult("عملیات مدیریتی کامل نشد. برای جزئیات، گزارش‌ها را بررسی کنید.");
    } finally {
      setRunning(null);
    }
  };

  const lastCommand = runtime?.lastCommandMethod
    ? <><bdi className="ltr">{runtime.lastCommandMethod}</bdi>، {relative(runtime.lastCommandAtUtc)}
      {runtime.lastCommandSucceeded === false ? "، ناموفق" : ""}</>
    : "ثبت نشده";

  return (
    <div className="page status-page">
      <header className="page-head">
        <div>
          <h1>وضعیت و کنترل</h1>
          <p className="muted">مسیر درخواست شما از این رایانه تا پشتیبانی</p>
        </div>
        <button className="button" onClick={() => host.call("status.refresh")}>
          <RotateCw size={15} aria-hidden />بررسی دوباره
        </button>
      </header>

      <section className="verdict" data-tone={status ? healthTone(status.health) : "idle"} aria-live="polite">
        <h2>{status?.label ?? "در حال بررسی وضعیت"}</h2>
        <p>{status?.detail ?? "وضعیت سرویس پس‌زمینه در حال دریافت است."}</p>
      </section>

      <ol className="route" aria-label="مسیر اتصال">
        {stops.map((stop) => (
          <li key={stop.name} className="route-stop" data-tone={stop.tone}>
            <span className="route-dot" aria-hidden />
            <span className="route-name">{stop.name}</span>
            <span className="route-value">{stop.value}</span>
          </li>
        ))}
      </ol>

      <dl className="facts wide">
        <div><dt>نام رایانه</dt><dd><bdi className="ltr">{runtime?.machineName ?? "—"}</bdi></dd></div>
        <div><dt>نسخه دستیار</dt><dd><bdi className="ltr">{runtime?.version ?? "—"}</bdi></dd></div>
        <div><dt>آخرین اتصال</dt><dd>{relative(runtime?.lastConnectedAtUtc)}</dd></div>
        <div><dt>آخرین عملکرد</dt><dd>{lastCommand}</dd></div>
        <div className="span"><dt>آخرین مشکل</dt><dd>{runtime?.lastProblemMessage
          ? <>{runtime.lastProblemMessage}، {relative(runtime.lastProblemAtUtc)}</> : "مشکلی ثبت نشده"}</dd></div>
      </dl>

      <section className="controls" aria-labelledby="controls-title">
        <h2 id="controls-title">کنترل سرویس</h2>
        <p className="muted">این کارها به تأیید مدیر سیستم نیاز دارند.</p>
        <div className="control-row">
          <button className="button" disabled={!can.start} onClick={() => run("start")}>
            {running === "start" ? <LoaderCircle className="spin" size={15} /> : <Power size={15} aria-hidden />}راه‌اندازی
          </button>
          <button className="button" disabled={!can.pause} onClick={() => run("pause")}>
            {running === "pause" ? <LoaderCircle className="spin" size={15} /> : <Pause size={15} aria-hidden />}مکث عملکردها
          </button>
          <button className="button" disabled={!can.resume} onClick={() => run("resume")}>
            {running === "resume" ? <LoaderCircle className="spin" size={15} /> : <Play size={15} aria-hidden />}ادامه عملکردها
          </button>
          {confirmStop ? (
            <span className="confirm-inline" role="group" aria-label="تأیید توقف سرویس">
              با توقف سرویس، این رایانه تا راه‌اندازی دوباره از پشتیبانی قطع می‌شود.
              <button className="button danger" onClick={() => run("stop")}>توقف سرویس</button>
              <button className="button" onClick={() => setConfirmStop(false)}>انصراف</button>
            </span>
          ) : (
            <button className="button danger-quiet" disabled={!can.stop} onClick={() => setConfirmStop(true)}>
              {running === "stop" ? <LoaderCircle className="spin" size={15} /> : <Power size={15} aria-hidden />}توقف سرویس
            </button>
          )}
        </div>
        {result && <p className="notice" role="status">{result}</p>}
      </section>
    </div>
  );
}
