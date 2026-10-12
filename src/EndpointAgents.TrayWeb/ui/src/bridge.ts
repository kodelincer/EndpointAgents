import type { ReadyInfo } from "./types";
import { createMockHost } from "./mock";

// The page talks to the .NET host only through named methods; the host refuses anything else.
export type Method =
  | "app.ready" | "app.exit" | "status.refresh"
  | "chat.list" | "chat.sync" | "chat.send" | "chat.close" | "chat.action.respond"
  | "service.control" | "logs.read" | "logs.export" | "clipboard.write"
  | "consent.answer" | "keyboard.persian"
  | "window.minimize" | "window.toggleMaximize" | "window.close" | "window.drag";

type Listener = (data: any) => void;

interface WebViewHost {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: { data: any }) => void): void;
}

export interface Host {
  call<T = unknown>(method: Method, params?: Record<string, unknown>): Promise<T>;
  on(name: string, listener: Listener): () => void;
  readonly preview: boolean;
}

export class HostError extends Error {
  constructor(public readonly code: string, message: string) {
    super(message);
  }
}

function createWebViewHost(webview: WebViewHost): Host {
  const pending = new Map<string, { resolve: (v: any) => void; reject: (e: Error) => void }>();
  const listeners = new Map<string, Set<Listener>>();
  let counter = 0;

  webview.addEventListener("message", (event) => {
    const message = event.data;
    if (!message || typeof message !== "object") return;
    if (message.type === "response") {
      const waiter = pending.get(message.id);
      if (!waiter) return;
      pending.delete(message.id);
      if (message.ok) waiter.resolve(message.result);
      else waiter.reject(new HostError(message.error?.code ?? "Error", message.error?.message ?? ""));
    } else if (message.type === "event") {
      listeners.get(message.name)?.forEach((listener) => listener(message.data));
    }
  });

  return {
    preview: false,
    call(method, params) {
      const id = `${Date.now().toString(36)}-${(counter++).toString(36)}`;
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        webview.postMessage({ id, method, params: params ?? {} });
      });
    },
    on(name, listener) {
      if (!listeners.has(name)) listeners.set(name, new Set());
      listeners.get(name)!.add(listener);
      return () => listeners.get(name)?.delete(listener);
    },
  };
}

const webview = (window as unknown as { chrome?: { webview?: WebViewHost } }).chrome?.webview;
const previewRequested = new URLSearchParams(location.search).has("preview");

/** The real host inside the Tray, or sample data for `--ui-preview` and `npm run dev`. */
export const host: Host = webview && !previewRequested ? createWebViewHost(webview) : createMockHost(webview);

export type { ReadyInfo };
