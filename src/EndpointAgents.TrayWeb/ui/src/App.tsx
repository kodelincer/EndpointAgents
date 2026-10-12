import { useCallback, useEffect, useState } from "react";
import { Activity, MessagesSquare, ScrollText } from "lucide-react";
import { host } from "./bridge";
import type { ConsentRequest, ReadyInfo, Section, TrayStatus } from "./types";
import { TitleBar } from "./TitleBar";
import { ChatPage } from "./ChatPage";
import { StatusPage } from "./StatusPage";
import { LogsPage } from "./LogsPage";
import { ConsentSheet } from "./ConsentSheet";

const sections: { id: Section; label: string; icon: typeof MessagesSquare }[] = [
  { id: "chat", label: "گفتگوها", icon: MessagesSquare },
  { id: "status", label: "وضعیت", icon: Activity },
  { id: "logs", label: "گزارش‌ها", icon: ScrollText },
];

export function App() {
  const [ready, setReady] = useState<ReadyInfo | null>(null);
  const [status, setStatus] = useState<TrayStatus | null>(null);
  const [section, setSection] = useState<Section>("chat");
  const [visible, setVisible] = useState(true);
  const [maximized, setMaximized] = useState(false);
  const [consents, setConsents] = useState<ConsentRequest[]>([]);

  useEffect(() => {
    const offs = [
      host.on("status", (data: TrayStatus) => setStatus(data)),
      host.on("navigate", (data: { section?: Section | null }) => data.section && setSection(data.section)),
      host.on("window", (data: { visible?: boolean; maximized?: boolean }) => {
        if (typeof data.visible === "boolean") setVisible(data.visible);
        if (typeof data.maximized === "boolean") setMaximized(data.maximized);
      }),
      host.on("consent", (data: ConsentRequest) =>
        setConsents((list) => (list.some((c) => c.id === data.id) ? list : [...list, data]))),
      host.on("consent.withdrawn", (data: { id: string }) =>
        setConsents((list) => list.filter((c) => c.id !== data.id))),
    ];
    host.call<ReadyInfo>("app.ready").then((info) => {
      setReady(info);
      if (info.status) setStatus(info.status);
      if (info.section) setSection(info.section);
      setMaximized(info.maximized);
      setConsents(info.consents ?? []);
    });
    return () => offs.forEach((off) => off());
  }, []);

  const answerConsent = useCallback((id: string, approved: boolean) => {
    setConsents((list) => list.filter((c) => c.id !== id));
    void host.call("consent.answer", { id, approved });
  }, []);

  const connected = status?.runtime?.connectionStatus === "Connected";

  return (
    <div className="shell" data-maximized={maximized || undefined}>
      <TitleBar status={status} maximized={maximized} />
      <div className="body">
        <nav className="rail" aria-label="بخش‌ها">
          {sections.map(({ id, label, icon: Icon }) => (
            <button
              key={id}
              className="rail-item"
              aria-current={section === id ? "page" : undefined}
              onClick={() => setSection(id)}
            >
              <Icon aria-hidden size={21} strokeWidth={1.8} />
              <span>{label}</span>
            </button>
          ))}
          <div className="rail-spacer" />
          {ready && <div className="rail-user" title={ready.machineName}>{ready.userName.slice(0, 2)}</div>}
        </nav>
        <main className="workspace">
          {ready && (
            <>
              <ChatPage
                active={section === "chat" && visible}
                hidden={section !== "chat"}
                connected={connected}
                status={status}
                messageLimit={ready.messageLimit}
              />
              {section === "status" && <StatusPage status={status} />}
              {section === "logs" && <LogsPage active={visible} />}
            </>
          )}
        </main>
      </div>
      {consents[0] && <ConsentSheet request={consents[0]} onAnswer={answerConsent} />}
    </div>
  );
}
