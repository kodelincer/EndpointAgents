"use client";

import { BookOpen, CircleAlert, Languages, ListChecks, RefreshCw, RotateCcw, Search, Send, Wrench } from "lucide-react";
import Link from "next/link";
import { useAuth } from "@/components/auth-gate";
import { useCallback, useEffect, useMemo, useState } from "react";
import { areaText, filterActions, getActionRuns, getCapabilities, groupByArea, requestMissingAction,
  saveActionSetting, successRate, tierText, undoActionRun, type ActionInfo, type ActionRun, type Capabilities,
  type Language } from "@/lib/capabilities";

type Tab = "actions" | "tools" | "howto" | "runs";

const toolText: Record<string, { en: string; fa: string }> = {
  run_endpoint_action: { en: "Runs one action from this page on the user's PC.", fa: "یکی از کارهای این صفحه را روی کامپیوتر کاربر اجرا می‌کند." },
  list_endpoint_actions: { en: "Lists the actions the agent may use (switched-off ones are hidden).", fa: "فهرست کارهای مجاز را به عامل می‌دهد." },
  list_environment_targets: { en: "Lists servers, web apps, printers and shares by id and label.", fa: "سرورها، سامانه‌ها، چاپگرها و پوشه‌ها را با شناسه نشان می‌دهد." },
  check_known_incidents: { en: "Known outages declared on the Environment page.", fa: "قطعی‌های اعلام‌شده در صفحه محیط." },
  recent_reports_from_others: { en: "What other users reported recently.", fa: "گزارش‌های اخیر کاربران دیگر." },
  probe_from_server: { en: "Checks a target from the server, to tell a PC fault from an outage.", fa: "هدف را از سرور بررسی می‌کند." },
  check_user_account: { en: "Read-only domain account state (locked, expired). Never unlocks.", fa: "وضعیت حساب دامنه را فقط می‌خواند." },
  note_skill_used: { en: "Records which skill guided a conversation (for skill statistics).", fa: "ثبت مهارت استفاده‌شده برای آمار." },
  get_site_knowledge: { en: "The organization knowledge file (network, services, rules), with internal names masked.", fa: "فایل شناخت سازمان (شبکه، سرویس‌ها، قوانین) با نام‌های داخلی پنهان‌شده." },
  endpoint_system_info: { en: "PC configuration: network, Windows, domain, hardware, disks.", fa: "پیکربندی کامپیوتر." },
  endpoint_get_info: { en: "Basic PC, internet, proxy and time facts.", fa: "اطلاعات پایه کامپیوتر." },
  find_repair_packages: { en: "Administrator-approved BAT/EXE repair packages and their symptoms.", fa: "بسته‌های تعمیر تأییدشده." },
  run_repair_package: { en: "Runs one approved repair package.", fa: "یک بسته تعمیر را اجرا می‌کند." },
  list_repair_attempts: { en: "Fixes already tried in this conversation.", fa: "تعمیرهای انجام‌شده در این گفتگو." },
  escalate_to_helpdesk: { en: "Hands the conversation to the helpdesk.", fa: "ارجاع به پشتیبانی." },
  record_feature_request: { en: "Saves a request for something the system cannot do yet.", fa: "ثبت درخواست قابلیت جدید." },
  mark_problem_resolved: { en: "Closes the conversation after the user confirms.", fa: "بستن گفتگو پس از تأیید کاربر." }
};

/** The helpdesk's reference: everything the support agent can do, generated from the live catalogue. */
export function CapabilitiesPage() {
  const { can } = useAuth();
  const [data, setData] = useState<Capabilities>();
  const [runs, setRuns] = useState<ActionRun[]>([]);
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [language, setLanguage] = useState<Language>("en");
  const [tab, setTab] = useState<Tab>("actions");
  const [query, setQuery] = useState("");
  const [area, setArea] = useState("all");
  const [tier, setTier] = useState("all");
  const [busy, setBusy] = useState<string>();

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      const [capabilities, recent] = await Promise.all([getCapabilities(), getActionRuns(100)]);
      setData(capabilities); setRuns(recent); setError(undefined);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "The catalogue could not be loaded.");
    } finally { setLoading(false); }
  }, []);

  useEffect(() => { const id = window.setTimeout(() => void refresh(), 0); return () => window.clearTimeout(id); }, [refresh]);

  const groups = useMemo(() => data ? groupByArea(filterActions(data.actions, query, area, tier), data.areas) : [],
    [data, query, area, tier]);
  const fa = language === "fa";

  const update = async (action: ActionInfo, enabled: boolean, maxPerHour: number | null) => {
    setBusy(action.id);
    try {
      await saveActionSetting(action.id, enabled, maxPerHour);
      setData(current => current && { ...current, actions: current.actions.map(item => item.id === action.id
        ? { ...item, enabled, maxPerHour: maxPerHour ?? item.defaultMaxPerHour } : item) });
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The setting could not be saved."); }
    finally { setBusy(undefined); }
  };

  const undo = async (run: ActionRun) => {
    setBusy(run.runId);
    try { await undoActionRun(run.runId); await refresh(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Undo failed."); }
    finally { setBusy(undefined); }
  };

  return <div className="file-page" dir={fa ? "rtl" : "ltr"} lang={language}>
    <section className="file-heading" aria-labelledby="capabilities-title">
      <div><div className="file-heading-mark"><BookOpen size={17} /></div><div>
        <h1 id="capabilities-title">{fa ? "توانایی‌های سیستم" : "Capabilities"}</h1>
        <p>{fa ? "هر کاری که عامل پشتیبانی می‌تواند روی کامپیوتر کاربر انجام دهد. این صفحه از فهرست زنده ساخته می‌شود؛ برای نوشتن مهارت از آن استفاده کنید."
          : "Everything the support agent can do on a user's PC, generated from the live catalogue. Use it to write skills."}</p></div></div>
      <div className="capability-heading-actions">
        <button className="secondary-button" onClick={() => setLanguage(fa ? "en" : "fa")}>
          <Languages size={15} />{fa ? "English" : "فارسی"}</button>
        <button className="secondary-button" onClick={() => void refresh()} disabled={loading}>
          <RefreshCw className={loading ? "spin" : ""} size={15} />{fa ? "تازه‌سازی" : "Refresh"}</button>
      </div>
    </section>

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>{fa ? "خطا" : "Something went wrong"}</strong><span>{error}</span></div>
      <button onClick={() => void refresh()}>{fa ? "دوباره" : "Retry"}</button></div>}

    <div className="support-filter capability-tabs" role="tablist">
      {([["actions", fa ? "کارها" : "Actions", ListChecks], ["tools", fa ? "ابزارهای عامل" : "Agent tools", Wrench],
        ["howto", fa ? "نوشتن مهارت" : "Writing a skill", BookOpen], ["runs", fa ? "اجراهای اخیر" : "Recent runs", RotateCcw]] as const)
        .map(([key, label, Icon]) => <button key={key} role="tab" aria-selected={tab === key}
          className={tab === key ? "selected" : ""} onClick={() => setTab(key)}><Icon size={14} />{label}</button>)}
    </div>

    {loading && !data ? <div className="file-list"><div className="file-skeleton" /></div> : data && <>
      {tab === "actions" && <>
        <div className="capability-filters">
          <label className="search-field"><Search size={15} /><input value={query} onChange={event => setQuery(event.target.value)}
            placeholder={fa ? "جستجو در کارها" : "Search actions"} aria-label="Search actions" /></label>
          <select value={area} onChange={event => setArea(event.target.value)} aria-label="Area">
            <option value="all">{fa ? "همه بخش‌ها" : "All areas"}</option>
            {data.areas.map(item => <option key={item} value={item}>{areaText[item]?.[language] ?? item}</option>)}</select>
          <select value={tier} onChange={event => setTier(event.target.value)} aria-label="Tier">
            <option value="all">{fa ? "همه سطح‌ها" : "All tiers"}</option>
            {(["Look", "SafeFix", "Disruptive", "OperatorOnly"] as const).map(item =>
              <option key={item} value={item}>{tierText[item][language]}</option>)}</select>
        </div>
        <p className="capability-count">{data.actions.length} {fa ? "کار" : "actions"} ·
          {" "}{data.actions.filter(item => !item.enabled).length} {fa ? "خاموش" : "switched off"}</p>
        {groups.map(group => <section key={group.area} className="capability-group" aria-label={group.area}>
          <h2>{areaText[group.area]?.[language] ?? group.area}</h2>
          {group.actions.map(action => <ActionCard key={action.id} action={action} language={language}
            busy={busy === action.id} canManage={can("environment.manage")} onChange={(enabled, max) => void update(action, enabled, max)} />)}
        </section>)}
        <MissingActionForm language={language} />
      </>}

      {tab === "tools" && <section className="capability-group">
        <p className="capability-note">{fa
          ? "عامل فقط این ابزارها را دارد. نام کامل هر ابزار در Hermes با mcp_controlplane_ شروع می‌شود. مهارت نمی‌تواند ابزار یا اجازه‌ای اضافه کند."
          : "The agent has only these tools. In Hermes each name is prefixed with mcp_controlplane_. A skill can never add a tool or a permission."}</p>
        {data.tools.map(tool => <article key={tool} className="file-row capability-tool">
          <code>{tool}</code><span>{toolText[tool]?.[language] ?? ""}</span></article>)}
        <p className="capability-note">{data.accountChecksConfigured
          ? (fa ? "بررسی حساب کاربری فعال است." : "Account checks are configured.")
          : (fa ? "بررسی حساب کاربری تنظیم نشده (ActiveDirectory در appsettings)." : "Account checks are not configured (ActiveDirectory section in appsettings).")}</p>
      </section>}

      {tab === "howto" && <HowTo language={language} publishes={data.skillsPublishToHermes} />}

      {tab === "runs" && <section className="file-list" aria-label="Recent action runs">
        {runs.length === 0 ? <p className="capability-note">{fa ? "هنوز اجرایی ثبت نشده." : "No action has run yet."}</p>
          : runs.map(run => <article key={run.runId} className="file-row capability-run">
            <div className="file-row-main">
              <div className="file-row-title"><code>{run.actionId}</code>
                <span className={`ticket-status ${run.status.toLowerCase()}`}>{run.status}</span>
                {run.undoneAtUtc && <span className="ticket-status">{fa ? "برگردانده شد" : "Undone"}</span>}</div>
              <p className="file-description" dir="auto">{run.summary}</p>
              <p className="file-compatibility">{new Date(run.startedAtUtc).toLocaleString()} · {run.source} · {run.agentId.slice(0, 8)}
                {run.conversationId && <> · <Link href={`/support/?conversation=${encodeURIComponent(run.conversationId)}`}>
                  {fa ? "گفتگو" : "Conversation"}</Link></>}</p>
            </div>
            {run.undoJson && !run.undoneAtUtc && can("actions.undo") && <button className="secondary-button" disabled={busy === run.runId}
              onClick={() => void undo(run)}><RotateCcw size={14} />{fa ? "برگرداندن" : "Undo"}</button>}
          </article>)}
      </section>}
    </>}
  </div>;
}

function ActionCard({ action, language, busy, canManage, onChange }: { action: ActionInfo; language: Language; busy: boolean;
  canManage: boolean; onChange: (enabled: boolean, maxPerHour: number | null) => void }) {
  const fa = language === "fa";
  const rate = successRate(action.stats);
  const [limit, setLimit] = useState(String(action.maxPerHour));
  return <article className={`file-row capability-action ${action.enabled ? "" : "switched-off"}`}>
    <div className="file-row-main">
      <div className="file-row-title">
        <strong>{fa ? action.nameFa : action.name}</strong><code dir="ltr">{action.id}</code>
        <span className={`tier-badge ${action.tier.toLowerCase()}`}>{tierText[action.tier][language]}</span>
        <span className="muted">{action.runAs === "User" ? (fa ? "در نشست کاربر" : "runs as the user") : (fa ? "در سرویس" : "runs as the service")}</span>
      </div>
      <p className="file-description" dir="ltr">{action.description}</p>
      <dl className="capability-facts" dir="ltr">
        <div><dt>Returns</dt><dd>{action.returns}</dd></div>
        {action.parameters.length > 0 && <div><dt>Parameters</dt><dd>{action.parameters.map(parameter =>
          <span key={parameter.name} className="capability-parameter"><code>{parameter.name}</code>
            {parameter.required ? "" : " (optional)"}: {parameter.description}
            {parameter.choices && <> [{parameter.choices.map(choice => choice.value).join(", ")}]</>}
            {parameter.targetKinds && <> (environment map: {parameter.targetKinds.join(", ")})</>}
            {parameter.kind === "Integer" && <> ({parameter.min}–{parameter.max})</>}</span>)}</dd></div>}
        {action.verifiedBy && <div><dt>Verified by</dt><dd><code>{action.verifiedBy}</code> before and after</dd></div>}
        {action.undoActionId && <div><dt>Undo</dt><dd>Helpdesk can undo from Recent runs</dd></div>}
        {action.usesSettings && action.usesSettings.length > 0 && <div><dt>Uses</dt><dd>{action.usesSettings.join(", ")} (Environment → Standards)</dd></div>}
        <div><dt>Used (30 days)</dt><dd>{action.stats ? `${action.stats.runs} runs${rate === null ? "" : `, ${rate}% succeeded`}` : "not yet"}</dd></div>
      </dl>
    </div>
    {action.tier !== "OperatorOnly" && <div className="capability-controls" dir="ltr">
      <label className="package-toggle"><input type="checkbox" checked={action.enabled} disabled={busy || !canManage}
        onChange={event => onChange(event.target.checked, Number(limit) || null)} />{action.enabled ? "On" : "Off"}</label>
      <label className="capability-limit">Max/hour per PC
        <input type="number" min={0} max={1000} value={limit} disabled={busy || !canManage} onChange={event => setLimit(event.target.value)}
          onBlur={() => Number(limit) !== action.maxPerHour && onChange(action.enabled, Number(limit) || null)} /></label>
    </div>}
  </article>;
}

function MissingActionForm({ language }: { language: Language }) {
  const fa = language === "fa";
  const [summary, setSummary] = useState("");
  const [details, setDetails] = useState("");
  const [state, setState] = useState<"idle" | "sending" | "sent" | "error">("idle");
  const send = async () => {
    setState("sending");
    try { await requestMissingAction(summary, details); setSummary(""); setDetails(""); setState("sent"); }
    catch { setState("error"); }
  };
  return <section className="capability-group capability-request">
    <h2>{fa ? "کاری که لازم دارید اینجا نیست؟" : "Missing an action?"}</h2>
    <p className="capability-note">{fa ? "درخواست به فهرست «درخواست قابلیت» می‌رود تا تیم توسعه آن را به‌صورت امن بسازد."
      : "Ask the developers. It goes to Feature requests, and the action is built and reviewed like every other one."}</p>
    <input value={summary} onChange={event => setSummary(event.target.value)} dir="auto"
      placeholder={fa ? "مثلاً: نصب درایور چاپگر از مخزن" : "e.g. Reinstall a printer driver from the repository"} aria-label="Action needed" />
    <textarea value={details} onChange={event => setDetails(event.target.value)} rows={3} dir="auto"
      placeholder={fa ? "چه مشکلی را حل می‌کند و چند بار پیش می‌آید؟" : "Which problem it solves and how often it happens"} aria-label="Details" />
    <div><button className="primary-button" disabled={!summary.trim() || state === "sending"} onClick={() => void send()}>
      <Send size={14} />{fa ? "ارسال" : "Send request"}</button>
      {state === "sent" && <span className="success-text">{fa ? " ثبت شد." : " Saved to Feature requests."}</span>}
      {state === "error" && <span className="danger-text">{fa ? " ثبت نشد." : " Could not be saved."}</span>}</div>
  </section>;
}

function HowTo({ language, publishes }: { language: Language; publishes: boolean }) {
  if (language === "fa") return <section className="capability-group capability-howto" dir="rtl">
    <h2>چطور یک مهارت خوب بنویسیم</h2>
    <ol>
      <li>یک مشکل مشخص را انتخاب کنید که بارها پیش می‌آید و در این صفحه کارهای لازم برایش وجود دارد.</li>
      <li>در بخش «چه زمانی» دو یا سه جمله واقعی کاربران را بنویسید؛ عامل مهارت را با همین توضیح انتخاب می‌کند.</li>
      <li>مراحل را به ترتیب بنویسید: اول <code>check_known_incidents</code>، بعد کارهای فقط‌خواندنی، بعد یک اصلاح ایمن در هر نوبت.</li>
      <li>شناسه دقیق کار را داخل `` بنویسید، مثل <code>`dns.flush`</code>. برای سرور، چاپگر و پوشه فقط شناسه نقشه محیط را بنویسید، نه آدرس.</li>
      <li>بعد از هر اصلاح بپرسید «مشکل حل شد؟» و بنویسید اگر حل نشد چه زمانی ارجاع (escalate) شود.</li>
      <li>مهارت نمی‌تواند اجازه بدهد: کارهای مزاحم همیشه موافقت کاربر را می‌خواهند و دستور PowerShell اجرا نمی‌شود.</li>
      <li>«بررسی» را بزنید، خطاها را رفع کنید و برای بازبینی بفرستید. فقط بازبین (مدیر یا یک کارشناس ارشد) منتشر می‌کند.</li>
    </ol>
    <p>{publishes ? "انتشار، فایل SKILL.md را مستقیم در پوشه مهارت‌های Hermes می‌نویسد." : "مسیر انتشار Hermes تنظیم نشده؛ پس از انتشار فایل را دانلود و در پوشه مهارت‌ها کپی کنید."}</p>
    <Link className="primary-button" href="/skills/">رفتن به ویرایشگر مهارت</Link>
  </section>;
  return <section className="capability-group capability-howto">
    <h2>How to write a good skill</h2>
    <ol>
      <li>Pick one problem that comes up often and that the actions on this page can handle.</li>
      <li>In &ldquo;When to use&rdquo;, write two or three real things users say (in Persian if they write Persian). Hermes picks a skill by its description.</li>
      <li>Write the steps in order: <code>check_known_incidents</code> first, then Look actions, then one safe fix per reply.</li>
      <li>Name actions by their exact id in backticks, like <code>`dns.flush`</code>. Name servers, printers and shares only by their environment-map id, never by address.</li>
      <li>After every fix, ask whether the problem is solved, and say when to escalate if it is not.</li>
      <li>A skill grants nothing: disruptive actions still need the user&apos;s go-ahead, and the agent cannot run commands.</li>
      <li>Press Check, fix the errors, submit for review. Only a reviewer (the administrator or a senior member) publishes.</li>
    </ol>
    <p>{publishes ? "Publishing writes SKILL.md straight into the Hermes skills folder." : "No Hermes publish path is set: after publishing, download SKILL.md and copy it into the Hermes skills folder."}</p>
    <Link className="primary-button" href="/skills/">Open the skill editor</Link>
  </section>;
}
