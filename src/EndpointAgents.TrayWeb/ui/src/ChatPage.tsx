import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  ArrowUp, Check, CheckCheck, Clock3, Info, LoaderCircle, PanelRight, PencilLine, RotateCw, Search, ShieldCheck, X,
} from "lucide-react";
import { host, HostError } from "./bridge";
import {
  actionStateLabel, conversationStateLabel, dayGroup, fullDate, isRtl, isTerminalStage, listStamp, num,
  parseYesNo, progressLabel, relative, time,
} from "./format";
import { RunLine } from "./RunLine";
import type {
  ActionDecisionReceipt, ActionProposal, ChatDelta, ChatMessage, ConversationSummary, MessageReceipt,
  ProgressEvent, TrayStatus,
} from "./types";

type Filter = "all" | "active" | "resolved" | "closed";
type Item =
  | { kind: "message"; key: string; order: number; message: ChatMessage; local?: boolean }
  | { kind: "action"; key: string; order: number; action: ActionProposal }
  | { kind: "run"; key: string; order: number; events: ProgressEvent[] };

const GREETING = "سلام، من دستیار پشتیبانی هستم. مشکل را توضیح دهید تا بررسی را شروع کنم.";
const DISCONNECTED = "برای ارسال درخواست، دستیار پشتیبانی باید به سرور متصل باشد.";
const SUGGESTIONS = ["آفیس فعال نیست", "اینترنت وصل نمی‌شود", "ویندوز فعال نیست", "چاپگر چاپ نمی‌کند"];
const filters: { id: Filter; label: string }[] = [
  { id: "all", label: "همه" }, { id: "active", label: "باز" },
  { id: "resolved", label: "حل‌شده" }, { id: "closed", label: "بسته" },
];

interface Props {
  active: boolean;
  hidden: boolean;
  connected: boolean;
  status: TrayStatus | null;
  messageLimit: number;
}

export function ChatPage({ active, hidden, connected, status, messageLimit }: Props) {
  const [summaries, setSummaries] = useState<ConversationSummary[]>([]);
  const [listState, setListState] = useState<"loading" | "ready" | "error">("loading");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [items, setItems] = useState<Item[]>([]);
  const [visibleLimit, setVisibleLimit] = useState(30);
  const [text, setText] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [notice, setNotice] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [search, setSearch] = useState("");
  const [unseen, setUnseen] = useState<Set<string>>(new Set());
  const [listOpen, setListOpen] = useState(false);
  const [detailsOpen, setDetailsOpen] = useState(true);
  const [confirmNew, setConfirmNew] = useState(false);
  const [deciding, setDeciding] = useState<string | null>(null);

  const selectedRef = useRef<string | null>(null);
  const lastSequence = useRef(0);
  const version = useRef(0);
  const syncing = useRef(false);
  const drafts = useRef(new Map<string, string>());
  const lastSeenSequence = useRef(new Map<string, number>());
  const listInitialized = useRef(false);
  const scroller = useRef<HTMLDivElement>(null);
  const stickToEnd = useRef(true);
  const composer = useRef<HTMLTextAreaElement>(null);
  const live = useRef({ active, connected });
  live.current = { active, connected };

  const selected = summaries.find((s) => s.conversationId === selectedId) ?? null;
  const activeConversation = summaries.find((s) => s.isActive) ?? null;
  const readOnly = selectedId !== null && selected !== null && !selected.isActive;
  const pendingAction = useMemo(() => {
    for (let i = items.length - 1; i >= 0; i--) {
      const item = items[i];
      if (item.kind === "action" && item.action.state === "Pending") return item.action;
    }
    return null;
  }, [items]);
  const lastRun = useMemo(() => {
    for (let i = items.length - 1; i >= 0; i--) if (items[i].kind === "run") return items[i] as Extract<Item, { kind: "run" }>;
    return null;
  }, [items]);
  const runningLabel = lastRun && !isTerminalStage(lastRun.events[lastRun.events.length - 1])
    ? progressLabel(lastRun.events[lastRun.events.length - 1]) : null;

  // ---- timeline -------------------------------------------------------------------------------

  const resetTimeline = useCallback((id: string | null) => {
    version.current++;
    lastSequence.current = 0;
    stickToEnd.current = true;
    setItems([]);
    setNotice("");
    selectedRef.current = id;
    setSelectedId(id);
    setText(drafts.current.get(id ?? "__new") ?? "");
  }, []);

  const applyDelta = useCallback((delta: ChatDelta) => {
    setVisibleLimit(delta.visibleItemLimit || 30);
    setItems((previous) => {
      const next = [...previous];
      const index = new Map(next.map((item, i) => [item.key, i]));
      const upsert = (item: Item) => {
        const at = index.get(item.key);
        if (at === undefined) { index.set(item.key, next.length); next.push(item); }
        else next[at] = { ...item, order: next[at].order };
      };
      for (const message of delta.messages) {
        const at = index.get(`m:${message.messageId}`);
        upsert({ kind: "message", key: `m:${message.messageId}`, order: at === undefined ? message.sequence : next[at].order, message });
      }
      for (const action of delta.actions) upsert({ kind: "action", key: `a:${action.actionId}`, order: action.sequence, action });
      for (const event of delta.progressEvents ?? []) {
        const key = `r:${event.runId}`;
        const at = index.get(key);
        if (at === undefined) upsert({ kind: "run", key, order: event.sequence, events: [event] });
        else {
          const run = next[at] as Extract<Item, { kind: "run" }>;
          if (!run.events.some((e) => e.eventId === event.eventId))
            next[at] = { ...run, events: [...run.events, event].sort((a, b) => a.sequence - b.sequence) };
        }
      }
      return next.sort((a, b) => a.order - b.order);
    });
    const sequences = [
      delta.conversation.lastSequence,
      ...delta.messages.map((m) => m.sequence),
      ...delta.actions.map((a) => a.sequence),
      ...(delta.progressEvents ?? []).map((p) => p.sequence),
    ];
    lastSequence.current = Math.max(lastSequence.current, ...sequences);
    const latest = delta.messages[delta.messages.length - 1];
    if (latest?.state === "Failed")
      setNotice(latest.role === "Assistant" ? "دستیار نتوانست این درخواست را کامل کند." : "بررسی این پیام ناموفق بود.");
    else if (latest?.state === "Queued") setNotice("پیام در صف دستیار پشتیبانی است.");
    else if (latest?.state === "Working") setNotice("دستیار پشتیبانی در حال بررسی پیام است…");
    else if (latest?.state === "Completed" && latest.role !== "User") setNotice("");
  }, []);

  const syncChat = useCallback(async () => {
    const id = selectedRef.current;
    if (!id || syncing.current || !live.current.active || !live.current.connected) return;
    syncing.current = true;
    const v = version.current;
    try {
      const delta = await host.call<ChatDelta>("chat.sync", { conversationId: id, afterSequence: lastSequence.current });
      if (v === version.current && id === selectedRef.current) applyDelta(delta);
    } catch (error) {
      if (v === version.current) setNotice("گفتگو به‌روز نشد. ارتباط با سرویس دوباره بررسی می‌شود.");
      void error;
    } finally {
      syncing.current = false;
    }
  }, [applyDelta]);

  const refreshList = useCallback(async (selectId?: string) => {
    if (!live.current.active || !live.current.connected) return;
    try {
      const list = await host.call<ConversationSummary[]>("chat.list");
      setSummaries(list);
      setListState("ready");
      setUnseen((current) => {
        const next = new Set(current);
        for (const s of list) {
          const seen = lastSeenSequence.current.get(s.conversationId);
          if (seen === undefined) lastSeenSequence.current.set(s.conversationId, s.lastSequence);
          else if (s.lastSequence > seen && s.conversationId !== selectedRef.current) next.add(s.conversationId);
        }
        return next;
      });
      const target = selectId ?? selectedRef.current;
      if (!listInitialized.current) {
        listInitialized.current = true;
        const initial = list.find((s) => s.isActive);
        if (initial) resetTimeline(initial.conversationId);
      } else if (target && !list.some((s) => s.conversationId === target)) {
        resetTimeline(null);
      } else if (selectId && selectId !== selectedRef.current) {
        resetTimeline(selectId);
      }
    } catch {
      if (!listInitialized.current) listInitialized.current = true;
      setListState("error");
    }
  }, [resetTimeline]);

  // Polling only while the conversations page is visible and the server is reachable.
  useEffect(() => {
    if (!active || !connected) return;
    void refreshList();
    void syncChat();
    const chat = setInterval(syncChat, 1000);
    const list = setInterval(refreshList, 3000);
    return () => { clearInterval(chat); clearInterval(list); };
  }, [active, connected, refreshList, syncChat]);

  useEffect(() => { if (selectedId) void syncChat(); }, [selectedId, syncChat]);

  useEffect(() => {
    if (!selectedId) return;
    const s = summaries.find((x) => x.conversationId === selectedId);
    if (s) lastSeenSequence.current.set(selectedId, s.lastSequence);
    setUnseen((current) => {
      if (!current.has(selectedId)) return current;
      const next = new Set(current);
      next.delete(selectedId);
      return next;
    });
  }, [selectedId, summaries]);

  // Keep the newest message in view unless the user scrolled up to read.
  useEffect(() => {
    const element = scroller.current;
    if (element && stickToEnd.current) element.scrollTop = element.scrollHeight;
  }, [items, selectedId]);

  useEffect(() => {
    if (active && !readOnly) composer.current?.focus();
  }, [active, selectedId, readOnly]);

  // ---- actions --------------------------------------------------------------------------------

  const select = (id: string | null) => {
    drafts.current.set(selectedRef.current ?? "__new", text);
    if (id !== selectedRef.current) resetTimeline(id);
    setListOpen(false);
  };

  const startNew = async () => {
    setConfirmNew(false);
    if (activeConversation) {
      try {
        await host.call("chat.close", { conversationId: activeConversation.conversationId });
      } catch (error) {
        setNotice(error instanceof HostError ? "گفتگوی قبلی بسته نشد. دوباره تلاش کنید." : String(error));
        return;
      }
    }
    drafts.current.delete("__new");
    resetTimeline(null);
    void refreshList();
    composer.current?.focus();
  };

  const decide = async (action: ActionProposal, approved: boolean, typed?: string) => {
    setDeciding(action.actionId);
    setNotice(approved ? "در حال آغاز اقدام تأییدشده…" : "در حال ثبت تصمیم شما…");
    try {
      const receipt = await host.call<ActionDecisionReceipt>("chat.action.respond", {
        conversationId: action.conversationId, actionId: action.actionId, approved, text: typed,
      });
      if (receipt.proposal) applyDelta({
        conversation: { conversationId: action.conversationId, lastSequence: lastSequence.current },
        messages: receipt.decisionMessage ? [receipt.decisionMessage] : [],
        actions: [{ ...receipt.proposal, sequence: action.sequence }], progressEvents: [], visibleItemLimit: visibleLimit,
      });
      setNotice(receipt.message);
      if (typed) setText("");
      await syncChat();
    } catch {
      setNotice("تصمیم شما ثبت نشد. ارتباط با سرویس را بررسی کنید و دوباره تلاش کنید.");
    } finally {
      setDeciding(null);
    }
  };

  const submit = async () => {
    const message = text.trim();
    if (submitting || !connected || readOnly || !message || message.length > messageLimit) return;
    if (pendingAction) {
      const answer = parseYesNo(message);
      if (answer === null) {
        setNotice("برای پاسخ به اقدام پیشنهادی، از دکمه‌های بالا استفاده کنید یا «بله» یا «نه» بنویسید.");
        return;
      }
      await decide(pendingAction, answer, message);
      return;
    }
    setSubmitting(true);
    setNotice("پیام در حال ارسال به دستیار پشتیبانی است…");
    try {
      const receipt = await host.call<MessageReceipt>("chat.send", { conversationId: selectedRef.current, text: message });
      if (!receipt.accepted) throw new Error(receipt.message);
      const now = new Date().toISOString();
      if (selectedRef.current !== receipt.conversationId) {
        drafts.current.delete("__new");
        resetTimeline(receipt.conversationId);
      }
      setItems((list) => list.some((i) => i.key === `m:${receipt.messageId}`) ? list : [...list, {
        kind: "message", key: `m:${receipt.messageId}`, order: receipt.sequence, local: true,
        message: {
          messageId: receipt.messageId, conversationId: receipt.conversationId, sequence: receipt.sequence,
          role: "User", body: message, textDirection: isRtl(message) ? "rtl" : "ltr", state: "Queued", createdAtUtc: now,
        },
      }]);
      stickToEnd.current = true;
      setText("");
      drafts.current.delete(receipt.conversationId);
      setNotice(receipt.message);
      await refreshList(receipt.conversationId);
      await syncChat();
    } catch (error) {
      setNotice(error instanceof Error && error.message ? error.message : "پیام ارسال نشد. دوباره تلاش کنید.");
    } finally {
      setSubmitting(false);
      composer.current?.focus();
    }
  };

  // ---- derived view -----------------------------------------------------------------------------

  const visibleSummaries = summaries.filter((s) =>
    (filter === "all" || (filter === "active" && s.isActive) || (filter === "resolved" && s.state === "Resolved")
      || (filter === "closed" && s.state === "Closed"))
    && (!search.trim() || `${s.title} ${s.preview}`.includes(search.trim())));
  const groups = new Map<string, ConversationSummary[]>();
  for (const s of visibleSummaries) {
    const label = dayGroup(s.updatedAtUtc);
    groups.set(label, [...(groups.get(label) ?? []), s]);
  }
  const shownItems = items.slice(-Math.max(visibleLimit, 1));
  const headerState = !connected ? "در انتظار اتصال"
    : runningLabel ?? (selected ? conversationStateLabel[selected.state] : "گفتگوی تازه");
  const shownNotice = !connected ? DISCONNECTED
    : readOnly ? "این گفتگو بسته شده است؛ برای درخواست تازه، گفتگوی جدید بسازید." : notice;

  if (hidden) return null;

  return (
    <div className="chat" data-list-open={listOpen || undefined} data-details={detailsOpen || undefined}>
      {/* history ------------------------------------------------------------------------------ */}
      <aside className="history" aria-label="فهرست گفتگوها">
        <div className="history-head">
          <h1>گفتگوها</h1>
          {confirmNew ? (
            <div className="confirm-new" role="group" aria-label="شروع گفتگوی تازه">
              <span>گفتگوی باز فعلی بسته شود؟</span>
              <button className="chip chip-strong" onClick={startNew}>بله، تازه شروع کن</button>
              <button className="chip" onClick={() => setConfirmNew(false)}>نه</button>
            </div>
          ) : (
            <button className="icon-button" aria-label="گفتگوی تازه" title="گفتگوی تازه" disabled={!connected}
              onClick={() => (activeConversation && activeConversation.state !== "Resolved" ? setConfirmNew(true) : startNew())}>
              <PencilLine size={18} aria-hidden />
            </button>
          )}
        </div>
        <label className="search">
          <Search size={15} aria-hidden />
          <input type="search" placeholder="جستجو در گفتگوها" value={search} onChange={(e) => setSearch(e.target.value)} />
        </label>
        <div className="segmented" role="tablist" aria-label="فیلتر گفتگوها">
          {filters.map((f) => (
            <button key={f.id} role="tab" aria-selected={filter === f.id} onClick={() => setFilter(f.id)}>{f.label}</button>
          ))}
        </div>
        <div className="history-list">
          {listState === "loading" && connected && [0, 1, 2].map((i) => <div key={i} className="skeleton-row" />)}
          {listState === "error" && (
            <div className="list-message">
              <p>فهرست گفتگوها خوانده نشد. ارتباط محلی با سرویس کامل نشد.</p>
              <button className="chip" onClick={() => refreshList()}><RotateCw size={14} aria-hidden />تلاش دوباره</button>
            </div>
          )}
          {listState === "ready" && visibleSummaries.length === 0 && (
            <p className="list-message">{summaries.length === 0 ? "هنوز گفتگویی ندارید. مشکل را در کادر پیام بنویسید." : "گفتگویی با این فیلتر پیدا نشد."}</p>
          )}
          {selectedId === null && (
            <button className="conversation draft" aria-current="true" onClick={() => select(null)}>
              <span className="avatar" data-state="Open" aria-hidden><PencilLine size={16} /></span>
              <span className="conversation-text">
                <span className="conversation-title">گفتگوی تازه</span>
                <span className="conversation-preview">{text ? text : "مشکل خود را بنویسید"}</span>
              </span>
            </button>
          )}
          {[...groups].map(([label, list]) => (
            <section key={label} className="day-group" aria-label={label}>
              <h2>{label}</h2>
              {list.map((s) => (
                <button key={s.conversationId} className="conversation"
                  aria-current={s.conversationId === selectedId ? "true" : undefined}
                  onClick={() => select(s.conversationId)}>
                  <span className="avatar" data-state={s.state} aria-hidden>{s.title.trim().slice(0, 1)}</span>
                  <span className="conversation-text">
                    <span className="conversation-title">{s.title}</span>
                    <span className="conversation-preview" dir="auto">{s.preview}</span>
                  </span>
                  <span className="conversation-meta">
                    <time dateTime={s.updatedAtUtc}>{listStamp(s.updatedAtUtc)}</time>
                    {unseen.has(s.conversationId) ? <span className="unseen">تازه</span>
                      : s.state === "AwaitingApproval" ? <span className="state-pill" data-state={s.state}>تأیید</span>
                      : <span className="state-text">{conversationStateLabel[s.state]}</span>}
                  </span>
                </button>
              ))}
            </section>
          ))}
        </div>
      </aside>
      <button className="scrim" aria-label="بستن فهرست گفتگوها" onClick={() => setListOpen(false)} />

      {/* conversation ---------------------------------------------------------------------------- */}
      <section className="conversation-pane" aria-label="گفتگو">
        <header className="chat-head">
          <button className="icon-button list-toggle" aria-label="فهرست گفتگوها" onClick={() => setListOpen(true)}>
            <MessagesIcon />
          </button>
          <span className="avatar large" data-state={selected?.state ?? "Open"} aria-hidden>
            {selected ? selected.title.trim().slice(0, 1) : <PencilLine size={18} />}
          </span>
          <div className="chat-head-text">
            <h2>{selected?.title ?? "گفتگوی تازه"}</h2>
            <p data-live={runningLabel ? "true" : undefined} aria-live="polite">
              {runningLabel && <span className="live-dots" aria-hidden><i /><i /><i /></span>}
              {headerState}
            </p>
          </div>
          <button className="icon-button details-toggle" aria-label="جزئیات گفتگو" aria-pressed={detailsOpen}
            onClick={() => setDetailsOpen(!detailsOpen)}>
            <PanelRight size={18} aria-hidden />
          </button>
        </header>

        <div className="timeline" ref={scroller}
          onScroll={(e) => {
            const el = e.currentTarget;
            stickToEnd.current = el.scrollHeight - el.scrollTop - el.clientHeight < 64;
          }}>
          <div className="timeline-inner" role="log" aria-live="polite" aria-label="پیام‌های گفتگو">
            {selectedId === null && (
              <>
                <Bubble role="Assistant" body={GREETING} />
                <div className="suggestions" aria-label="پیشنهادها">
                  {SUGGESTIONS.map((s) => (
                    <button key={s} className="chip" disabled={!connected} onClick={() => { setText(s); composer.current?.focus(); }}>{s}</button>
                  ))}
                </div>
              </>
            )}
            {selectedId !== null && items.length === 0 && connected && (
              <div className="timeline-loading" aria-label="در حال بارگذاری"><LoaderCircle className="spin" size={20} /></div>
            )}
            {shownItems.map((item, index) => {
              const previous = shownItems[index - 1];
              const showDay = item.kind === "message" && (!previous || previous.kind !== "message"
                || dayGroup(previous.message.createdAtUtc) !== dayGroup(item.message.createdAtUtc));
              return (
                <div key={item.key} className="timeline-item">
                  {showDay && item.kind === "message" && <div className="day-chip">{dayGroup(item.message.createdAtUtc)}</div>}
                  {item.kind === "message" && <Bubble role={item.message.role} body={item.message.body}
                    at={item.message.createdAtUtc} state={item.message.state} />}
                  {item.kind === "run" && <RunLine events={item.events} />}
                  {item.kind === "action" && (
                    <ActionCard action={item.action} busy={deciding === item.action.actionId}
                      canDecide={!readOnly && connected && deciding === null} onDecide={decide} />
                  )}
                </div>
              );
            })}
          </div>
        </div>

        <footer className="composer-area">
          {shownNotice && <p className="notice" role="status">{shownNotice}</p>}
          {readOnly ? (
            <div className="closed-bar">
              <span>این گفتگو بسته شده و فقط برای مشاهده است.</span>
              <button className="button primary" disabled={!connected} onClick={() => startNew()}>شروع گفتگوی تازه</button>
            </div>
          ) : (
            <div className="composer" data-disabled={!connected || undefined}>
              <textarea ref={composer} rows={1} dir="auto" maxLength={messageLimit}
                placeholder={pendingAction ? "بله یا نه بنویسید، یا از دکمه‌های بالا استفاده کنید" : "مشکل خود را بنویسید…"}
                aria-label="متن پیام" value={text} disabled={!connected || submitting}
                onFocus={() => host.call("keyboard.persian")}
                onChange={(e) => setText(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); void submit(); }
                }} />
              {text.length > messageLimit * 0.75 && (
                <span className="counter" data-full={text.length >= messageLimit || undefined}>{num(text.length)} از {num(messageLimit)}</span>
              )}
              <button className="send" aria-label="ارسال پیام" disabled={!connected || submitting || !text.trim()} onClick={() => submit()}>
                {submitting ? <LoaderCircle className="spin" size={18} aria-hidden /> : <ArrowUp size={19} strokeWidth={2.2} aria-hidden />}
              </button>
            </div>
          )}
        </footer>
      </section>

      {/* details ----------------------------------------------------------------------------------- */}
      <aside className="details" aria-label="جزئیات">
        <h2>جزئیات گفتگو</h2>
        {selected ? (
          <dl className="facts">
            <div><dt>وضعیت</dt><dd><span className="state-pill" data-state={selected.state}>{conversationStateLabel[selected.state]}</span></dd></div>
            <div><dt>شروع</dt><dd>{fullDate(selected.createdAtUtc)}</dd></div>
            <div><dt>آخرین تغییر</dt><dd>{relative(selected.updatedAtUtc)}</dd></div>
          </dl>
        ) : <p className="muted">با فرستادن اولین پیام، گفتگوی تازه ساخته می‌شود.</p>}
        <h2>این رایانه</h2>
        <dl className="facts">
          <div><dt>اتصال</dt><dd>{status?.label ?? "در حال بررسی"}</dd></div>
          {status?.runtime && <div><dt>نام رایانه</dt><dd><bdi className="ltr">{status.runtime.machineName}</bdi></dd></div>}
        </dl>
        <div className="help-note">
          <ShieldCheck size={18} aria-hidden />
          <p>دستیار فقط کارهای تأییدشده را انجام می‌دهد و پیش از هر تغییر از شما اجازه می‌گیرد. رمز عبور خود را هرگز در گفتگو ننویسید.</p>
        </div>
      </aside>
    </div>
  );
}

function MessagesIcon() {
  return <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
    <path d="M4 6h16M4 12h16M4 18h10" strokeLinecap="round" /></svg>;
}

function Bubble({ role, body, at, state }: { role: ChatMessage["role"]; body: string; at?: string; state?: ChatMessage["state"] }) {
  const mine = role === "User";
  const author = role === "Operator" ? "کارشناس پشتیبانی" : role === "System" ? "سیستم" : null;
  return (
    <div className="bubble" data-mine={mine || undefined} data-role={role}>
      {author && <span className="bubble-author">{author}</span>}
      <p dir="auto">{body}</p>
      {at && (
        <span className="bubble-meta">
          <time dateTime={at}>{time(at)}</time>
          {mine && (state === "Queued" ? <Clock3 size={13} aria-label="در صف" />
            : state === "Failed" ? <X size={13} aria-label="ناموفق" />
            : state === "Working" ? <Check size={13} aria-label="دریافت شد" />
            : <CheckCheck size={14} aria-label="بررسی شد" />)}
        </span>
      )}
    </div>
  );
}

function ActionCard({ action, busy, canDecide, onDecide }: {
  action: ActionProposal; busy: boolean; canDecide: boolean;
  onDecide: (action: ActionProposal, approved: boolean) => void;
}) {
  const pending = action.state === "Pending";
  const file = action.parameters?.["_approvedArtifactFileName"];
  const hash = action.parameters?.["_approvedArtifactSha256"];
  return (
    <section className="action-card" data-state={action.state} aria-label="اقدام پیشنهادی">
      <div className="action-head">
        <span className="action-icon" aria-hidden><ShieldCheck size={18} /></span>
        <div>
          <h3>{action.displayName}</h3>
          <p className="muted">{pending ? "پیشنهاد دستیار؛ بدون تأیید شما اجرا نمی‌شود" : actionStateLabel[action.state]}</p>
        </div>
      </div>
      <p>{action.description}</p>
      {typeof file === "string" && typeof hash === "string" && (
        <details className="artifact">
          <summary><Info size={14} aria-hidden />فایل تأییدشده</summary>
          <bdi className="ltr mono">{file}</bdi>
          <bdi className="ltr mono hash">{hash}</bdi>
        </details>
      )}
      {pending && (
        <div className="action-buttons">
          <button className="button primary" disabled={!canDecide} onClick={() => onDecide(action, true)}>
            {busy ? <LoaderCircle className="spin" size={16} aria-hidden /> : null}انجامش بده
          </button>
          <button className="button" disabled={!canDecide} onClick={() => onDecide(action, false)}>نه، لازم نیست</button>
          <span className="muted expires">تا {time(action.expiresAtUtc)} معتبر است</span>
        </div>
      )}
    </section>
  );
}
