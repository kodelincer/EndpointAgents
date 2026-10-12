import { useCallback, useEffect, useRef, useState } from "react";
import { Copy, Download, RotateCw, Search } from "lucide-react";
import { host } from "./bridge";
import { levelLabel, num, stamp } from "./format";
import type { LogEntry } from "./types";

const ranges = [{ hours: 1, label: "یک ساعت" }, { hours: 6, label: "۶ ساعت" },
  { hours: 24, label: "۲۴ ساعت" }, { hours: 168, label: "۷ روز" }];
const levels = [{ id: "", label: "همه سطح‌ها" }, { id: "Error", label: "خطا" },
  { id: "Warning", label: "هشدار" }, { id: "Information", label: "اطلاعات" }];

const id = (value: number) => value.toLocaleString("fa-IR", { useGrouping: false });
const keyOf = (e: LogEntry) => `${e.timestampUtc}|${e.source}|${e.eventId}|${e.message.slice(0, 40)}`;
const formatEntry = (e: LogEntry) =>
  `[${stamp(e.timestampUtc)}] ${levelLabel(e.level)} · ${e.source} · ${e.eventId || "—"}\r\n${e.message}`;

export function LogsPage({ active }: { active: boolean }) {
  const [hours, setHours] = useState(6);
  const [level, setLevel] = useState("");
  const [search, setSearch] = useState("");
  const [entries, setEntries] = useState<LogEntry[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [state, setState] = useState("");
  const loading = useRef(false);
  const query = useRef({ hours, level, search });
  query.current = { hours, level, search };

  const load = useCallback(async () => {
    if (loading.current) return;
    loading.current = true;
    try {
      const list = await host.call<LogEntry[]>("logs.read", query.current);
      setEntries(list);
      setState(list.length === 0 ? "رویدادی مطابق این فیلترها پیدا نشد." : `${num(list.length)} رویداد، جدیدترین در ابتدا`);
    } catch (error) {
      setState("گزارش‌ها در دسترس نیستند: " + (error instanceof Error ? error.message : ""));
    } finally {
      loading.current = false;
    }
  }, []);

  useEffect(() => {
    const timer = setTimeout(load, search ? 450 : 0);
    return () => clearTimeout(timer);
  }, [hours, level, search, load]);

  useEffect(() => {
    if (!active) return;
    const timer = setInterval(load, 5000);
    return () => clearInterval(timer);
  }, [active, load]);

  const current = entries?.find((e) => keyOf(e) === selected) ?? null;

  const copy = async () => {
    const text = current ? formatEntry(current) : (entries ?? []).map(formatEntry).join("\r\n\r\n");
    if (!text) return;
    await host.call("clipboard.write", { text });
    setState(current ? "رویداد انتخاب‌شده کپی شد." : "همه رویدادهای نمایش‌داده‌شده کپی شدند.");
  };

  const exportLogs = async (format: "txt" | "json") => {
    const path = await host.call<string | null>("logs.export", { format });
    if (path) setState("گزارش‌ها ذخیره شدند: " + path);
  };

  return (
    <div className="page logs-page">
      <header className="page-head">
        <div>
          <h1>گزارش‌ها</h1>
          <p className="muted">رویدادهای دستیار پشتیبانی روی این رایانه</p>
        </div>
        <div className="head-actions">
          <button className="button" onClick={copy}><Copy size={15} aria-hidden />{current ? "کپی رویداد" : "کپی همه"}</button>
          <button className="button" onClick={() => exportLogs("txt")}><Download size={15} aria-hidden />ذخیره متن</button>
          <button className="button" onClick={() => exportLogs("json")}><Download size={15} aria-hidden />ذخیره JSON</button>
        </div>
      </header>

      <div className="filters">
        <label className="search">
          <Search size={15} aria-hidden />
          <input type="search" placeholder="جستجو در گزارش‌ها" aria-label="جستجو" value={search} onChange={(e) => setSearch(e.target.value)} />
        </label>
        <label className="select">
          <span>سطح</span>
          <select value={level} onChange={(e) => setLevel(e.target.value)}>
            {levels.map((l) => <option key={l.id} value={l.id}>{l.label}</option>)}
          </select>
        </label>
        <div className="segmented" role="radiogroup" aria-label="بازه زمانی">
          {ranges.map((r) => (
            <button key={r.hours} role="radio" aria-checked={hours === r.hours} onClick={() => setHours(r.hours)}>{r.label}</button>
          ))}
        </div>
        <button className="icon-button" aria-label="خواندن دوباره" onClick={load}><RotateCw size={16} /></button>
      </div>
      <p className="muted log-state" role="status">{state}</p>

      <div className="log-split">
        <div className="log-table" role="listbox" aria-label="رویدادها">
          <div className="log-row log-header" aria-hidden>
            <span>زمان</span><span>سطح</span><span>منبع</span><span>شناسه</span><span>پیام</span>
          </div>
          {entries === null && [0, 1, 2, 3].map((i) => <div key={i} className="skeleton-row" />)}
          {entries?.slice(0, 500).map((entry) => {
            const key = keyOf(entry);
            return (
              <div key={key} className="log-row" role="option" tabIndex={0} aria-selected={key === selected}
                data-level={entry.level}
                onClick={() => setSelected(key === selected ? null : key)}
                onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); setSelected(key === selected ? null : key); } }}>
                <span className="ltr-cell">{stamp(entry.timestampUtc)}</span>
                <span><span className="level" data-level={entry.level}>{levelLabel(entry.level)}</span></span>
                <span className="ltr-cell">{entry.source.replace("EndpointAgents.", "")}</span>
                <span className="ltr-cell">{entry.eventId ? id(entry.eventId) : "—"}</span>
                <span className="message-cell" dir="auto">{entry.message}</span>
              </div>
            );
          })}
        </div>
        <aside className="log-details" aria-label="جزئیات رویداد">
          {current ? (
            <>
              <h2><span className="level" data-level={current.level}>{levelLabel(current.level)}</span> {stamp(current.timestampUtc)}</h2>
              <dl className="facts">
                <div><dt>منبع</dt><dd><bdi className="ltr">{current.source}</bdi></dd></div>
                <div><dt>شناسه رویداد</dt><dd>{current.eventId ? id(current.eventId) : "—"}</dd></div>
              </dl>
              <p className="log-message" dir="auto">{current.message}</p>
            </>
          ) : <p className="muted">برای دیدن جزئیات، یک رویداد را انتخاب کنید.</p>}
        </aside>
      </div>
    </div>
  );
}
