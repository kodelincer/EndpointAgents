import type { Host, Method } from "./bridge";
import type {
  ActionProposal, ChatDelta, ChatMessage, ConversationSummary, LogEntry, ProgressEvent, ReadyInfo, TrayStatus,
} from "./types";

// Sample data for design review only (`--ui-preview`, `npm run dev`). Nothing here reaches a service.

const minutes = (n: number) => new Date(Date.now() - n * 60_000).toISOString();

const conversations: ConversationSummary[] = [
  {
    conversationId: "c-office", title: "فعال نبودن آفیس", preview: "ورد پیام می‌دهد که محصول فعال نشده است.",
    textDirection: "rtl", createdAtUtc: minutes(14), updatedAtUtc: minutes(1), lastSequence: 9,
    state: "AwaitingApproval", isActive: true,
  },
  {
    conversationId: "c-print", title: "چاپگر طبقه دوم", preview: "چاپگر دوباره به صف اضافه شد و صفحه آزمایشی چاپ شد.",
    textDirection: "rtl", createdAtUtc: minutes(60 * 26), updatedAtUtc: minutes(60 * 25), lastSequence: 12,
    state: "Resolved", isActive: false,
  },
  {
    conversationId: "c-net", title: "قطع شدن اینترنت IBSng", preview: "برنامه اتصال باز شد؛ نام کاربری را خودتان وارد کنید.",
    textDirection: "rtl", createdAtUtc: minutes(60 * 24 * 3), updatedAtUtc: minutes(60 * 24 * 3 - 20), lastSequence: 7,
    state: "Closed", isActive: false,
  },
];

const messages: ChatMessage[] = [
  { messageId: "m1", conversationId: "c-office", sequence: 1, role: "User", body: "سلام، ورد موقع باز شدن می‌گوید محصول فعال نشده و نمی‌توانم فایل را ویرایش کنم.", textDirection: "rtl", state: "Completed", createdAtUtc: minutes(14) },
  { messageId: "m2", conversationId: "c-office", sequence: 6, role: "Assistant", body: "آفیس روی این رایانه نصب است اما فعال‌سازی آن منقضی شده است. می‌توانم تعمیر تأییدشده فعال‌سازی را اجرا کنم؛ چند دقیقه طول می‌کشد و برنامه‌های آفیس بسته می‌شوند.", textDirection: "rtl", state: "Completed", createdAtUtc: minutes(2) },
];

const progress: ProgressEvent[] = [
  ["Queued", "None", 13.5], ["AssistantStarted", "None", 13], ["EndpointCheckStarted", "OfficeInventory", 12],
  ["EndpointCheckCompleted", "OfficeInventory", 11], ["EndpointCheckStarted", "OfficeActivationStatus", 10],
  ["EndpointCheckCompleted", "OfficeActivationStatus", 6], ["AwaitingApproval", "ProposedAction", 2],
].map(([stage, activity, at], index) => ({
  eventId: `p${index}`, conversationId: "c-office", runId: "run-1", sequence: 2 + index * 0.5,
  stage: stage as ProgressEvent["stage"], activity: activity as ProgressEvent["activity"],
  outcome: (stage as string).endsWith("Completed") ? "Success" : "None", createdAtUtc: minutes(at as number),
}));

const action: ActionProposal = {
  actionId: "a1", conversationId: "c-office", sequence: 8, method: "office.activation_repair",
  displayName: "تعمیر فعال‌سازی آفیس", description: "اسکریپت تأییدشده فعال‌سازی را اجرا می‌کند و پس از آن وضعیت را دوباره بررسی می‌کند.",
  parameters: null, createdAtUtc: minutes(1), expiresAtUtc: minutes(-14), state: "Pending",
};

const status: TrayStatus = {
  health: "Ready", service: "Running", label: "متصل و آماده",
  detail: "سرویس پس‌زمینه آماده دریافت عملکردهای تأییدشده پشتیبانی است.",
  runtime: {
    agentId: "agent-7f3a21", machineName: "FIN-PC-014", version: "1.4.0", connectionStatus: "Connected",
    functionMode: "Active", startedAtUtc: minutes(60 * 5), lastConnectedAtUtc: minutes(60 * 5 - 1),
    lastCommandAtUtc: minutes(6), lastCommandMethod: "office.activation_status", lastCommandSucceeded: true,
    activeCommands: 0,
  },
};

const logs: LogEntry[] = [
  { timestampUtc: minutes(1), level: "Information", source: "EndpointAgents.Service", eventId: 0, message: "Completed local Tray operation chat.send with request id 7c1e: Queued." },
  { timestampUtc: minutes(6), level: "Information", source: "EndpointAgents.Service", eventId: 1000, message: "Command office.activation_status completed in 2.4 s." },
  { timestampUtc: minutes(31), level: "Warning", source: "EndpointAgents.Tray", eventId: 0, message: "کانال فرمان سرویس در دسترس نیست و دوباره تلاش می‌شود: IOException" },
  { timestampUtc: minutes(32), level: "Error", source: "EndpointAgents.Service", eventId: 1201, message: "Connection to the control plane failed: The remote host closed the connection." },
  { timestampUtc: minutes(95), level: "Information", source: "EndpointAgents.Tray", eventId: 0, message: "برنامه ناحیه اعلان دستیار پشتیبانی برای نشست کاربر اجرا شد." },
];

export function createMockHost(webview?: { postMessage(m: unknown): void }): Host {
  const forward = (method: Method) => webview?.postMessage({ id: `mock-${method}`, method, params: {} });
  const wait = <T,>(value: T, ms = 220) => new Promise<T>((resolve) => setTimeout(() => resolve(structuredClone(value)), ms));

  return {
    preview: true,
    on: () => () => {},
    async call<T>(method: Method, params?: Record<string, unknown>): Promise<T> {
      switch (method) {
        case "app.ready": {
          forward(method);
          const ready: ReadyInfo = {
            preview: true, userName: "z.ahmadi", machineName: "FIN-PC-014", status, maximized: false, consents: [],
            messageLimit: 200, section: (new URLSearchParams(location.search).get("section") as ReadyInfo["section"]) ?? null,
          };
          return wait(ready as T, 0);
        }
        case "status.refresh": return wait(status as T);
        case "chat.list": return wait(conversations as T);
        case "chat.sync": {
          const id = params?.conversationId as string;
          const after = Number(params?.afterSequence ?? 0);
          const delta: ChatDelta = {
            conversation: { conversationId: id, lastSequence: id === "c-office" ? 9 : 12 },
            messages: id === "c-office" ? messages.filter((m) => m.sequence > after) : after ? [] : [
              { messageId: `${id}-1`, conversationId: id, sequence: 1, role: "User", body: conversations.find((c) => c.conversationId === id)?.title ?? "", textDirection: "rtl", state: "Completed", createdAtUtc: minutes(60 * 26) },
              { messageId: `${id}-2`, conversationId: id, sequence: 2, role: "Assistant", body: conversations.find((c) => c.conversationId === id)?.preview ?? "", textDirection: "rtl", state: "Completed", createdAtUtc: minutes(60 * 25) },
            ],
            actions: id === "c-office" && after < 8 ? [action] : [],
            progressEvents: id === "c-office" ? progress.filter((p) => p.sequence > after) : [],
            visibleItemLimit: 30,
          };
          return wait(delta as T, 120);
        }
        case "chat.send":
          return wait({ conversationId: (params?.conversationId as string) ?? "c-office", messageId: crypto.randomUUID(), sequence: 99, accepted: true, message: "پیام شما ثبت شد." } as T);
        case "chat.action.respond":
          return wait({ accepted: true, message: params?.approved ? "تعمیر آغاز شد." : "اقدام اجرا نمی‌شود.", proposal: { ...action, state: params?.approved ? "Approved" : "Rejected" } } as T);
        case "logs.read": return wait(logs as T);
        case "service.control": return wait({ success: true, message: "عملیات مدیریتی انجام شد.", cancelled: false } as T, 600);
        case "window.minimize": case "window.toggleMaximize": case "window.close": case "window.drag":
        case "keyboard.persian":
          forward(method);
          return true as T;
        default:
          return wait(true as T);
      }
    },
  };
}
