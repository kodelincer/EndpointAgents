import type {
  ActionState, ConnectionStatus, ConversationState, Health, ProgressEvent, ServiceState,
} from "./types";

const faNumber = new Intl.NumberFormat("fa-IR");
const faTime = new Intl.DateTimeFormat("fa-IR", { hour: "2-digit", minute: "2-digit" });
const faWeekday = new Intl.DateTimeFormat("fa-IR", { weekday: "long" });
const faDate = new Intl.DateTimeFormat("fa-IR", { year: "numeric", month: "long", day: "numeric" });
const faFull = new Intl.DateTimeFormat("fa-IR", {
  weekday: "long", day: "numeric", month: "long", hour: "2-digit", minute: "2-digit",
});
const faStamp = new Intl.DateTimeFormat("fa-IR", {
  year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit",
});

export const num = (value: number) => faNumber.format(value);
export const time = (iso?: string | null) => (iso ? faTime.format(new Date(iso)) : "");
export const fullDate = (iso?: string | null) => (iso ? faFull.format(new Date(iso)) : "ثبت نشده");
export const stamp = (iso?: string | null) => (iso ? faStamp.format(new Date(iso)) : "ثبت نشده");

function dayDifference(iso: string) {
  const day = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  return Math.round((day(new Date()) - day(new Date(iso))) / 86_400_000);
}

/** Short stamp for list rows: time today, weekday this week, date beyond. */
export function listStamp(iso: string) {
  const days = dayDifference(iso);
  if (days <= 0) return time(iso);
  if (days < 7) return faWeekday.format(new Date(iso));
  return faDate.format(new Date(iso));
}

export function dayGroup(iso: string) {
  const days = dayDifference(iso);
  if (days <= 0) return "امروز";
  if (days === 1) return "دیروز";
  if (days < 7) return `${num(days)} روز پیش`;
  return faDate.format(new Date(iso));
}

export function relative(iso?: string | null) {
  if (!iso) return "ثبت نشده";
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return "همین حالا";
  if (seconds < 3600) return `${num(Math.floor(seconds / 60))} دقیقه پیش`;
  if (seconds < 86_400) return `${num(Math.floor(seconds / 3600))} ساعت پیش`;
  return fullDate(iso);
}

export const healthTone = (health: Health) =>
  health === "Ready" ? "ok" : health === "Error" ? "bad" : health === "Checking" ? "idle" : "warn";

export const serviceLabel: Record<ServiceState, string> = {
  Unknown: "نامشخص", NotInstalled: "نصب نشده", Stopped: "متوقف", StartPending: "در حال شروع",
  StopPending: "در حال توقف", Running: "در حال اجرا", Interactive: "اجرای تعاملی",
};

export const connectionLabel: Record<ConnectionStatus, string> = {
  Starting: "در حال شروع", Connecting: "در حال اتصال", Connected: "متصل", Reconnecting: "در حال اتصال مجدد",
  Disconnected: "قطع", Faulted: "خطا",
};

export const conversationStateLabel: Record<ConversationState, string> = {
  Open: "باز", Working: "در حال پاسخ‌گویی", AwaitingApproval: "منتظر تأیید شما", Answered: "پاسخ داده شد",
  Resolved: "حل شد", Closed: "بسته شد",
};

export const actionStateLabel: Record<ActionState, string> = {
  Pending: "منتظر تأیید", Approved: "تأیید شد", Rejected: "رد شد", Expired: "منقضی شد",
  Executed: "انجام شد", Failed: "ناموفق بود", Cancelled: "متوقف شد",
};

export const levelLabel = (level: string) =>
  level === "Error" ? "خطا" : level === "Warning" ? "هشدار" : level === "Information" ? "اطلاعات" : level;

const activityLabel: Record<string, string> = {
  OfficeInventory: "بررسی نصب و نسخه آفیس",
  OfficeActivationStatus: "بررسی فعال‌سازی آفیس",
  OfficeRuntimeStatus: "بررسی برنامه‌های باز آفیس",
  WindowsActivationStatus: "بررسی فعال‌سازی ویندوز",
  WindowsActivationRepair: "تعمیر فعال‌سازی ویندوز",
  ProposedAction: "آماده‌سازی اقدام پیشنهادی",
};

/** One station label per progress event, written as what is happening or what happened. */
export function progressLabel(event: ProgressEvent): string {
  const failed = event.outcome === "Failure";
  switch (event.stage) {
    case "Queued": return "درخواست در صف بررسی";
    case "AssistantStarted": return "دستیار بررسی را آغاز کرد";
    case "WaitingForModel": return "در انتظار پاسخ دستیار";
    case "EndpointCheckStarted": return activityLabel[event.activity] ?? "بررسی رایانه";
    case "EndpointCheckCompleted": return failed ? "بررسی رایانه ناموفق بود" : "بررسی رایانه کامل شد";
    case "Retrying": return "ارتباط برقرار نشد؛ تلاش دوباره";
    case "Completed": return "پاسخ آماده شد";
    case "Failed": return "بررسی کامل نشد";
    case "AwaitingApproval": return "منتظر تأیید شما";
    case "ActionStarted":
      return event.activity === "OfficeActivationRepair" ? "تعمیر فعال‌سازی آفیس در حال اجرا"
        : event.activity === "WindowsActivationRepair" ? "تعمیر فعال‌سازی ویندوز در حال اجرا"
        : "اقدام تأییدشده در حال اجرا";
    case "ActionCompleted":
      return event.activity === "OfficeActivationRepair" ? (failed ? "تعمیر آفیس ناموفق بود" : "تعمیر آفیس انجام شد")
        : event.activity === "WindowsActivationRepair" ? (failed ? "تعمیر ویندوز ناموفق بود" : "تعمیر ویندوز انجام شد")
        : failed ? "اجرای اقدام ناموفق بود" : "اقدام تأییدشده انجام شد";
    case "ActionRejected": return "اقدام اجرا نشد";
    case "Cancelled": return "درخواست متوقف شد";
  }
}

export const isTerminalStage = (event: ProgressEvent) =>
  event.stage === "Completed" || event.stage === "Failed" || event.stage === "ActionCompleted" ||
  event.stage === "ActionRejected" || event.stage === "Cancelled";

/** True when the first strong character is Persian/Arabic/Hebrew. */
export function isRtl(text: string) {
  for (const character of text) {
    if (/[֐-ࣿיִ-ﻼ]/.test(character)) return true;
    if (/\p{L}/u.test(character)) return false;
  }
  return true;
}

const positive = new Set(["yes", "yes please", "approve", "approved", "do it", "continue", "ok", "okay",
  "بله", "بله لطفا", "آره", "اره", "تایید", "تأیید", "انجام بده", "ادامه بده"]);
const negative = new Set(["no", "reject", "cancel", "stop", "نه", "خیر", "رد", "لغو", "انجام نده"]);

/** Mirrors the service's yes/no reading of a typed answer to a proposed action. */
export function parseYesNo(text: string): boolean | null {
  const normalized = text.trim().toLowerCase().replace(/ي|ى/g, "ی").replace(/ك/g, "ک").replace(/‌/g, " ")
    .split(/\s+/).filter(Boolean).join(" ").replace(/[.!?؟،,;؛:]+$/, "");
  if (positive.has(normalized)) return true;
  if (negative.has(normalized)) return false;
  return null;
}
